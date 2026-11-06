using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public enum VerificationStatus
{
    VerifiedRemoved,
    PartiallyRemoved,
    StillPresent,
    Unknown
}

public class UninstallService
{
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly TimeSpan ProcessTreeTimeout = TimeSpan.FromMinutes(30);
    private readonly TimeSpan _completionTimeout;
    private readonly string? _vendorLogDirectory;
    private readonly DesktopShortcutCleanupService _desktopShortcuts = new();
    public UninstallService(TimeSpan? completionTimeout = null) => _completionTimeout = completionTimeout ?? ProcessTreeTimeout;
    internal UninstallService(TimeSpan? completionTimeout, string vendorLogDirectory) : this(completionTimeout) => _vendorLogDirectory = vendorLogDirectory;
    internal UninstallService(TimeSpan? completionTimeout, DesktopShortcutCleanupService desktopShortcuts) : this(completionTimeout) => _desktopShortcuts = desktopShortcuts;
    private static UninstallerProcess? _pendingProcess;
    private static InstalledApp? _pendingApp;
    private static bool _pendingRegistration;
    private static bool _pendingIsMsi;
    private static (string AppId, int ExitCode)? _completedEvidence;
    private static readonly MsixDeploymentService Msix = new();
#if ENGINE_TESTS
    internal Func<Task>? BeforeFinalRegistrationValidationAsync { get; set; }
#endif
    // A completed process awaiting a registration recheck is evidence, not a live
    // execution. Keeping it in this barrier deadlocked every later explicit run.
    public static bool HasPendingOperation => _pendingProcess is not null || Msix.HasPendingOperation;

    public record UninstallResult(
        bool Success,
        int ExitCode,
        VerificationStatus Verification,
        string StatusMessage,
        string Error,
        string FailureCode = "None",
        bool ExitObserved = false)
    {
        public int? ObservedExitCode => ExitObserved && FailureCode != "RegistrationAbsent" ? ExitCode : null;
        public string DiagnosticCode => FailureCode == "None" ? "" : FailureCode;
    }

    /// <summary>
    /// Executes the uninstallation for the specified application with proper elevation,
    /// silent/interactive mode selection, progress reporting, and post-uninstall verification.
    /// Prevents concurrent uninstall executions.
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(
        InstalledApp app,
        bool preferQuiet = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IProgress<UninstallOperationState>? state = null)
    {
        var operation = Guid.NewGuid().ToString("N");
        UninstallDiagnostics.Write(operation, app, "Requested", new { quiet = preferQuiet });
        try
        {
            var result = await ExecuteCoreAsync(app, preferQuiet,
                new UninstallDiagnostics.Forward<string>(message => UninstallDiagnostics.Write(operation, app, "Progress", new { message }), progress), ct,
                new UninstallDiagnostics.Forward<UninstallOperationState>(value => UninstallDiagnostics.Write(operation, app, "State", new { state = value.ToString() }), state));
            UninstallDiagnostics.Write(operation, app, "Result", result);
            return result;
        }
        catch (Exception ex)
        {
            UninstallDiagnostics.Write(operation, app, "Exception", new { type = ex.GetType().Name, ex.Message });
            throw;
        }
    }

