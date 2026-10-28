using System.IO;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunAlreadyAbsentTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        const string registryRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_";
        await RunTest("Verified current absence remains a skip in queue and history without a fabricated exit or removal", results, logger, async () =>
        {
            var result = UninstallService.AlreadyAbsentResult();
            if (result.Success || BatchUninstallCoordinator.Classify(result) != BatchItemState.Skipped || UninstallService.FormatResultEvidence(result).Contains("Exit code:"))
                throw new Exception("Absence became successful removal or fabricated a process exit.");
            foreach (var uncertain in new[] { VerificationStatus.Unknown, VerificationStatus.StillPresent })
                if (BatchUninstallCoordinator.Classify(result with { Verification = uncertain }) == BatchItemState.Skipped)
                    throw new Exception("Unverified absence was allowed to hide an application.");
            if (HistoryOutcome.Normalize(false, "NotInstalled") != "NotInstalled" || HistoryOutcome.Normalize(true, "NotInstalled") != "NeedsAttention")
                throw new Exception("Skipped history outcome became success or an unknown status.");
            var run = await BatchUninstallCoordinator.RunAsync([new() { Id = "OwnedAbsent" }], _ => Task.FromResult(result), () => false);
            if (run.State != BatchQueueState.CompletedWithSkips || run.Successful != 0 || run.Failed != 0 || run.Skipped != 1 || run.Processed != 0 || run.Stopped || run.Items[0].ExitCode is not null)
                throw new Exception("An already absent-only queue has inaccurate accounting or a false all-success state.");
            return "Exact absence is skipped, not success/failure; unknown presence cannot skip. No native exit is invented, history stays neutral, and a skip-only queue finishes with notes.";
        });
        await RunTest("An actual owned uninstaller removing a selected peer makes the peer skipped, not a missing-executable failure", results, logger, async () =>
        {
            var firstId = Guid.NewGuid().ToString("N"); var peerId = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(Path.GetTempPath(), "Uninstaller77_RemovedPeer_" + firstId); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "owner.txt"), firstId + ":" + peerId);
            var peerExe = Path.Combine(folder, "peer.exe"); File.Copy(Environment.ProcessPath!, peerExe);
            var first = new InstalledApp { Id = firstId, Name = "Owned parent uninstall fixture", Source = "Fixture", RegistryHive = "HKCU", RegistryKeyPath = "HKCU\\" + registryRoot + firstId,
                UninstallString = $"\"{Environment.ProcessPath}\" --fixture-uninstall-pair {firstId}:{peerId}" };
            var peer = new InstalledApp { Id = peerId, Name = "Owned peer removed with parent", Source = "Fixture", RegistryHive = "HKCU", RegistryKeyPath = "HKCU\\" + registryRoot + peerId,
                UninstallString = $"\"{peerExe}\" --fixture-delay 100" };
            foreach (var app in new[] { first, peer })
                using (var key = Registry.CurrentUser.CreateSubKey(app.RegistryKeyPath[5..]))
                { key.SetValue("DisplayName", app.Name); key.SetValue("UninstallString", app.UninstallString); }
            try
            {
                var outcomes = new List<UninstallService.UninstallResult>(); var engine = new UninstallService();
                var batch = await BatchUninstallCoordinator.RunAsync([first, peer], async app =>
                { var outcome = await engine.UninstallAsync(app); outcomes.Add(outcome); return outcome; }, () => false);
                if (batch.Successful != 1 || batch.Failed != 0 || batch.Skipped != 1 || batch.Processed != 1 ||
                    outcomes[1].FailureCode != "RegistrationAbsent" || outcomes[1].Success || batch.Items[1].ExitCode is not null ||
                    File.Exists(peerExe) || !File.Exists(Path.Combine(folder, "execution.txt")))
                    throw new Exception("An already removed peer was misreported as a failed uninstall: " + outcomes[1]);
                return "A real owned EXE removed its own and its peer's unique HKCU keys and exact peer executable. The live engine counted one removal and one already-absent skip; no peer process or fake successful removal was recorded.";
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(registryRoot + firstId, false); Registry.CurrentUser.DeleteSubKeyTree(registryRoot + peerId, false); }
        });
        await RunTest("Absent exact registration is checked before a missing cached executable", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            var result = await new UninstallService().UninstallAsync(new() { Id = id, Name = "Owned already-absent fixture", RegistryHive = "HKCU",
                RegistryKeyPath = "HKCU\\" + registryRoot + id, UninstallString = $"\"{Path.Combine(Path.GetTempPath(), "OwnedMissing_" + id, "uninstall.exe")}\"" });
            if (result.Success || result.FailureCode != "RegistrationAbsent" || BatchUninstallCoordinator.Classify(result) != BatchItemState.Skipped)
                throw new Exception("Missing file was checked before exact registration absence: " + result);
            return "No cached executable was launched; current absence is a skip, never success or a missing-file failure.";
        });
        await RunTest("A still-registered application with a missing executable remains failed and is not silently unregistered", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N"); var path = registryRoot + id;
            var app = new InstalledApp { Id = id, Name = "Owned broken registration fixture", RegistryHive = "HKCU", RegistryKeyPath = "HKCU\\" + path,
                UninstallString = $"\"{Path.Combine(Path.GetTempPath(), "OwnedMissing_" + id, "uninstall.exe")}\"" };
            using (var key = Registry.CurrentUser.CreateSubKey(path)) { key.SetValue("DisplayName", app.Name); key.SetValue("UninstallString", app.UninstallString); }
            try
            {
                var result = await new UninstallService().UninstallAsync(app);
                using var retained = Registry.CurrentUser.OpenSubKey(path);
                if (result.Success || result.FailureCode != "MissingExecutable" || retained is null || BatchUninstallCoordinator.Classify(result) != BatchItemState.Failed)
                    throw new Exception("A genuine broken registration was hidden or converted into a skip.");
                return "Existing registration and missing uninstaller remain a real failure; no Registry cleanup or fabricated removal occurred.";
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
        });
    }
}
