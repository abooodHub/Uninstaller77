using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private static async Task RunQueueProtectionTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        foreach (var reason in new[] { "ProtectionOff", "RateLimit", "Permission" })
        {
            await RunTest($"Queue protection preserves {reason} without blocking registry export", results, logger, async () =>
            {
                var (root, id, app) = ProtectionFixture();
                var exported = false;
                var service = new QueueProtectionService(root, _ => Task.FromResult(new ProtectionStep("Failed", reason)),
                    (path, _) => Task.FromResult(exported = path == app.RegistryKeyPath));
                var report = await service.PrepareAsync(id, DateTimeOffset.Now, [app], true, () => false);
                if (!exported || report.Restore != new ProtectionStep("Failed", reason) || !report.HasFailure ||
                    report.Backups.Single().Result.Status != "Succeeded") throw new Exception("Protection failure suppressed another step.");
                if (report.RestoreText("ar") == report.RestoreText("en")) throw new Exception("Failure reason was not localized.");
                return "Simulated restore-point failure, with independent backup and localized warning. No restore point or uninstaller launched.";
            });
        }

        await RunTest("Queue protection records an export failure and handles disabled settings and MSIX", results, logger, async () =>
        {
            var (root, id, app) = ProtectionFixture();
            var calls = 0;
            var service = new QueueProtectionService(root, _ => Task.FromResult(new ProtectionStep("Succeeded")),
                (_, _) => { calls++; throw new UnauthorizedAccessException(); });
            var msix = new InstalledApp { Name = "Owned MSIX model", Source = "MSIX" };
            var disabled = await service.PrepareAsync(id, DateTimeOffset.Now, [app, msix], false, () => false);
            if (calls != 0 || disabled.Restore.Reason != "SettingOff" || disabled.Backups.Any(item => item.Result.Reason != "SettingOff"))
                throw new Exception("Disabled protection invoked a helper.");
            var stopped = await service.PrepareAsync(id, DateTimeOffset.Now, [app], true, () => true);
            if (calls != 0 || stopped.Restore.Reason != "Stopped") throw new Exception("Stopped queue invoked a helper.");
            var result = await service.PrepareAsync(id, DateTimeOffset.Now, [app, msix], true, () => false);
            if (calls != 1 || !result.HasFailure || result.Restore.Status != "Succeeded" ||
                result.Backups[0].Result != new ProtectionStep("Failed", "ExportFailed") ||
                result.Backups[1].Result != new ProtectionStep("Disabled", "NoRegistryBranch")) throw new Exception("Incorrect partial results.");
            return "Export exception was recorded separately. Disabled, stopped and unsupported branches started no export.";
        });

        await RunTest("Queue protection success writes a real owned .reg and round-trips History", results, logger, async () =>
        {
            var (root, id, app) = ProtectionFixture();
            using (var registry = Registry.CurrentUser.CreateSubKey(app.RegistryKeyPath[5..]))
            {
                registry.SetValue("DisplayName", app.Name);
                using var child = registry.CreateSubKey("OwnedChild");
                child.SetValue("OwnedMarker", id);
            }
            try
            {
                var started = new DateTimeOffset(2026, 11, 5, 4, 30, 0, TimeSpan.FromHours(3));
                var service = new QueueProtectionService(root, description =>
                {
                    if (!description.Contains("2026-11-05 04:30:00 +03:00 (1)") || !description.EndsWith(id[..8], StringComparison.Ordinal))
                        throw new Exception("Restore point name lost the queue timestamp, count or identity.");
                    return Task.FromResult(new ProtectionStep("Succeeded"));
                });
                var report = await service.PrepareAsync(id, started, [app], true, () => false);
                var backup = report.Backups.Single();
                if (report.HasFailure || report.Restore.Status != "Succeeded" || backup.File is null || !File.Exists(backup.File))
                    throw new Exception("The registry branch was not exported.");
                var content = File.ReadAllText(backup.File);
                if (!content.Contains($"[HKEY_CURRENT_USER\\{app.RegistryKeyPath[5..]}\\OwnedChild]", StringComparison.OrdinalIgnoreCase) || !content.Contains(id))
                    throw new Exception("The export omitted the exact owned branch or nested value.");
                var database = new DatabaseService(Path.Combine(root, "history.db"));
                database.AddRecord(new UninstallRecord { AppId = id, AppName = "Queue protection", Outcome = "QueueProtection", Notes = JsonSerializer.Serialize(report) });
                var saved = database.GetHistoryPage(false, null, 10).Single();
                var reread = QueueProtectionReport.Read(saved.Notes);
                if (saved.Success || HistoryOutcome.Normalize(saved.Success, saved.Outcome) != "QueueProtection" ||
                    reread is null || reread.PointName != report.PointName || reread.Backups.Single().File != backup.File)
                    throw new Exception("History lost the report or treated protection as an uninstall success.");
                return "Verified real reg.exe export and SQLite round-trip; restore creation was simulated and no uninstaller launched.";
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(app.RegistryKeyPath[5..], false); }
        });
    }

    private static (string Root, string Id, InstalledApp App) ProtectionFixture()
    {
        var id = Guid.NewGuid().ToString("N");
        return (Path.Combine(Path.GetTempPath(), "U77_Protection_" + id), id, new InstalledApp
        {
            Name = "Owned protection " + id,
            RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\U77_Protection_" + id
        });
    }
}