    private async Task<UninstallResult> ExecuteCoreAsync(InstalledApp app, bool preferQuiet,
        IProgress<string>? progress, CancellationToken ct, IProgress<UninstallOperationState>? state)
    {
        if (ct.IsCancellationRequested)
            return new UninstallResult(false, -2, VerificationStatus.Unknown, "Cancelled before launch.", "No uninstaller was started.", "Cancelled");
        bool acquired;
        try { acquired = await _lock.WaitAsync(100, ct); }
        catch (OperationCanceledException) { return new(false, -2, VerificationStatus.Unknown, "Cancelled before launch.", "No uninstaller was started.", "Cancelled"); }
        if (!acquired)
        {
            return new UninstallResult(
                false, -1, VerificationStatus.Unknown,
                "Concurrent uninstall blocked.",
                "Another uninstallation process is already in progress. Please wait for it to complete.");
        }

        try
        {
            if (_pendingProcess is not null || Msix.HasPendingOperation)
                return new(false, -3, VerificationStatus.Unknown, "Previous uninstaller completion is still uncertain.",
                    "Retry verification or stop for manual review before starting another operation.", "NeedsUserAttention");
            state?.Report(UninstallOperationState.Preparing);
            progress?.Report($"Preparing uninstallation for {app.Name}...");

            // Special handling for MSIX / AppX Store apps
            if (app.InstallerType == InstallType.Msix || app.Source == "MSIX")
            {
                state?.Report(UninstallOperationState.Launching);
                var result = await Msix.RemoveAsync(app, _completionTimeout, ct, progress, state);
                state?.Report(result.Success ? UninstallOperationState.Completed : result.FailureCode == "RegistrationAbsent"
                    ? UninstallOperationState.NotInstalled : result.Verification == VerificationStatus.Unknown
                    ? UninstallOperationState.NeedsUserAttention : UninstallOperationState.Failed);
                return result;
            }

            // A previous item may have removed this component and its uninstaller.
            // Observe the exact identity before checking the stale executable path.
            if (UninstallBroker.IsRegistrationPath(app.RegistryKeyPath))
            {
                var before = UninstallRegistration.Read(app.RegistryKeyPath);
                if (before.State == UninstallRegistration.Presence.Absent) return AlreadyAbsentResult();
                if (before.State == UninstallRegistration.Presence.Unknown)
                    return new(false, -1, VerificationStatus.Unknown, "The current uninstall registration could not be verified.", before.Error, "RegistrationUnavailable");
            }
            RegisteredUninstallCommand.LaunchCommand command;
            try { command = RegisteredUninstallCommand.PrepareLaunch(app, preferQuiet); }
            catch (RegisteredUninstallCommand.LaunchValidationException ex)
            {
                if (ex.FailureCode == "MissingExecutable" && UninstallRegistration.Read(app.RegistryKeyPath).State == UninstallRegistration.Presence.Absent)
                    return AlreadyAbsentResult();
                return new(false, -1, VerificationStatus.StillPresent, ex.Message, ex.Detail, ex.FailureCode);
            }
            var exe = command.Executable;
            var args = command.Arguments;
            var isMsi = command.IsMsi;
            var validation = ValidateCurrentLaunch(app, command, preferQuiet, out var current);
            if (validation is not null) return validation;
            const bool registrationWasPresent = true;

            int exitCode;
            var launchedAt = DateTimeOffset.MinValue;
            var launchedProcessId = 0;
            var requiresElevation = false;
            DesktopShortcutCleanupService.Snapshot? desktopSnapshot = null;
            UninstallerProcess? execution = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                desktopSnapshot = await Task.Run(() => _desktopShortcuts.Capture(current!));
                ct.ThrowIfCancellationRequested();
#if ENGINE_TESTS
                if (BeforeFinalRegistrationValidationAsync is not null)
                    await BeforeFinalRegistrationValidationAsync();
#endif
                // Capture is optional pre-launch housekeeping and may take time.
                // Re-read the exact registration after it, immediately before
                // CreateProcess or the independently validating elevated broker.
                validation = ValidateCurrentLaunch(app, command, preferQuiet, out current);
                if (validation is not null) return validation;
                requiresElevation = RegisteredUninstallCommand.RequiresElevation(current!, command);
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = requiresElevation,
                    Verb = requiresElevation ? "runas" : string.Empty,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.SystemDirectory
                };
                progress?.Report($"Launching uninstaller for {app.Name}...");
                state?.Report(UninstallOperationState.Launching);
                launchedAt = DateTimeOffset.Now;
                execution = isMsi ? UninstallerProcess.Start(psi)
                    : await UninstallerProcess.StartRegisteredAsync(psi, app.RegistryKeyPath, preferQuiet, ct,
                        new(app.RegistryKeyPath, current!.Name, current.InstallLocation, exe));
                _completedEvidence = null; // Superseded only after the new explicit launch.
                var proc = execution.Root;
                launchedProcessId = execution.ExecutionId;
                state?.Report(UninstallOperationState.Running);

                progress?.Report($"Uninstaller running (PID: {execution.ExecutionId}). Waiting for completion...");

                // Do not cancel or kill third-party uninstallers after launch. Some launch
                // a worker and exit immediately, so wait for the observed process tree too.
                state?.Report(UninstallOperationState.AwaitingCompletion);
                var treeCompleted = await execution.WaitAsync(_completionTimeout, progress);
                // System msiexec /X has synchronous completion semantics even though the
                // Windows Installer service is outside its process tree. Registry verification
                // below is still mandatory; 1618/1603 cannot become success.
                if (!treeCompleted && !(isMsi && proc.HasExited))
                {
                    _pendingProcess = execution;
                    _pendingApp = app;
                    _pendingRegistration = registrationWasPresent;
                    _pendingIsMsi = isMsi;
                    execution = null;
                    state?.Report(UninstallOperationState.NeedsUserAttention);
                    // The broker's own exit code is not the uninstaller's exit.
                    var timeoutCode = _pendingProcess.ObservedExitCode ?? -3;
                    return new UninstallResult(false, timeoutCode, VerificationStatus.Unknown,
                        "Uninstaller completion could not be established confidently.",
                        "A related process may still be active or elevated process tracking is incomplete. The batch is paused for review.", "NeedsUserAttention",
                        _pendingProcess.ObservedExitCode.HasValue);
                }

                exitCode = execution.ObservedExitCode ?? throw new InvalidOperationException("The completed process lost its exit evidence.");
                progress?.Report($"Uninstaller exited with code {exitCode}.");
            }
            catch (UninstallBroker.BrokerRejectedException ex)
            {
                return new(false, -1, VerificationStatus.StillPresent, "Uninstall launch was rejected.", ex.Message, "BrokerRejected");
            }
            catch (Win32Exception w32Ex) when (execution is null && requiresElevation && w32Ex.NativeErrorCode == 1223) // ERROR_CANCELLED by user at UAC prompt
            {
                return new UninstallResult(
                    false, 1223, VerificationStatus.StillPresent,
                    "UAC Prompt was cancelled by user.",
                    "Administrative privileges were denied at the UAC prompt. No uninstaller was started.", "UacDenied");
            }
            catch (OperationCanceledException) when (execution is null)
            {
                return new UninstallResult(false, -2, VerificationStatus.Unknown, "Cancelled by user.", "Uninstallation cancelled.", "Cancelled");
            }
            catch (Exception ex)
            {
                if (execution is not null)
                {
                    _pendingProcess = execution; _pendingApp = app;
                    _pendingRegistration = registrationWasPresent; _pendingIsMsi = isMsi; execution = null;
                    return new UninstallResult(false, -1, VerificationStatus.Unknown, "Process launch error.", ex.Message, "NeedsUserAttention");
                }
                // No process object exists, so nothing was started: this is a definite
                // launch failure, not an unresolved operation that must pause the queue.
                var native = ex as Win32Exception ?? ex.InnerException as Win32Exception;
                var launchCode = native?.NativeErrorCode switch { 740 => "ElevationRequired", 5 => "LaunchAccessDenied", _ => "LaunchFailed" };
                return new UninstallResult(false, -1, VerificationStatus.StillPresent, "Process launch error.",
                    native is null ? ex.Message : $"{ex.Message} (Windows error {native.NativeErrorCode}). No uninstaller was started.", launchCode);
            }
            finally { execution?.Dispose(); }

