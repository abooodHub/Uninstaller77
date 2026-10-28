using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>AppX cmdlets are synchronous deployment operations. Require a matching
/// terminal receipt and a separate package-state query; process exit alone is insufficient.</summary>
public sealed class MsixDeploymentService
{
    public sealed record PackageState(VerificationStatus Verification, string Error, string FailureCode, long ElapsedMs);
    public sealed record Receipt(string OperationId, string Package, string State, string Error, string HResult);
    private sealed record Pending(InstalledApp App, string OperationId, Process Process, Task<string> Output, Task<string> Errors);
    private Pending? _pending;
    public bool HasPendingOperation => _pending is not null;
    internal static bool CanReleaseReview(string expectedId, string pendingId, bool processExited, bool outputCompleted, bool errorsCompleted) =>
        string.Equals(expectedId, pendingId, StringComparison.Ordinal) && processExited && outputCompleted && errorsCompleted;

    // Explicit acknowledgment retires a finished deployment worker's uncertain
    // receipt without declaring removal successful or touching a package.
    public bool TryReleaseCompletedReview(InstalledApp app)
    {
        if (_pending is null) return true;
        try
        {
            if (!CanReleaseReview(app.Id, _pending.App.Id, _pending.Process.HasExited,
                _pending.Output.IsCompletedSuccessfully, _pending.Errors.IsCompletedSuccessfully)) return false;
            _pending.Process.Dispose(); _pending = null;
            return true;
        }
        catch { return false; }
    }
    public static bool ValidIdentity(string identity) => Regex.IsMatch(identity, @"^[A-Za-z0-9_.~+-]+$");

    public static Receipt? ParseReceipt(string output, string operationId, string package)
    {
        var line = output.Split('\n').LastOrDefault(row => row.StartsWith("U77_RESULT:", StringComparison.Ordinal));
        if (line is null) return null;
        try
        {
            using var document = JsonDocument.Parse(line[11..]);
            var root = document.RootElement;
            string Read(string name) => root.TryGetProperty(name, out var value) ? value.ToString() : "";
            if (Read("operationId") != operationId || Read("package") != package) return null;
            var state = Read("state");
            return state is "Removed" or "Failed" or "Protected" or "Absent" or "Present"
                ? new(operationId, package, state, Read("error"), Read("hresult")) : null;
        }
        catch (JsonException) { return null; }
    }

    public static UninstallService.UninstallResult Evaluate(int exitCode, Receipt? receipt, PackageState query, string stderr)
        => EvaluateCore(exitCode, receipt, query, stderr) with { ExitObserved = true };

