using WinUninstaller.Models;

namespace WinUninstaller.Services;

public enum BatchItemState { Succeeded, Failed, Skipped, NeedsAttention, Cancelled }
public enum BatchQueueState { Running, Succeeded, PartialFailure, CriticalFailure, Stopped, NeedsUserAttention, CompletedWithSkips }

public sealed record BatchItemResult(InstalledApp App, BatchItemState State, string Message, int? ExitCode = null, string FailureCode = "");
public sealed record BatchRunResult(IReadOnlyList<BatchItemResult> Items, bool Stopped, string? CriticalError = null)
{
    public bool CriticalFailure => CriticalError is not null;
    public int Processed => Items.Count(i => i.State != BatchItemState.Skipped);
    public int Successful => Items.Count(i => i.State == BatchItemState.Succeeded);
    public int Failed => Items.Count(i => i.State == BatchItemState.Failed);
    public int Skipped => Items.Count(i => i.State == BatchItemState.Skipped);
    public bool NeedsUserAttention => Items.Any(i => i.State == BatchItemState.NeedsAttention);
    public BatchQueueState State => CriticalFailure ? BatchQueueState.CriticalFailure
        : NeedsUserAttention ? BatchQueueState.NeedsUserAttention
        : Failed > 0 ? BatchQueueState.PartialFailure
        : Stopped ? BatchQueueState.Stopped : Skipped > 0 ? BatchQueueState.CompletedWithSkips : BatchQueueState.Succeeded;
}

public sealed class BatchHistoryWriteException(InstalledApp app, UninstallService.UninstallResult engineResult, Exception inner)
    : Exception($"The engine result for {app.Name} was obtained, but its history row could not be saved: {inner.Message}", inner)
{
    public InstalledApp App { get; } = app;
    public UninstallService.UninstallResult EngineResult { get; } = engineResult;
}

/// <summary>Runs a queue against an injected executor; safe to exercise with synthetic apps in tests.</summary>
public static class BatchUninstallCoordinator
{
    private static readonly SemaphoreSlim ExecutionLock = new(1, 1);

    public static async Task<BatchRunResult> RunAsync(
        IReadOnlyList<InstalledApp> queue,
        Func<InstalledApp, Task<UninstallService.UninstallResult>> execute,
        Func<bool> stopAfterCurrent)
    {
        if (!await ExecutionLock.WaitAsync(0))
            throw new InvalidOperationException("A batch uninstall queue is already active.");

        try
        {
        var results = new List<BatchItemResult>(queue.Count);
        for (var index = 0; index < queue.Count; index++)
        {
            if (stopAfterCurrent())
            {
                for (var rest = index; rest < queue.Count; rest++)
                    results.Add(new(queue[rest], BatchItemState.Skipped, "Skipped after stop request."));
                return new(results, true);
            }
            var app = queue[index];
            try
            {
                var result = await execute(app);
                var item = ToItemResult(app, result);
                results.Add(item);

                if (item.State == BatchItemState.Cancelled)
                {
                    for (var rest = index + 1; rest < queue.Count; rest++)
                        results.Add(new(queue[rest], BatchItemState.Skipped, "Not started after cancellation."));
                    return new(results, true);
                }

                if (item.State == BatchItemState.NeedsAttention)
                {
                    for (var rest = index + 1; rest < queue.Count; rest++)
                        results.Add(new(queue[rest], BatchItemState.Skipped, "Not started; the previous uninstall needs verification."));
                    return new(results, false);
                }
                // A completed, vendor-declared refusal is a per-application
                // failure. Other explicitly confirmed items may continue.
                // Unknown completion still pauses above; generic no-op and
                // pending restart retain their conservative stop.
                if (result.FailureCode is "RemovalNotObserved" or "RestartPending")
                {
                    for (var rest = index + 1; rest < queue.Count; rest++)
                        results.Add(new(queue[rest], BatchItemState.Skipped, "Not started after a completed uninstaller left its registration present."));
                    return new(results, true);
                }
            }
            catch (BatchHistoryWriteException ex)
            {
                var item = ToItemResult(ex.App, ex.EngineResult);
                results.Add(item with { Message = item.Message + " — History write failed." });
                for (var rest = index + 1; rest < queue.Count; rest++)
                    results.Add(new(queue[rest], BatchItemState.Skipped, "Not started after a critical history error."));
                return new(results, false, ex.Message);
            }
            catch (Exception ex)
            {
                results.Add(new(app, BatchItemState.Failed, ex.Message));
                for (var rest = index + 1; rest < queue.Count; rest++)
                    results.Add(new(queue[rest], BatchItemState.Skipped, "Not started after a critical queue error."));
                return new(results, false, ex.Message);
            }

            if (stopAfterCurrent() && index + 1 < queue.Count)
            {
                for (var rest = index + 1; rest < queue.Count; rest++)
                    results.Add(new(queue[rest], BatchItemState.Skipped, "Skipped after stop request."));
                return new(results, true);
            }
        }
        return new(results, false);
        }
        finally
        {
            ExecutionLock.Release();
        }
    }

    internal static BatchItemResult ToItemResult(InstalledApp app, UninstallService.UninstallResult result) =>
        new(app, Classify(result),
            string.IsNullOrWhiteSpace(result.Error) ? result.StatusMessage : $"{result.StatusMessage} — {result.Error}",
            result.ObservedExitCode, result.DiagnosticCode);

    public static BatchItemState Classify(UninstallService.UninstallResult result) =>
        // Cancellation is identified by its own failure code. A raw exit value such as
        // 1223 or -2 from an uninstaller that actually ran is that vendor's failure.
        result.FailureCode is "UacDenied" or "Cancelled" ? BatchItemState.Cancelled
        : !result.Success && result.FailureCode == "RegistrationAbsent" && result.Verification == VerificationStatus.VerifiedRemoved
            ? BatchItemState.Skipped
        : result.Success && result.Verification is VerificationStatus.VerifiedRemoved or VerificationStatus.PartiallyRemoved
            ? BatchItemState.Succeeded
        : result.FailureCode is "UninstallerExitError" or "MsiUnknownProduct" or "MsiBusy" or "MsiInstallFailure" or "RegistrationChanged" or "RegistrationUnavailable"
            or "InsufficientPrivileges" or "AccessDenied" or "ElevationRequired" or "LaunchAccessDenied" or "LaunchFailed"
            ? BatchItemState.Failed
        : result.Verification == VerificationStatus.Unknown ? BatchItemState.NeedsAttention : BatchItemState.Failed;
}
