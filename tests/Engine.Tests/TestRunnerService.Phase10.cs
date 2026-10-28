using System.Diagnostics;
using System.IO;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    public async Task<List<TestResult>> RunPhase10UnitTestsAsync(Action<string>? logger = null)
    {
        var results = new List<TestResult>();
        await RunPhase10TestsAsync(results, logger);
        logger?.Invoke($"Phase10 non-destructive unit tests: {results.Count(result => result.Passed)} Passed, {results.Count(result => !result.Passed)} Failed");
        return results;
    }

    private static async Task RunPhase10TestsAsync(List<TestResult> results, Action<string>? logger)
    {
        static InstalledApp[] Apps() => [new() { Id = "a", Name = "Same display name" }, new() { Id = "b", Name = "Same display name" }, new() { Id = "c", Name = "Third" }];
        static UninstallService.UninstallResult Removed() => new(true, 0, VerificationStatus.VerifiedRemoved, "Verified", "");

        await RunTest("Fast native-launched fixture retains its actual exit code", results, logger, async () =>
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                using var execution = UninstallerProcess.Start(new ProcessStartInfo(
                    Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe"), "--fixture-exit 1603")
                    { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true });
                if (!await execution.WaitAsync(TimeSpan.FromSeconds(10), null) || execution.Root.ExitCode != 1603)
                    throw new Exception("The completed fixture lost its real exit code.");
            }
            return "Eight real short-lived child processes retained exit code 1603 after completion; no uninstall command or Registry action.";
        });

        await RunTest("Read-only Registry HKCU source diagnostics", results, logger, () =>
        {
            var registry = new RegistryDiscoveryService();
            registry.DiscoverApps(includeMsix: false);
            var source = registry.LastDiagnostics.Single(item => item.Source == "HKCU 64-bit");
            if (source.Errors != 0 || source.SourceErrors != 0) throw new Exception(string.Join("; ", source.Messages));
            if (source.Scanned != source.Accepted + source.Skipped + source.Duplicates)
                throw new Exception("HKCU source counters did not reconcile.");
            return $"HKCU 64-bit: {source.Scanned} scanned, {source.Accepted} accepted, {source.Skipped} skipped, {source.Duplicates} duplicates, 0 errors.";
        });

        await RunTest("Absolute shell paths cannot bypass executable policy", results, logger, () =>
        {
            foreach (var name in new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe" })
                foreach (var executable in new[] { name, @"C:\Windows\System32\" + name, @"C:\Program Files\Tools\" + name.ToUpperInvariant() })
                    if (UninstallService.IsAllowedExecutable(executable)) throw new Exception("Unsafe command host accepted: " + executable);
            if (!UninstallService.IsAllowedExecutable(@"C:\Fixture\uninstall.exe") || !UninstallService.IsAllowedExecutable("msiexec.exe"))
                throw new Exception("Direct uninstall engines were rejected.");
            return "24 short/absolute/mixed-case shell-host paths rejected before process launch; direct uninstallers retained.";
        });

        await RunTest("Review stop releases an already waiting operation", results, logger, async () =>
        {
            var control = new QueueStopController();
            var wait = control.WaitForReviewAsync();
            control.RequestStop();
            if (await wait.WaitAsync(TimeSpan.FromSeconds(1)) is not null || !control.StopRequested) throw new Exception("Review wait did not stop.");
            control.RequestStop();
            return "Stop resolves the wait immediately and repeated requests are idempotent.";
        });
        await RunTest("Review stop cannot be lost before the wait is registered", results, logger, async () =>
        {
            var control = new QueueStopController(); control.RequestStop();
            if (await control.WaitForReviewAsync().WaitAsync(TimeSpan.FromSeconds(1)) is not null) throw new Exception("A stopped queue restarted waiting.");
            if (control.TryResolve(Removed())) throw new Exception("A stopped queue accepted resume evidence.");
            return "A pre-existing stop returns immediately and cannot be reversed by late verification.";
        });
        await RunTest("MSIX completion receipts bind operation and package identities", results, logger, () =>
        {
            const string json = "U77_RESULT:{\"operationId\":\"one\",\"package\":\"Test.Package\",\"state\":\"Removed\",\"error\":\"\",\"hresult\":\"\"}";
            if (MsixDeploymentService.ParseReceipt(json, "one", "Test.Package") is null ||
                MsixDeploymentService.ParseReceipt(json, "two", "Test.Package") is not null ||
                MsixDeploymentService.ParseReceipt(json, "one", "Other.Package") is not null ||
                MsixDeploymentService.ParseReceipt("U77_RESULT:invalid", "one", "Test.Package") is not null)
                throw new Exception("Receipt validation accepted the wrong operation or package.");
            return "Missing, malformed, mismatched and valid receipts are distinguished.";
        });
        await RunTest("MSIX exit zero requires receipt and independent verification", results, logger, () =>
        {
            var absent = new MsixDeploymentService.PackageState(VerificationStatus.VerifiedRemoved, "", "None", 1);
            var present = absent with { Verification = VerificationStatus.StillPresent };
            var receipt = new MsixDeploymentService.Receipt("one", "Test.Package", "Removed", "", "");
            if (!MsixDeploymentService.Evaluate(0, receipt, absent, "").Success ||
                MsixDeploymentService.Evaluate(0, null, absent, "").Success ||
                MsixDeploymentService.Evaluate(0, receipt, present, "").Verification != VerificationStatus.Unknown)
                throw new Exception("A process exit became false MSIX success.");
            return "Success needs all three signals; inconsistent results pause.";
        });
        await RunTest("MSIX protected-package failure preserves the real diagnostic", results, logger, () =>
        {
            var query = new MsixDeploymentService.PackageState(VerificationStatus.StillPresent, "", "None", 1);
            var result = MsixDeploymentService.Evaluate(5, new("one", "Test.Package", "Protected", "NonRemovable", "0x80073CFA"), query, "deployment rejected");
            if (result.Success || result.Verification != VerificationStatus.StillPresent || result.FailureCode != "ProtectedPackage" ||
                !result.Error.Contains("0x80073CFA") || !result.Error.Contains("deployment rejected")) throw new Exception("Failure evidence was hidden.");
            return "Protected/error outcomes remain failures, with HRESULT and stderr available.";
        });
        await RunTest("MSIX verification failure pauses instead of reporting removal", results, logger, () =>
        {
            var query = new MsixDeploymentService.PackageState(VerificationStatus.Unknown, "query timed out", "MsixQueryTimeout", 45000);
            var result = MsixDeploymentService.Evaluate(0, new("one", "Test.Package", "Removed", "", ""), query, "");
            if (result.Success || result.FailureCode != "MsixQueryTimeout" || result.Verification != VerificationStatus.Unknown) throw new Exception("Query failure was discarded.");
            return "Verification timeout remains uncertain and diagnostic detail is retained.";
        });
        await RunTest("Read-only Windows AppX query with captured process diagnostics", results, logger, async () =>
        {
            var query = await MsixDeploymentService.QueryAsync("Uninstaller77.NonexistentFixture_0.0.0.0_x64__1234567890abc");
            if (query.Verification != VerificationStatus.VerifiedRemoved) throw new Exception(query.FailureCode + ": " + query.Error);
            return $"Read-only query completed in {query.ElapsedMs} ms; disposable identity is absent. No removal command ran.";
        });

        await RunTest("Phase10 stop before first launch", results, logger, async () =>
        {
            var calls = 0;
            var result = await BatchUninstallCoordinator.RunAsync(Apps(), _ => { calls++; return Task.FromResult(Removed()); }, () => true);
            if (calls != 0 || result.Skipped != 3 || !result.Stopped) throw new Exception("A pending operation started after stop.");
            return "No executor invoked; all three items skipped.";
        });

        await RunTest("Phase10 stop while active preserves strict sequence", results, logger, async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0; var stop = false;
            var task = BatchUninstallCoordinator.RunAsync(Apps(), async _ =>
            { calls++; entered.TrySetResult(); await release.Task; return Removed(); }, () => stop);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stop = true;
            if (task.IsCompleted || calls != 1) throw new Exception("Active operation was detached or a second item launched.");
            release.TrySetResult();
            var result = await task;
            if (calls != 1 || result.Successful != 1 || result.Skipped != 2 || !result.Stopped) throw new Exception("Stop accounting failed.");
            return "Current operation awaited; no pending application launched after stop.";
        });

        await RunTest("Phase10 unverified success is rejected", results, logger, async () =>
        {
            var calls = 0;
            var result = await BatchUninstallCoordinator.RunAsync(Apps(), _ =>
            { calls++; return Task.FromResult(new UninstallService.UninstallResult(true, 0, VerificationStatus.Unknown, "Launcher exited", "")); }, () => false);
            if (calls != 1 || result.Successful != 0 || !result.NeedsUserAttention) throw new Exception("An exit code became unverified success.");
            return "Unknown verification pauses even when an executor incorrectly sets Success=true.";
        });

        await RunTest("Phase10 cancellation prevents later launches", results, logger, async () =>
        {
            var calls = 0;
            var result = await BatchUninstallCoordinator.RunAsync(Apps(), _ =>
            { calls++; return Task.FromResult(new UninstallService.UninstallResult(false, -2, VerificationStatus.Unknown, "Cancelled before launch.", "", "Cancelled")); }, () => false);
            if (calls != 1 || result.Items[0].State != BatchItemState.Cancelled || result.Skipped != 2) throw new Exception("Cancelled operation advanced the queue.");
            return "Cancellation is distinct from failure and stops remaining work.";
        });

        await RunTest("Phase10 durable journal identities and interrupted recovery", results, logger, () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "Uninstaller77Journal_" + Guid.NewGuid().ToString("N"));
            try
            {
                var journal = new QueueJournal(folder);
                var now = DateTimeOffset.UtcNow;
                var snapshot = new QueueJournalSnapshot("queue-1", now, true, false,
                    Apps().Select(app => new QueueJournalItem(Guid.NewGuid().ToString("N"), app, UninstallOperationState.AwaitingCompletion, now, null)).ToArray());
                using (journal.AcquireExecutionLease())
                {
                    var refused = false;
                    try { using var second = journal.AcquireExecutionLease(); } catch (IOException) { refused = true; }
                    if (!refused) throw new Exception("Cross-process execution lease was not exclusive.");
                    journal.Save(snapshot);
                }
                var read = journal.Read()!;
                if (!QueueJournal.NeedsReview(read) || read.Items.Select(item => item.OperationId).Distinct().Count() != 3 ||
                    read.Items[0].App.Id == read.Items[1].App.Id || read.Items[0].App.Name != read.Items[1].App.Name || read.Items[0].StartedAt != now)
                    throw new Exception("Interrupted evidence or installation identities were lost.");
                return "Atomic journal retained exact app IDs, operation IDs and timestamps; interrupted work requires review.";
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });

        await RunTest("Phase10 early-exit launcher job tracks child lifetime", results, logger, async () =>
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
            using var execution = UninstallerProcess.Start(new ProcessStartInfo(exe, "--fixture-child 1800")
            { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            await execution.Root.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (execution.IsTreeComplete) throw new Exception("Worker disappeared from the job after the launcher exited.");
            var complete = await execution.WaitAsync(TimeSpan.FromSeconds(10), null);
            if (!complete || !execution.IsTreeComplete) throw new Exception("The worker was not awaited.");
            return "Real child process remained tracked after its parent exited; job became empty only after child exit.";
        });

        await RunTest("Phase10 process timeout retains live evidence without killing", results, logger, async () =>
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
            using var execution = UninstallerProcess.Start(new ProcessStartInfo(exe, "--fixture-delay 900")
            { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
            if (await execution.WaitAsync(TimeSpan.FromMilliseconds(50), null)) throw new Exception("A live process was reported complete.");
            if (execution.Root.HasExited) throw new Exception("Timeout terminated the fixture.");
            if (!await execution.WaitAsync(TimeSpan.FromSeconds(10), null)) throw new Exception("Timed-out process could not be verified later.");
            return "Timeout returned uncertainty; fixture stayed alive and later completion was observed.";
        });
    }
}