    private static UninstallService.UninstallResult EvaluateCore(int exitCode, Receipt? receipt, PackageState query, string stderr)
    {
        var details = string.Join("\n", new[] { receipt?.Error, receipt?.HResult, stderr, query.Error }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (query.Verification == VerificationStatus.Unknown)
            return new(false, exitCode, VerificationStatus.Unknown, "MSIX verification query failed.", details, query.FailureCode);
        if (exitCode == 0 && receipt?.State == "Removed" && query.Verification == VerificationStatus.VerifiedRemoved)
            return new(true, exitCode, VerificationStatus.VerifiedRemoved, "MSIX package removal was verified.", "");
        if (exitCode == 0 && receipt?.State == "Removed" && query.Verification != VerificationStatus.VerifiedRemoved)
            return new(false, exitCode, VerificationStatus.Unknown, "MSIX deployment and verification did not agree.", details, "MsixVerificationMismatch");
        if (receipt is null)
            return new(false, exitCode, VerificationStatus.Unknown, "MSIX completion receipt was missing or invalid.", details, "MissingMsixReceipt");
        var message = receipt.State == "Protected" ? "This Windows package does not support removal."
            : receipt.State == "Absent" ? "The package was already absent; no removal was performed."
            : "MSIX package removal failed.";
        return new(false, exitCode, query.Verification, message, details,
            receipt.State == "Protected" ? "ProtectedPackage" : "MsixDeploymentFailed");
    }

    public async Task<UninstallService.UninstallResult> RemoveAsync(InstalledApp app, TimeSpan timeout,
        CancellationToken cancellation, IProgress<string>? progress, IProgress<UninstallOperationState>? state)
    {
        if (!ValidIdentity(app.Id)) return new(false, -1, VerificationStatus.StillPresent, "Invalid MSIX identity.", "Package identity is invalid.", "InvalidMsixIdentity");
        if (_pending is not null) return new(false, -3, VerificationStatus.Unknown, "Previous MSIX removal requires review.", "", "NeedsUserAttention");
        var operation = Guid.NewGuid().ToString("N");
        var script = $$"""
            $ErrorActionPreference='Stop'
            function Complete-U77($state,$errorText,$errorCode,$exitCode) {
              $result=@{operationId='{{operation}}';package='{{app.Id}}';state=$state;error=$errorText;hresult=$errorCode}
              [Console]::Out.WriteLine('U77_RESULT:' + ($result | ConvertTo-Json -Compress))
              exit $exitCode
            }
            try {
              $installed=@(Get-AppxPackage -ErrorAction Stop | Where-Object PackageFullName -EQ '{{app.Id}}')
              if ($installed.Count -eq 0) { Complete-U77 'Absent' '' '' 2 }
              if ($installed[0].NonRemovable) { Complete-U77 'Protected' 'Windows marks this package NonRemovable.' '' 5 }
              Remove-AppxPackage -Package '{{app.Id}}' -ErrorAction Stop
              if (@(Get-AppxPackage -ErrorAction Stop | Where-Object PackageFullName -EQ '{{app.Id}}').Count -ne 0) {
                Complete-U77 'Present' 'Package registration remains present after deployment.' '' 2
              }
              Complete-U77 'Removed' '' '' 0
            } catch {
              Complete-U77 'Failed' $_.Exception.Message ([string]$_.Exception.HResult + ' / ' + $_.FullyQualifiedErrorId) 1
            }
            """;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var process = Process.Start(Command(script)) ?? throw new InvalidOperationException("PowerShell did not start.");
            _pending = new(app, operation, process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            Log(app, operation, "Launched", process.Id, "");
            state?.Report(UninstallOperationState.AwaitingCompletion);
            progress?.Report($"Removing MSIX Package '{app.Name}'...");
            try { await process.WaitForExitAsync().WaitAsync(timeout); }
            catch (TimeoutException)
            {
                return new(false, -3, VerificationStatus.Unknown, "MSIX removal is still awaiting completion.",
                    $"PID: {process.Id}. The deployment process was not terminated.", "MsixDeploymentTimeout");
            }
            state?.Report(UninstallOperationState.Verifying);
            return await CompletePendingAsync();
        }
        catch (OperationCanceledException) { return new(false, -2, VerificationStatus.StillPresent, "Cancelled before launch.", "No uninstaller was started.", "Cancelled"); }
        catch (Exception ex)
        {
            Log(app, operation, "Exception", _pending?.Process.Id, ex.ToString());
            return new(false, -1, _pending is null ? VerificationStatus.StillPresent : VerificationStatus.Unknown,
                _pending is null ? "MSIX process could not be launched." : "MSIX removal exception.", ex.Message,
                _pending is null ? "MsixLaunchFailed" : "MsixDeploymentException");
        }
    }

    public async Task<UninstallService.UninstallResult> RetryAsync(InstalledApp app)
    {
        if (_pending?.App.Id != app.Id)
            return new(false, -3, VerificationStatus.Unknown, "No reliable live process evidence is available.", "Review the interrupted operation manually.", "NeedsUserAttention");
        if (!_pending.Process.HasExited)
            return new(false, -3, VerificationStatus.Unknown, "MSIX removal is still awaiting completion.", $"PID: {_pending.Process.Id}", "MsixDeploymentTimeout");
        return await CompletePendingAsync();
    }

    private async Task<UninstallService.UninstallResult> CompletePendingAsync()
    {
        var pending = _pending!;
        var output = await pending.Output.WaitAsync(TimeSpan.FromSeconds(5));
        var errors = await pending.Errors.WaitAsync(TimeSpan.FromSeconds(5));
        var receipt = ParseReceipt(output, pending.OperationId, pending.App.Id);
        var query = await QueryAsync(pending.App.Id);
        var result = Evaluate(pending.Process.ExitCode, receipt, query, errors);
        Log(pending.App, pending.OperationId, result.FailureCode, pending.Process.Id,
            JsonSerializer.Serialize(new { result, stdout = output, stderr = errors, query }));
        if (result.Verification != VerificationStatus.Unknown)
        { pending.Process.Dispose(); _pending = null; }
        return result;
    }

    public static async Task<PackageState> QueryAsync(string package, TimeSpan? timeout = null)
    {
        if (!ValidIdentity(package)) return new(VerificationStatus.Unknown, "Invalid package identity.", "InvalidMsixIdentity", 0);
        var clock = Stopwatch.StartNew();
        try
        {
            var script = $$"""
                $ErrorActionPreference='Stop'
                try {
                  $items=@(Get-AppxPackage -ErrorAction Stop | Where-Object PackageFullName -EQ '{{package}}')
                  @{package='{{package}}';exists=($items.Count -ne 0)} | ConvertTo-Json -Compress
                  exit 0
                } catch { [Console]::Error.WriteLine($_.Exception.Message + ' / ' + $_.FullyQualifiedErrorId); exit 1 }
                """;
            using var process = Process.Start(Command(script));
            if (process is null) return new(VerificationStatus.Unknown, "Package query did not start.", "MsixQueryLaunchFailed", clock.ElapsedMilliseconds);
            var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(45)); }
            catch (TimeoutException) { return new(VerificationStatus.Unknown, "The read-only package query timed out.", "MsixQueryTimeout", clock.ElapsedMilliseconds); }
            var output = await outputTask; var errors = await errorTask;
            if (process.ExitCode != 0) return new(VerificationStatus.Unknown, errors, "MsixQueryFailed", clock.ElapsedMilliseconds);
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.GetProperty("package").GetString() != package)
                return new(VerificationStatus.Unknown, "Package query identity mismatch.", "MsixQueryIdentityMismatch", clock.ElapsedMilliseconds);
            return new(document.RootElement.GetProperty("exists").GetBoolean() ? VerificationStatus.StillPresent : VerificationStatus.VerifiedRemoved,
                errors, "None", clock.ElapsedMilliseconds);
        }
        catch (Exception ex) { return new(VerificationStatus.Unknown, ex.Message, "MsixQueryException", clock.ElapsedMilliseconds); }
    }

    private static ProcessStartInfo Command(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add(script);
        return start;
    }
    private static void Log(InstalledApp app, string operationId, string stage, int? pid, string detail)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "msix-deployment.jsonl"), JsonSerializer.Serialize(new
            { timestamp = DateTimeOffset.UtcNow, operationId, appId = app.Id, appName = app.Name, stage, pid, detail }) + Environment.NewLine);
        }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
    }
}