            // Post-uninstall verification
            progress?.Report("Verifying uninstallation status in Windows Registry and file system...");
            state?.Report(UninstallOperationState.Verifying);
            var verification = registrationWasPresent
                ? await UninstallRegistration.VerifyRemovalAsync(app.RegistryKeyPath)
                : VerificationStatus.Unknown;

            // The job/synchronous MSI boundary has completed. A retained
            // registration is a finite "removal not observed" result, not an
            // indefinitely running process. Keep read-only retry evidence.
            if (exitCode is 0 or 3010 && verification == VerificationStatus.StillPresent)
            {
                _completedEvidence = (app.Id, exitCode);
            }

            var completed = InterpretCompletedProcessResult(exitCode, verification, isMsi);
            if (completed.Success && desktopSnapshot is not null)
            {
                var shortcuts = await Task.Run(() => _desktopShortcuts.Complete(desktopSnapshot, completed.Success, completed.Verification));
                if (shortcuts.Removed > 0) progress?.Report($"Removed orphaned desktop shortcuts: {shortcuts.Removed}.");
                if (shortcuts.Kept > 0) progress?.Report("Some desktop shortcuts were kept for review.");
            }
            if (!completed.Success && exitCode == 0 && verification == VerificationStatus.StillPresent &&
                VendorUninstallDiagnostics.HasRiotDependencyFailure(app, exe, launchedProcessId, launchedAt, _vendorLogDirectory))
                completed = completed with { StatusMessage = VendorUninstallDiagnostics.RiotDependencyMessage,
                    Error = VendorUninstallDiagnostics.RiotDependencyAdvice, FailureCode = "VendorDependenciesInstalled" };
            state?.Report(completed.Success ? UninstallOperationState.Completed : verification == VerificationStatus.Unknown
                ? UninstallOperationState.NeedsUserAttention : UninstallOperationState.Failed);
            return completed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static UninstallResult? ValidateCurrentLaunch(InstalledApp confirmed,
        RegisteredUninstallCommand.LaunchCommand expected, bool preferQuiet, out InstalledApp? current)
    {
        current = null;
        var registration = UninstallRegistration.Read(confirmed.RegistryKeyPath);
        if (registration.State == UninstallRegistration.Presence.Absent) return AlreadyAbsentResult();
        if (registration.State == UninstallRegistration.Presence.Unknown)
            return new(false, -1, VerificationStatus.Unknown, "The current uninstall registration could not be verified.",
                registration.Error, "RegistrationUnavailable");
        try
        {
            current = RegistryDiscoveryService.ReadExactRegistration(confirmed.RegistryKeyPath);
            var fresh = current is null ? null : RegisteredUninstallCommand.PrepareLaunch(current, preferQuiet);
            if (fresh is null || fresh.IsMsi != expected.IsMsi ||
                !fresh.Executable.Equals(expected.Executable, StringComparison.OrdinalIgnoreCase) || fresh.Arguments != expected.Arguments)
                return new(false, -1, VerificationStatus.StillPresent, "The registered uninstall command changed after confirmation.",
                    "No uninstaller was launched. Refresh the application list and review the current command.", "RegistrationChanged");
            return null;
        }
        catch (Exception ex)
        {
            current = null;
            return new(false, -1, VerificationStatus.Unknown, "The current uninstall registration could not be verified.",
                ex.Message, "RegistrationUnavailable");
        }
    }

