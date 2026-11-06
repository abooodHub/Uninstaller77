using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

internal sealed record ProtectionStep(string Status, string Reason = "");
internal sealed record RegistryBackup(string App, ProtectionStep Result, string? File = null);
internal sealed record QueueProtectionReport(ProtectionStep Restore, RegistryBackup[] Backups, string? PointName = null)
{
    internal string RestoreText(string language) => Text(Restore, language);
    internal bool HasFailure => Restore.Status == "Failed" || Backups.Any(item => item.Result.Status == "Failed");

    internal string RegistryText(string language)
    {
        var succeeded = Backups.Count(item => item.Result.Status == "Succeeded");
        var failed = Backups.Count(item => item.Result.Status == "Failed");
        var disabled = Backups.Length - succeeded - failed;
        return string.Format(UiStrings.Get(language, "protectionExportCounts"), succeeded, failed, disabled);
    }

    internal string Details(string language) => string.Join(Environment.NewLine, new[] { PointName }.Where(name => name is not null).Concat(Backups.Select(item =>
        $"{item.App} — {Text(item.Result, language)}{(item.File is null ? "" : Environment.NewLine + item.File)}")));

    private static string Text(ProtectionStep step, string language) => UiStrings.Get(language, "protection" + step.Status) +
        (string.IsNullOrEmpty(step.Reason) ? "" : " — " + UiStrings.Get(language, "protectionReason" + step.Reason));

    internal static QueueProtectionReport? Read(string notes)
    {
        try
        {
            var report = JsonSerializer.Deserialize<QueueProtectionReport>(notes);
            return report?.Restore is not null && report.Backups is not null && report.Backups.All(item => item?.Result is not null) ? report : null;
        }
        catch (JsonException) { return null; }
    }
}

internal sealed class QueueProtectionService(string historyDirectory,
    Func<string, Task<ProtectionStep>>? createRestore = null, Func<string, string, Task<bool>>? export = null)
{
    private readonly Func<string, Task<ProtectionStep>> _createRestore = createRestore ?? CreateRestorePointAsync;
    private readonly Func<string, string, Task<bool>> _export = export ?? ExportRegistryAsync;

    internal async Task<QueueProtectionReport> PrepareAsync(string queueId, DateTimeOffset started, IReadOnlyList<InstalledApp> apps,
        bool enabled, Func<bool> stopRequested)
    {
        if (!Guid.TryParseExact(queueId, "N", out _)) throw new ArgumentException("Invalid queue identity.", nameof(queueId));
        var reason = enabled ? "Stopped" : "SettingOff";
        var restore = new ProtectionStep("Disabled", reason);
        string? pointName = null;
        if (enabled && !stopRequested())
        {
            pointName = FormattableString.Invariant($"Uninstaller 77 {started:yyyy-MM-dd HH:mm:ss zzz} ({apps.Count}) {queueId[..8]}");
            try { restore = await _createRestore(pointName); }
            catch (Exception ex) { Debug.WriteLine(ex); restore = new("Failed", "Unavailable"); }
        }

        var backups = new RegistryBackup[apps.Count];
        var folder = Path.Combine(historyDirectory, "RegistryBackups", queueId);
        for (var index = 0; index < apps.Count; index++)
        {
            var app = apps[index];
            if (!enabled || stopRequested()) backups[index] = new(app.Name, new("Disabled", reason));
            else if (app.Source == "MSIX" || app.InstallerType == InstallType.Msix)
                backups[index] = new(app.Name, new("Disabled", "NoRegistryBranch"));
            else if (!UninstallBroker.IsRegistrationPath(app.RegistryKeyPath))
                backups[index] = new(app.Name, new("Failed", "InvalidRegistryBranch"));
            else
            {
                var file = Path.Combine(folder, FormattableString.Invariant($"{index + 1:D4}.reg"));
                try
                {
                    EnsureBackupDirectory(folder);
                    backups[index] = await _export(app.RegistryKeyPath, file)
                        ? new(app.Name, new("Succeeded"), file) : new(app.Name, new("Failed", "ExportFailed"));
                }
                catch (Exception ex) { Debug.WriteLine(ex); backups[index] = new(app.Name, new("Failed", "ExportFailed")); }
            }
        }
        return new(restore, backups, pointName);
    }

    private static void EnsureBackupDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        for (var parent = directory; parent is not null; parent = parent.Parent)
        {
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The registry backup path contains a reparse point.");
        }
        directory.Create();
    }

    private static async Task<ProtectionStep> CreateRestorePointAsync(string description)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return new("Failed", "Permission");

        // Observe the uniquely named point after creation. A zero exit can also mean Windows skipped creation.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $description = '{{description.Replace("'", "''")}}'
            try {
                $settings = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
                $policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore'
                if ((Get-ItemProperty $policy -Name DisableSR -ErrorAction SilentlyContinue).DisableSR -eq 1 -or
                    (Get-ItemProperty $settings -Name DisableSR -ErrorAction SilentlyContinue).DisableSR -eq 1) { 'ProtectionOff'; exit }
                $minutes = (Get-ItemProperty $settings -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
                if ($null -eq $minutes) { $minutes = 1440 }
                $latest = Get-CimInstance -Namespace root/default -ClassName SystemRestore | Sort-Object SequenceNumber -Descending | Select-Object -First 1
                if ($minutes -gt 0 -and $latest) {
                    $created = $latest.CreationTime
                    if ($created -is [string]) { $created = [System.Management.ManagementDateTimeConverter]::ToDateTime($created) }
                    if ($created -gt (Get-Date).AddMinutes(-$minutes)) { 'RateLimit'; exit }
                }
                Checkpoint-Computer -Description $description -RestorePointType APPLICATION_UNINSTALL -WarningAction SilentlyContinue | Out-Null
                if (Get-CimInstance -Namespace root/default -ClassName SystemRestore | Where-Object Description -CEQ $description) { 'Created' } else { 'Unverified' }
            } catch {
                if ($_.Exception -is [UnauthorizedAccessException] -or $_.Exception.HResult -eq -2147024891 -or $_.CategoryInfo.Category -eq 'PermissionDenied') { 'Permission' }
                elseif ($_.Exception.HResult -eq -2147023838 -or $_.FullyQualifiedErrorId -match 'SystemRestore.*Disabled') { 'ProtectionOff' }
                elseif ($_.FullyQualifiedErrorId -match 'SystemRestore.*(Frequency|Throttl)') { 'RateLimit' }
                else { 'Unavailable' }
            }
            """;
        using var process = Start(@"WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        var output = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (TimeoutException) { return new("Failed", "Unverified"); }
        var result = (await output).Trim();
        return process.ExitCode == 0 && result == "Created" ? new("Succeeded")
            : new("Failed", result is "Permission" or "ProtectionOff" or "RateLimit" or "Unverified" ? result : "Unavailable");
    }

    internal static async Task<bool> ExportRegistryAsync(string path, string destination)
    {
        if (!UninstallBroker.IsRegistrationPath(path) || path.Contains('\0')) return false;
        var temporary = destination + ".tmp";
        if (File.Exists(temporary) || File.Exists(destination)) return false;
        try
        {
            using var process = Start("reg.exe", "export", path, temporary);
            var output = process.StandardOutput.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException)
            {
                // This handle belongs only to the read-only registry export helper.
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                return false;
            }
            await output;
            if (process.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length == 0) return false;
            File.Move(temporary, destination);
            return true;
        }
        finally { File.Delete(temporary); }
    }

    private static Process Start(string file, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, file))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("The Windows protection helper did not start.");
    }
}
