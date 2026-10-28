using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunPhase13TestsAsync(List<TestResult> results, Action<string>? logger)
    {
        await RunTest("Phase13 scoped filesystem and unique HKCU fixture capture persists to SQLite", results, logger, async () =>
        {
            var fixtureRoot = Path.Combine(Path.GetTempPath(), "U77_Phase13_" + Guid.NewGuid().ToString("N"));
            var scope = Path.Combine(fixtureRoot, "scope"); Directory.CreateDirectory(scope);
            var dbPath = Path.Combine(fixtureRoot, "monitor.db");
            var registryName = "U77_Phase13_" + Guid.NewGuid().ToString("N");
            var registryPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{registryName}";
            try
            {
                File.WriteAllText(Path.Combine(scope, "baseline.txt"), "baseline");
                var db = new DatabaseService(dbPath);
                using var monitor = new AdvancedInstallMonitorService(db);
                var started = await monitor.StartManualAsync("Owned Phase 13 fixture", scope);
                File.WriteAllText(Path.Combine(scope, "created.txt"), "metadata only");
                using (var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true))
                {
                    if (key is null) throw new InvalidOperationException("Could not create unique HKCU test fixture key.");
                    key.SetValue("DisplayName", "Uninstaller 77 isolated Phase 13 fixture");
                    key.SetValue("DisplayVersion", "0.0.1");
                }
                var visible64 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64).OpenSubKey(registryPath) is not null;
                var visible32 = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32).OpenSubKey(registryPath) is not null;
                await Task.Delay(600);
                var ended = await monitor.StopAsync();
                var saved = db.GetMonitorSession(started.SessionId) ?? throw new InvalidOperationException("Session was not persisted.");
                var captured = db.GetMonitorEvents(started.SessionId, 0, 500);
                if (ended.State != InstallMonitorState.Completed.ToString() || saved.EventCount == 0)
                    throw new InvalidOperationException("The session did not complete with persisted observations.");
                if (!captured.Any(e => e.Category == "Files" && e.Target.EndsWith("created.txt", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The owned fixture file change was not observed.");
                if (!captured.Any(e => e.Category == "Registry" && e.Target.Contains(registryName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"The unique HKCU uninstall fixture was not found in the before/after comparison. Visible64={visible64}; Visible32={visible32}; warnings={saved.WarningsJson}; Observed: " + string.Join(" | ", captured.Take(20).Select(e => e.Category + "/" + e.ChangeType + "/" + e.Target)));
                var report = Path.Combine(fixtureRoot, "report.csv"); monitor.ExportSession(started.SessionId, report, "csv");
                if (!File.Exists(report) || !File.ReadAllText(report).Contains("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("CSV report did not persist or sanitize the owned fixture path.");
                return $"Observed {captured.Count} persisted events, including the owned file and unique HKCU key; session reopen and sanitized CSV export succeeded. CoverageComplete={saved.CoverageComplete}.";
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false); } catch (SystemException) { }
                try { if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true); } catch (IOException) { }
            }
        });

        await RunTest("Phase13 duplicate start prevention and explicit cancelled stop state", results, logger, async () =>
        {
            var fixtureRoot = Path.Combine(Path.GetTempPath(), "U77_Phase13_State_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixtureRoot);
            try
            {
                var db = new DatabaseService(Path.Combine(fixtureRoot, "monitor.db")); using var monitor = new AdvancedInstallMonitorService(db);
                await monitor.StartManualAsync("State fixture", fixtureRoot);
                var blocked = false;
                try { await monitor.StartManualAsync("Conflicting fixture", fixtureRoot); } catch (InvalidOperationException) { blocked = true; }
                if (!blocked) throw new InvalidOperationException("A second conflicting session was accepted.");
                var ended = await monitor.StopAsync(cancelled: true);
                if (ended.State != InstallMonitorState.Cancelled.ToString() || ended.EndedAtUtc is null)
                    throw new InvalidOperationException("Cancellation did not preserve its explicit terminal state.");
                return "Concurrent session start was rejected; stop with cancelled=true completed final comparison and persisted Cancelled.";
            }
            finally { try { Directory.Delete(fixtureRoot, true); } catch (IOException) { } }
        });

        await RunTest("Phase13 interrupted-session recovery preserves evidence and reports missed interval", results, logger, async () =>
        {
            var fixtureRoot = Path.Combine(Path.GetTempPath(), "U77_Phase13_Recovery_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixtureRoot);
            try
            {
                var scope = Path.Combine(fixtureRoot, "scope"); Directory.CreateDirectory(scope);
                var db = new DatabaseService(Path.Combine(fixtureRoot, "monitor.db"));
                var original = new AdvancedInstallMonitorService(db);
                var started = await original.StartManualAsync("Interrupted fixture", scope);
                File.WriteAllText(Path.Combine(scope, "recovered.txt"), "fixture");
                db.MarkInterruptedMonitorSessions();
                original.Dispose();
                using var monitor = new AdvancedInstallMonitorService(db);
                if (db.GetMonitorSession(started.SessionId)?.State != InstallMonitorState.Interrupted.ToString()) throw new InvalidOperationException("Active persisted session was not marked Interrupted on startup.");
                var recovered = await monitor.RecoverInterruptedAsync(started.SessionId);
                var events = db.GetMonitorEvents(started.SessionId, 0, 500);
                if (recovered.State != InstallMonitorState.Interrupted.ToString() || recovered.CoverageComplete || events.All(e => !e.Target.EndsWith("recovered.txt", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Recovery mismatch: state={recovered.State}; coverage={recovered.CoverageComplete}; events={events.Count}; targets={string.Join(" | ", events.Take(10).Select(e => e.Target))}; warnings={recovered.WarningsJson}");
                if (!recovered.WarningsJson.Contains("cannot reconstruct", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Recovery omitted the missed-observation warning.");
                return $"Interrupted session recovered with {events.Count} current comparison events; missed activity remains explicitly unobserved.";
            }
            finally { try { Directory.Delete(fixtureRoot, true); } catch (IOException) { } }
        });
    }
}
