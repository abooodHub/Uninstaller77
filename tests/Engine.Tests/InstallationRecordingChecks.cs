using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;
using WinUninstaller.Services;

internal static class InstallationRecordingChecks
{
    private const string UninstallParent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private static string FixtureRoot(string id) => Path.Combine(Path.GetTempPath(), "U77_InstallationRecording_" + id);
    private static string FixtureRegistration(string id) => UninstallParent + @"\U77Monitor_" + id;
    private static string FixtureScope(string id) => @"Software\U77MonitorFixture\" + id;

    private static bool IsVmwareGuest()
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        return bios?.GetValue("SystemManufacturer") is string manufacturer && manufacturer.Contains("VMware", StringComparison.OrdinalIgnoreCase);
    }

    internal static int RunOwnedInstaller(string verb, string id)
    {
        if (!IsVmwareGuest() || !Guid.TryParseExact(id, "N", out _) || verb is not ("install" or "remove")) return 87;
        var root = FixtureRoot(id);
        var installed = Path.Combine(root, "App");
        if (File.ReadAllText(Path.Combine(root, "owner.txt")) != id || !InstallationJournal.LocalPathWithoutLinks(installed)) return 87;
        var registered = FixtureRegistration(id);
        if (verb == "remove")
        {
            using (var key = Registry.CurrentUser.OpenSubKey(registered))
                if (key?.GetValue("U77Fixture") as string != id || key.GetValue("InstallLocation") as string != installed) return 87;
            Registry.CurrentUser.DeleteSubKey(registered, throwOnMissingSubKey: true);
            return 0;
        }
        using (var existing = Registry.CurrentUser.OpenSubKey(registered)) { if (existing is not null) return 87; }
        var target = Path.Combine(installed, "Owned.exe");
        File.WriteAllText(target, "Owned executable marker, never executed");
        using var entry = Registry.CurrentUser.CreateSubKey(registered);
        entry.SetValue("DisplayName", "Owned monitor fixture");
        entry.SetValue("DisplayVersion", "1");
        entry.SetValue("Publisher", "U77");
        entry.SetValue("InstallLocation", installed);
        entry.SetValue("DisplayIcon", target);
        entry.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" --installation-recording-fixture remove {id}");
        entry.SetValue("U77Fixture", id);
        using var product = Registry.CurrentUser.CreateSubKey(FixtureScope(id));
        product.SetValue("InstallLocation", installed);
        product.SetValue("UninstallRegistryKey", "HKCU\\" + registered);
        return 0;
    }

    internal static async Task<int> RunAsync(string? vmEvidence = null)
    {
        var isVm = vmEvidence is not null;
        if (isVm && !IsVmwareGuest()) throw new InvalidOperationException("VMware guest could not be verified.");
        var results = new List<string>();
        void Check(bool result, string message)
        {
            if (!result) throw new InvalidOperationException(message);
            results.Add(message);
            Console.WriteLine("PASS " + message);
        }

        var id = Guid.NewGuid().ToString("N");
        var root = FixtureRoot(id);
        var installed = Path.Combine(root, "App");
        var unrelated = Path.Combine(installed, "unrelated.txt");
        var target = Path.Combine(installed, "Owned.exe");
        var shortcut = Path.Combine(root, "destination");
        var registrationPath = FixtureRegistration(id);
        var scope = FixtureScope(id);
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(root, "owner.txt"), id);
        var journal = new InstallationJournal(root);
        var installIdentity = new InstalledApp { Id = "HKCU\\" + registrationPath, RegistryKeyPath = "HKCU\\" + registrationPath,
            Name = "Owned monitor fixture", Version = "1", Publisher = "U77", InstallLocation = installed, DisplayIcon = target };
        try
        {
            Check(InstallationScope.Valid(scope) && !InstallationScope.Valid(@"Software\Microsoft\Windows") &&
                !InstallationScope.ValidDirectory(Path.GetPathRoot(root)!), "Recording rejects broad operating-system scopes");
            Check(InstallationScope.RegistryViews(true).SequenceEqual(new[] { ("HKCU", 0), ("HKLM", 32), ("HKLM", 64) }),
                "Shared HKCU software keys are scanned once and HKLM keeps both views");
            using (var systemKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                Check(systemKey is not null && UninstallRegistration.ReadLastWrite(systemKey.Handle) > 0,
                    "Windows supplies a real last-write timestamp through a read-only Registry handle");
            var beforeFiles = new Dictionary<string, (string?, long, long)> { ["removed"] = ("old", 1, 1), ["modified"] = ("old", 1, 1) };
            var afterFiles = new Dictionary<string, (string?, long, long)> { ["new"] = ("new", 2, 2), ["modified"] = ("new", 2, 2) };
            Check(InstallationRecorder.CompareFiles(beforeFiles, afterFiles, false, false).Select(change => change.Change).SequenceEqual(["Modified"]) &&
                InstallationRecorder.CompareFiles(beforeFiles, afterFiles, true, true).Count() == 3,
                "An incomplete snapshot does not claim creation or deletion from missing observations");
            var registryKey = ("HKCU\\" + scope, 0);
            var keys = new Dictionary<(string Path, int View), InstallationScope.KeyStamp> { [registryKey] = new("owned", false, null, null) };
            Check(!InstallationRecorder.CompareKeys(keys, [], "After", afterComplete: false).Any() &&
                !InstallationRecorder.CompareKeys([], keys, "After", beforeComplete: false).Any() &&
                InstallationRecorder.CompareKeys(keys, [], "After").Single().Change == "Deleted",
                "Registry changes distinguish an incomplete observation from a verified absence");
            Check(InstallationRecorder.MatchRegistration(installed, [], [installIdentity]) == MonitoredRegistration.Capture(installIdentity) &&
                InstallationRecorder.MatchRegistration(installed, [installIdentity], [installIdentity]) is null &&
                InstallationRecorder.MatchRegistration(installed, [], [installIdentity, installIdentity]) is null,
                "Registry association requires one fresh identity with an exact folder and executable reference");

            var recorder = new InstallationRecorder(journal);
            using var finish = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var during = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registryDuring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            InstallationReport? report = null;
            File.WriteAllText(Path.Combine(installed, "existing.txt"), "Before installation");
            var recording = Task.Run(() => recorder.RecordAsync(installed, isVm ? scope : "", value =>
            {
                report = value;
                if (value.State == "Monitoring") ready.TrySetResult();
                if (value.Changes.Any(change => change.Phase == "During" && change.Kind == "FileEvent")) during.TrySetResult();
                if (value.Changes.Any(change => change.Phase == "During" && change.Kind == "Registry")) registryDuring.TrySetResult();
            }, finish.Token));
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
                File.WriteAllText(unrelated, "Independent concurrent writer");
                if (isVm)
                {
                    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                    foreach (var arg in new[] { "--installation-recording-fixture", "install", id }) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start)!;
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    Check(process.ExitCode == 0, "Owned installer process created the fixture registration and payload");
                    using var product = Registry.CurrentUser.CreateSubKey(scope);
                    using var secondary = product.CreateSubKey("Independent");
                    secondary.SetValue("Owner", "Unrelated writer");
                }
                else File.WriteAllText(target, "Owned executable marker, never executed");
                await during.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (isVm) await registryDuring.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { finish.Cancel(); await recording; }

            Check(report is { State: "Completed", BaselineFiles: 1 } && report.Changes.Any(change => change.Phase == "During") &&
                report.Changes.Any(change => change.Phase == "After" && change.Path == target), "Recording captures the baseline, live events and final change set");
            var saved = journal.Recent().Single();
            var identity = MonitoredRegistration.Capture(installIdentity, 100)!;
            var completedReport = saved with { Registration = identity, State = "Completed" };
            Check(InstallationJournal.MatchesRemoval(completedReport, identity) &&
                !InstallationJournal.MatchesRemoval(completedReport, identity with { LastWrite = 200 }) &&
                !InstallationJournal.MatchesRemoval(completedReport, identity with { LastWrite = null }),
                "A recreated registration with identical names and paths cannot reuse an old report");
            Check(saved.Changes.Where(change => change.Path == unrelated).Any() &&
                saved.Changes.Where(change => change.Path == unrelated).All(change => change.Association == "Observed"),
                "Concurrent file remains an observation even in the monitored folder");
            Check(saved.Changes.Where(change => change.Phase == "During").All(change => change.Association == "Observed"),
                "Notifications never claim a writing process or installation ownership");
            if (isVm)
            {
                var installedStamp = saved.Registration?.LastWrite;
                Check(installedStamp > 0 && saved.Registration == MonitoredRegistration.Capture(installIdentity, installedStamp) && saved.Changes.Any(change =>
                    change.Path == target && change.Association == "RegistrationLinked"), "VM fixture binds the exact new uninstall registration and its executable");
                Check(saved.Changes.Where(change => change.Path.EndsWith("\\Independent", StringComparison.OrdinalIgnoreCase)).Any() &&
                    saved.Changes.Where(change => change.Path.EndsWith("\\Independent", StringComparison.OrdinalIgnoreCase)).All(change => change.Association == "Observed"),
                    "VM fixture excludes an adjacent Registry key without explicit identity evidence");
                installIdentity.RegistrationLastWrite = installedStamp;
                Check(journal.AddResidualEvidence(installIdentity, [], CancellationToken.None) == 0, "An installed registration cannot supply residual evidence");
                var current = RegistryDiscoveryService.ReadExactRegistration(installIdentity.RegistryKeyPath) ?? throw new InvalidOperationException("Owned registration was not discovered.");
                var uninstall = await new UninstallService().UninstallAsync(current);
                Check(uninstall.Success && uninstall.Verification == VerificationStatus.VerifiedRemoved && File.Exists(target) && uninstall.RegistrationLastWrite == installedStamp,
                    "The actual uninstall engine verified removal of the owned fixture while leaving a known file");
                var scanner = new ResidualScanService { Installations = journal };
                var scan = await scanner.ScanResidualsAsync(installIdentity);
                Check(scan.Items.Any(item => item.Reason == InstallationJournal.EvidenceReason && !item.CanClean && !item.IsSelected &&
                    item.Path.Contains(scope, StringComparison.OrdinalIgnoreCase)), "Verified removal exposes unchanged monitor evidence as review-only");
                using (var key = Registry.CurrentUser.OpenSubKey(scope, writable: true)) key!.SetValue("ChangedAfterReview", "Changed");
                var changed = await scanner.ScanResidualsAsync(installIdentity);
                Check(!changed.Items.Any(item => item.Reason == InstallationJournal.EvidenceReason && item.Type == ResidualType.RegistryKey),
                    "Changed Registry evidence is no longer used by the residual scanner");
                installIdentity.RegistrationLastWrite = installedStamp + 1;
                Check(journal.AddResidualEvidence(installIdentity, [], CancellationToken.None) == 0,
                    "Removal of another generation cannot reuse the earlier log");
            }
            else Check(saved.Registration is null && saved.Changes.All(change => change.Association == "Observed"),
                "A real file recording without a new registration remains unattributed");

            installIdentity.RegistrationLastWrite = 100;
            var record = new UninstallRecord { AppId = installIdentity.Id, AppName = installIdentity.Name, Success = true,
                Outcome = "Succeeded", ResidualIdentity = ResidualHistoryIdentity.Capture(installIdentity) };
            Check(ResidualHistoryIdentity.TryRestore(record, out var historyApp) && historyApp?.RegistrationLastWrite == 100,
                "Rescanning history retains the specific registration that was verified before removal");

            using (var cancelRecording = new CancellationTokenSource())
            {
                await recorder.RecordAsync(installed, "", value =>
                {
                    if (value.State == "Monitoring") cancelRecording.Cancel();
                }, CancellationToken.None, cancelRecording.Token);
                var cancelled = journal.Recent().Single(value => value.State == "Cancelled");
                Check(cancelled.FinishedAt.HasValue && cancelled.Registration is null && cancelled.Changes.All(change => change.Phase != "After"),
                    "Cancelling releases the watcher and preserves an explicitly unlinked report without a final comparison");
            }

            var queue = new QueueStopController();
            var result = await BatchUninstallCoordinator.RunAsync([installIdentity, new() { Id = "second", Name = "Owned queue peer" }], _ =>
            {
                queue.RequestStop();
                return Task.FromResult(new UninstallService.UninstallResult(true, 0, VerificationStatus.VerifiedRemoved, "Fixture complete", ""));
            }, () => queue.StopRequested);
            Check(result.Stopped && result.Items[0].State == BatchItemState.Succeeded && result.Items[1].State == BatchItemState.Skipped,
                "Stop-after-current retains the active result and skips the next program");

            var backup = Path.Combine(root, "backup");
            var stored = Path.Combine(backup, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stored);
            File.WriteAllText(Path.Combine(stored, "owned.txt"), "Original fixture contents");
            var fingerprint = UnregisteredAppService.Fingerprint(UnregisteredAppService.Snapshot(stored, CancellationToken.None));
            var receipt = stored + ".json";
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { OriginalPath = shortcut, QuarantinePath = stored, AppName = "Owned fixture", CreatedUtc = DateTimeOffset.UtcNow, Kind = "Directory", Sha256 = fingerprint }));
            Directory.CreateDirectory(shortcut);
            Check(!(await ResidualScanService.RestoreQuarantinedAsync(receipt, backup)).Success && Directory.Exists(stored), "Restore refuses an occupied destination and retains the backup");
            Directory.Delete(shortcut);
            File.WriteAllText(Path.Combine(stored, "owned.txt"), "Changed");
            Check(!(await ResidualScanService.RestoreQuarantinedAsync(receipt, backup)).Success && File.Exists(receipt), "Restore refuses a changed backup and retains its receipt");
            File.WriteAllText(Path.Combine(stored, "owned.txt"), "Original fixture contents");
            Check((await ResidualScanService.RestoreQuarantinedAsync(receipt, backup)).Success && File.Exists(Path.Combine(shortcut, "owned.txt")) && !File.Exists(receipt),
                "Restore returns the verified backup to its original destination");
            Check(ResidualScanService.CompleteRestore(backup, shortcut).Success, "A receipt-cleanup error does not falsely report that the restore failed");
            var originalRegistry = new RegistryResidualSnapshot("HKCU\\" + scope, 64, new(StringComparer.OrdinalIgnoreCase)
            { ["InstallLocation"] = installed, ["UninstallRegistryKey"] = installIdentity.RegistryKeyPath, ["Other"] = "Expected" }, 1, true);
            var liveRegistry = originalRegistry with { Values = new(StringComparer.OrdinalIgnoreCase)
            { ["UninstallRegistryKey"] = installIdentity.RegistryKeyPath, ["Other"] = "Expected" } };
            Check(RegistryResidualCleanupService.MatchesRegistryValueRestoreTarget(originalRegistry, liveRegistry), "Registry restore recognizes the exact state remaining after cleanup");
            liveRegistry.Values["Other"] = "Replaced";
            Check(!RegistryResidualCleanupService.MatchesRegistryValueRestoreTarget(originalRegistry, liveRegistry), "Registry restore rejects changed neighboring values");
            if (vmEvidence is not null)
                File.WriteAllText(vmEvidence, JsonSerializer.Serialize(new { vm = Environment.OSVersion.Version.ToString(), results, log = saved }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"RESULT {results.Count} passed / 0 failed");
            return 0;
        }
        finally
        {
            if (isVm)
            {
                Registry.CurrentUser.DeleteSubKeyTree(registrationPath, throwOnMissingSubKey: false);
                Registry.CurrentUser.DeleteSubKeyTree(scope, throwOnMissingSubKey: false);
            }
        }
    }
}
