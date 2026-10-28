using System.IO;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunCompletionAuditTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        await RunTest("Removed registration does not label unrelated folder contents as proven leftovers", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            var path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + id;
            var folder = Path.Combine(Path.GetTempPath(), "Uninstaller77_UnrelatedContents_" + id); Directory.CreateDirectory(folder);
            var keep = Path.Combine(folder, "unrelated-owned-data.txt"); File.WriteAllText(keep, "Preserve this independent data.");
            var executable = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
            var command = $"\"{executable}\" --fixture-uninstall {id}";
            using (var key = Registry.CurrentUser.CreateSubKey(path)) { key.SetValue("DisplayName", "Owned registration-only fixture"); key.SetValue("UninstallString", command); }
            try
            {
                var result = await new UninstallService().UninstallAsync(new InstalledApp { Id = id, Name = "Owned registration-only fixture",
                    RegistryHive = "HKCU", RegistryKeyPath = "HKCU\\" + path, UninstallString = command, Source = "Registry", InstallLocation = folder });
                if (!result.Success || result.Verification != VerificationStatus.VerifiedRemoved || File.ReadAllText(keep) != "Preserve this independent data.")
                    throw new Exception("Unrelated folder contents corrupted exact registration verification or were altered.");
                return "Real owned uninstaller removed only its registration; independent file preserved and no arbitrary leftover claim was fabricated.";
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
        });
        await RunTest("MSIX review release requires the same finished worker and completed output", results, logger, () =>
        {
            foreach (var exited in new[] { false, true })
            foreach (var output in new[] { false, true })
            foreach (var errors in new[] { false, true })
                if (MsixDeploymentService.CanReleaseReview("owned", "owned", exited, output, errors) != (exited && output && errors))
                    throw new Exception("A live or incompletely observed MSIX worker could be acknowledged as retired.");
            if (MsixDeploymentService.CanReleaseReview("other", "owned", true, true, true)) throw new Exception("MSIX review crossed an identity boundary.");
            return "Only the exact finished worker with fully drained output can be retired by acknowledgment; no AppX package was removed.";
        });
        await RunTest("Cancelled worker request sends no uninstall before its commit boundary", results, logger, async () =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try
            {
                using var session = await UninstallBroker.StartAsync(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\OwnedNeverLaunched",
                    false, Environment.ProcessPath!, "--fixture-delay 100", elevate: false, cancellation: cancellation.Token);
                throw new Exception("A cancelled request started a worker.");
            }
            catch (OperationCanceledException) { }
            return "Pre-cancelled broker request stopped before pipe creation, worker launch or command transfer.";
        });
        await RunTest("Completed no-op stops its batch with finite honest results and no next launch", results, logger, async () =>
        {
            var calls = 0;
            var queue = Enumerable.Range(1, 3).Select(i => new InstalledApp { Id = "owned-noop-" + i, Source = "Fixture", Name = "Owned " + i }).ToArray();
            var run = await BatchUninstallCoordinator.RunAsync(queue, _ =>
            {
                calls++;
                return Task.FromResult(UninstallService.InterpretCompletedProcessResult(0, VerificationStatus.StillPresent, false));
            }, () => false);
            if (calls != 1 || run.Successful != 0 || run.Failed != 1 || run.Skipped != 2 || run.NeedsUserAttention ||
                run.State != BatchQueueState.PartialFailure || !run.Stopped)
                throw new Exception("No-op completion left an endless review, fabricated success or launched the next fixture.");
            return "One failed verification, two explicitly skipped items, finite results and no automatic next launch.";
        });
        await RunTest("Known failed exits stay failed even when registration cannot be read", results, logger, () =>
        {
            foreach (var code in new[] { 1603, 1605, 1618, 5 })
            {
                var result = UninstallService.InterpretCompletedProcessResult(code, VerificationStatus.Unknown, true);
                if (result.Success || BatchUninstallCoordinator.Classify(result) != BatchItemState.Failed)
                    throw new Exception("A known failed exit became an endless process-completion review.");
            }
            var running = UninstallService.IncompleteProcessResult(false, 1603);
            if (BatchUninstallCoordinator.Classify(running) != BatchItemState.NeedsAttention)
                throw new Exception("Root exit hid a still-active descendant tree.");
            return "Observed terminal failure codes are finite failures; incomplete descendant trees still require attention.";
        });
        await RunTest("Registration observation rejects arbitrary Registry absence as uninstall evidence", results, logger, async () =>
        {
            foreach (var identity in new[] { "", @"HKCU\Software\Missing_" + Guid.NewGuid().ToString("N"), @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Owned\Child" })
            {
                var result = UninstallRegistration.Read(identity);
                var reviewed = await QueueReviewVerifier.ObserveAsync(new InstalledApp { RegistryKeyPath = identity, Source = "Registry" });
                if (result.State != UninstallRegistration.Presence.Unknown || reviewed.State != "Unknown")
                    throw new Exception("An unsupported path was called a removed installation.");
            }
            return "Engine and review use one exact Registry identity boundary; unsupported locations stay unknown.";
        });
        await RunTest("Acknowledgment never releases an active owned uninstaller", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            var path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + id;
            var executable = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            { key.SetValue("DisplayName", "Owned active-review fixture"); key.SetValue("UninstallString", $"\"{executable}\" --fixture-delay 1500"); }
            var app = new InstalledApp { Id = id, Source = "Registry", RegistryHive = "HKCU", RegistryKeyPath = "HKCU\\" + path,
                Name = "Owned active-review fixture", UninstallString = $"\"{executable}\" --fixture-delay 1500" };
            var engine = new UninstallService(TimeSpan.FromMilliseconds(50));
            try
            {
                var result = await engine.UninstallAsync(app);
                if (result.Success || !UninstallService.HasPendingOperation || engine.TryReleaseCompletedReview(app))
                    throw new Exception("Acknowledgment released a live process.");
                if (engine.TryReleaseCompletedReview(new InstalledApp { Id = "other" })) throw new Exception("Mismatched review released execution.");
                await Task.Delay(1800);
                if (!engine.TryReleaseCompletedReview(app) || UninstallService.HasPendingOperation)
                    throw new Exception("A fully finished job could not be released by an explicit review acknowledgment.");
                using var retained = Registry.CurrentUser.OpenSubKey(path);
                if (retained is null) throw new Exception("Acknowledgment modified the retained registration.");
                return "Live tree and wrong identity refused; completed job explicitly released without success or Registry modification.";
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); await engine.RetryVerificationAsync(app); }
        });
        await RunTest("Completed no-op uninstaller is finite and cannot block the next explicit removal", results, logger, async () =>
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
            var firstId = Guid.NewGuid().ToString("N"); var nextId = Guid.NewGuid().ToString("N");
            const string root = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_";
            InstalledApp Make(string id, string args)
            {
                using var key = Registry.CurrentUser.CreateSubKey(root + id);
                key.SetValue("DisplayName", "Owned completed-no-op fixture " + id);
                key.SetValue("UninstallString", $"\"{executable}\" {args}");
                return new InstalledApp { Id = id, Source = "Registry", RegistryHive = "HKCU", RegistryKeyPath = "HKCU\\" + root + id,
                    Name = "Owned no-op fixture", UninstallString = $"\"{executable}\" {args}" };
            }
            var first = Make(firstId, "--fixture-delay 100");
            var next = Make(nextId, "--fixture-uninstall " + nextId);
            var engine = new UninstallService();
            try
            {
                var result = await engine.UninstallAsync(first);
                if (result.Success || result.ExitCode != 0 || result.Verification != VerificationStatus.StillPresent ||
                    result.FailureCode != "RemovalNotObserved" || UninstallService.HasPendingOperation)
                    throw new Exception($"Finished no-op was not finalized: Success={result.Success}; Exit={result.ExitCode}; Verification={result.Verification}; Failure={result.FailureCode}; Pending={UninstallService.HasPendingOperation}");
                var nextResult = await engine.UninstallAsync(next);
                if (!nextResult.Success || nextResult.Verification != VerificationStatus.VerifiedRemoved)
                    throw new Exception("An earlier finished no-op blocked a separate explicitly requested real fixture uninstall.");
                using var retained = Registry.CurrentUser.OpenSubKey(root + firstId);
                if (retained is null) throw new Exception("An unchanged registration was deleted to manufacture success.");
                return "Real no-op exit 0 plus retained registration finishes honestly; a new explicit fixture uninstall executes; no automatic next removal or user program was touched.";
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKeyTree(root + firstId, false);
                // Clear old-version completed evidence when demonstrating the pre-fix failure.
                await engine.RetryVerificationAsync(first);
                Registry.CurrentUser.DeleteSubKeyTree(root + nextId, false);
            }
        });
    }
}