    /// <summary>Read-only recheck; never relaunches an uninstaller.</summary>
    public async Task<UninstallResult> RetryVerificationAsync(InstalledApp app)
    {
        await _lock.WaitAsync();
        try
        {
            if (Msix.HasPendingOperation && (app.Source == "MSIX" || app.InstallerType == InstallType.Msix))
                return await Msix.RetryAsync(app);
            if (_pendingProcess is null && _completedEvidence?.AppId == app.Id)
            {
                var evidence = _completedEvidence.Value;
                var verified = await UninstallRegistration.VerifyRemovalAsync(app.RegistryKeyPath);
                if (verified is VerificationStatus.VerifiedRemoved or VerificationStatus.PartiallyRemoved)
                {
                    _completedEvidence = null;
                    return new(true, evidence.ExitCode, verified, "Application registration was removed.", "", ExitObserved: true);
                }
                return InterpretCompletedProcessResult(evidence.ExitCode, verified, false);
            }
            if (_pendingProcess is null || _pendingApp?.Id != app.Id)
                return new(false, -3, VerificationStatus.Unknown, "No reliable live process evidence is available.", "Review the interrupted operation manually.", "NeedsUserAttention");
            var observedExit = _pendingProcess.ObservedExitCode;
            // A read-only retry must re-evaluate an external browser handoff if
            // an earlier timeout/metadata restriction left the live job pending.
            // This never starts a second uninstaller or closes the browser.
            if (!_pendingIsMsi && observedExit == 0 && _pendingProcess.HasReliableTree && !_pendingProcess.IsRemovalBoundaryComplete)
                await _pendingProcess.WaitAsync(TimeSpan.FromSeconds(4), null, checkHandoffImmediately: true);
            if (!CanFinalizeObservedProcess(_pendingIsMsi, _pendingProcess.HasReliableTree,
                _pendingProcess.HasReliableTree && _pendingProcess.IsRemovalBoundaryComplete, observedExit))
                return IncompleteProcessResult(!_pendingProcess.HasReliableTree, observedExit);
            var code = observedExit!.Value;
            var verification = app.Source == "MSIX" ? (await MsixDeploymentService.QueryAsync(app.Id)).Verification
                : _pendingRegistration ? await UninstallRegistration.VerifyRemovalAsync(app.RegistryKeyPath) : VerificationStatus.Unknown;
            _pendingProcess.Dispose(); _pendingProcess = null; _pendingApp = null;
            // Retain an observed successful exit if its registration has not
            // settled yet. Otherwise the next retry loses all process evidence.
            if (code is 0 or 3010 && verification == VerificationStatus.StillPresent)
            {
                _completedEvidence = (app.Id, code);
            }
            return InterpretCompletedProcessResult(code, verification, _pendingIsMsi);
        }
        finally { _lock.Release(); }
    }

    // Only our normalized system msiexec /X launch has synchronous MSI semantics.
    // Other EXEs require an authenticated removal boundary: an empty job, or a
    // verified external browser handoff after root exit zero and registration absence.
    internal static bool CanFinalizeObservedProcess(bool normalizedMsi, bool reliableTree, bool treeComplete, int? exitCode) =>
        exitCode.HasValue && (normalizedMsi || reliableTree && treeComplete);

    // Called only after explicit acknowledgment of that stopped review. Never
    // discard an active tree, failed IPC, a different identity or an MSIX task.
    public bool TryReleaseCompletedReview(InstalledApp app)
    {
        if (!_lock.Wait(0)) return false;
        try
        {
            if (Msix.HasPendingOperation && !Msix.TryReleaseCompletedReview(app)) return false;
            if (_pendingProcess is not null)
            {
                if (_pendingApp?.Id != app.Id || !CanFinalizeObservedProcess(_pendingIsMsi,
                    _pendingProcess.HasReliableTree, _pendingProcess.HasReliableTree && _pendingProcess.IsRemovalBoundaryComplete,
                    _pendingProcess.ObservedExitCode)) return false;
                _pendingProcess.Dispose(); _pendingProcess = null; _pendingApp = null;
            }
            if (_completedEvidence?.AppId == app.Id) _completedEvidence = null;
            return true;
        }
        catch { return false; } // Missing process evidence must retain the barrier.
        finally { _lock.Release(); }
    }

    internal static UninstallResult IncompleteProcessResult(bool brokered, int? exitCode) => new(false, exitCode ?? -3,
        VerificationStatus.Unknown, "Uninstaller completion is still uncertain.", brokered
            ? "Reliable tracking of this elevated uninstaller's full process tree is unavailable. Current registration is checked separately; no further uninstall will start automatically."
            : "A related process is still running. No additional application will be started.",
        brokered ? "UntrackedElevation" : "NeedsUserAttention", exitCode.HasValue);

    // Interprets an observed exit only. Never changes registry/file evidence or
    // turns "unknown product" into a removal performed by this application.
    internal static UninstallResult InterpretCompletedProcessResult(int exitCode, VerificationStatus verification, bool isMsi)
        => InterpretCore(exitCode, verification, isMsi) with { ExitObserved = true };

    private static UninstallResult InterpretCore(int exitCode, VerificationStatus verification, bool isMsi)
    {
        if (exitCode is not (0 or 3010))
        {
            // A vendor EXE owns its exit-code meaning. Values 5/740 alone do not
            // establish an access/elevation failure; native launch errors do.
            if (isMsi && exitCode == 1605)
                return new(false, exitCode, verification,
                    "Windows Installer does not recognize the registered product.",
                    "The registered MSI product code was not found in this installation context. The entry may be stale or refer to another product or user context. No successful removal was verified.",
                    "MsiUnknownProduct");
            return new(false, exitCode, verification, "Uninstaller returned a failure exit code.",
                isMsi && exitCode == 1618 ? "Windows Installer is busy. Retry after the other installation completes." : $"Exit code: {exitCode}",
                isMsi && exitCode == 1618 ? "MsiBusy" : isMsi && exitCode == 1603 ? "MsiInstallFailure" : "UninstallerExitError");
        }
        if (verification == VerificationStatus.StillPresent)
            return new(false, exitCode, verification,
                exitCode == 3010 ? "The uninstaller requested a restart; the installation registration is still present."
                    : "The uninstaller finished, but removal was not observed.",
                "The installation registration is still present. No successful removal was verified. Remaining queue items were not started automatically.",
                exitCode == 3010 ? "RestartPending" : "RemovalNotObserved");
        var success = verification is VerificationStatus.VerifiedRemoved or VerificationStatus.PartiallyRemoved;
        var status = verification switch
        {
            VerificationStatus.VerifiedRemoved when exitCode == 3010 => "Uninstaller requested a reboot; removal was verified, but restart may still be required.",
            VerificationStatus.VerifiedRemoved => "Application registration was removed.",
            VerificationStatus.PartiallyRemoved => "Application was uninstalled, but leftover files/registry keys were detected.",
            VerificationStatus.StillPresent => "Application still appears registered in the system.",
            _ => exitCode == 3010 ? "Restart required; removal cannot yet be verified." : $"Removal could not be verified (exit code {exitCode})."
        };
        return new(success, exitCode, verification, status, success ? "" : $"Exit code: {exitCode}", success ? "None" : "RemovalUnverified");
    }

    internal static UninstallResult AlreadyAbsentResult() => new(false, -1, VerificationStatus.VerifiedRemoved,
        "The installation registration is already absent.",
        "No uninstaller was launched for this item. It was skipped because its exact registration is already absent.", "RegistrationAbsent");

    internal static string FormatResultEvidence(UninstallResult result) => string.Join("\n",
        new[] { result.StatusMessage, result.DiagnosticCode, result.Error, result.ObservedExitCode is int exitCode ? FormattableString.Invariant($"Exit code: {exitCode}") : "" }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));

}
