using System.IO;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunExitDiagnosticsTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        await RunTest("MSI 1605 preserves unknown-product failure for every verification observation", results, logger, () =>
        {
            foreach (var evidence in Enum.GetValues<VerificationStatus>())
            {
                var result = UninstallService.InterpretCompletedProcessResult(1605, evidence, true);
                if (result.Success || result.ExitCode != 1605 || result.Verification != evidence || result.FailureCode != "MsiUnknownProduct" ||
                    result.StatusMessage != "Windows Installer does not recognize the registered product." || !result.Error.Contains("No successful removal was verified."))
                    throw new Exception("1605 was misreported: " + result);
            }
            return "Never attributed another operation's missing registration or an unrecognized product to successful removal.";
        });
        await RunTest("Completed process diagnostics retain success evidence and reject non-success exits", results, logger, () =>
        {
            foreach (var code in new[] { 0, 3010, 1603, 1605, 1618 })
            foreach (var evidence in Enum.GetValues<VerificationStatus>())
            {
                var result = UninstallService.InterpretCompletedProcessResult(code, evidence, true);
                var expected = code is 0 or 3010 && evidence is VerificationStatus.VerifiedRemoved or VerificationStatus.PartiallyRemoved;
                if (result.Success != expected || (!expected && result.FailureCode == "None"))
                    throw new Exception("Invalid success/failure diagnostic: " + result);
            }
            var unrelated = UninstallService.InterpretCompletedProcessResult(1605, VerificationStatus.StillPresent, false);
            if (unrelated.FailureCode == "MsiUnknownProduct") throw new Exception("An arbitrary EXE's exit was mislabeled as a Windows Installer error.");
            return "Only valid exits plus verified evidence succeed; known MSI errors have a cause without changing the evidence.";
        });
        await RunTest("Result evidence omits the None sentinel and duplicate exit lines", results, logger, () =>
        {
            var result = new UninstallService.UninstallResult(false, 1605, VerificationStatus.StillPresent, "Owned fixture", "Exit code: 1605", ExitObserved: true);
            var text = UninstallService.FormatResultEvidence(result);
            if (text.Split('\n').Contains("None") || text.Split("Exit code: 1605").Length != 2 || !text.Contains("Owned fixture"))
                throw new Exception("Evidence formatting lost or duplicated diagnostic data: " + text);
            return "No meaningless failure-code sentinel; actual error and exact exit remain recorded.";
        });
        await RunTest("Owned real executable exit 1605 remains unsuccessful without a fabricated MSI identity", results, logger, async () =>
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
            var fixture = new InstalledApp
            {
                Id = "owned-exit-1605", Name = "Owned exit diagnostic fixture", RegistryHive = "HKCU",
                RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + Guid.NewGuid().ToString("N"),
                UninstallString = $"\"{exe}\" --fixture-exit 1605"
            };
            var fixtureKey = fixture.RegistryKeyPath[5..];
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(fixtureKey))
            { key.SetValue("DisplayName", fixture.Name); key.SetValue("UninstallString", fixture.UninstallString); }
            try
            {
                var result = await _uninstaller.UninstallAsync(fixture);
                if (result.Success || result.ExitCode != 1605 || result.FailureCode != "UninstallerExitError")
                    throw new Exception("Owned process failure was misreported: " + result);
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(fixtureKey, false); }
            return "Started only our test runner child; captured actual exit 1605. No MSI or user program was invoked.";
        });
        await RunTest("Mixed queue does not count MSI 1605 as successful or skipped", results, logger, async () =>
        {
            var apps = new[] { new InstalledApp { Id = "owned-ok", Name = "Owned success" }, new InstalledApp { Id = "owned-1605", Name = "Owned MSI error" } };
            var batch = await BatchUninstallCoordinator.RunAsync(apps, app => Task.FromResult(
                UninstallService.InterpretCompletedProcessResult(app.Id == "owned-ok" ? 0 : 1605,
                    app.Id == "owned-ok" ? VerificationStatus.VerifiedRemoved : VerificationStatus.StillPresent, true)), () => false);
            if (batch.Successful != 1 || batch.Failed != 1 || batch.Skipped != 0 || batch.State != BatchQueueState.PartialFailure || batch.Items[1].ExitCode != 1605)
                throw new Exception("Mixed queue accounting did not preserve the MSI failure.");
            return "One verified success and one failure; unknown product was never converted to a successful uninstall.";
        });
        await RunTest("Vendor exit values 1223 and -2 are failures of that program, never a cancellation", results, logger, () =>
        {
            foreach (var code in new[] { 1223, -2 })
            {
                var result = UninstallService.InterpretCompletedProcessResult(code, VerificationStatus.StillPresent, false);
                if (BatchUninstallCoordinator.Classify(result) != BatchItemState.Failed || !result.ExitObserved || result.FailureCode != "UninstallerExitError")
                    throw new Exception("Observed vendor exit was treated as cancellation: " + result);
            }
            var uac = new UninstallService.UninstallResult(false, 1223, VerificationStatus.StillPresent, "UAC Prompt was cancelled by user.", "denied", "UacDenied");
            if (BatchUninstallCoordinator.Classify(uac) != BatchItemState.Cancelled || UninstallService.FormatResultEvidence(uac).Contains("Exit code"))
                throw new Exception("UAC denial lost its identity or reported a fake exit code.");
            return "Only the explicit UAC/cancel codes cancel the queue; observed exits keep their meaning.";
        });
        await RunTest("Vendor exit numbers alone never prove missing privileges; exits require observation", results, logger, () =>
        {
            var vendorFive = UninstallService.InterpretCompletedProcessResult(5, VerificationStatus.StillPresent, false);
            var vendor740 = UninstallService.InterpretCompletedProcessResult(740, VerificationStatus.StillPresent, false);
            var refused = UninstallService.InterpretCompletedProcessResult(93, VerificationStatus.StillPresent, false);
            var notLaunched = new UninstallService.UninstallResult(false, -1, VerificationStatus.StillPresent, "Uninstaller executable was not found.", "x.exe", "MissingExecutable");
            if (vendorFive.FailureCode != "UninstallerExitError" || vendor740.FailureCode != "UninstallerExitError" || refused.FailureCode != "UninstallerExitError")
                throw new Exception("Failure causes were conflated.");
            if (!UninstallService.FormatResultEvidence(refused).Contains("Exit code: 93") || UninstallService.FormatResultEvidence(notLaunched).Contains("Exit code"))
                throw new Exception("Exit evidence is missing for an exited process or invented for an unlaunched one.");
            if (UninstallService.InterpretCompletedProcessResult(5, VerificationStatus.StillPresent, true).FailureCode == "InsufficientPrivileges")
                throw new Exception("An MSI exit was relabelled as a privilege problem.");
            return "Vendor-defined 5/740 remain vendor failures, distinct from native launch errors; no sentinel exit is displayed.";
        });
        await RunTest("Real invalid executable launch is definite and the next owned item runs", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            var root = Path.Combine(Path.GetTempPath(), "Uninstaller77_LaunchFailure_" + id);
            Directory.CreateDirectory(root);
            var executable = Path.Combine(root, "Invalid.exe"); File.WriteAllText(executable, "Not an executable; owned fixture only");
            var keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + id;
            var app = new InstalledApp { Id = id, Name = "Owned invalid executable", RegistryHive = "HKCU", RegistryKeyPath = "HKCU\\" + keyPath, UninstallString = $"\"{executable}\"" };
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
            { key.SetValue("DisplayName", app.Name); key.SetValue("UninstallString", app.UninstallString); }
            try
            {
                var calls = 0;
                var batch = await BatchUninstallCoordinator.RunAsync(new[] { app, new InstalledApp { Id = "next", Name = "Owned next" } }, async item =>
                { calls++; return item == app ? await _uninstaller.UninstallAsync(app) : UninstallService.InterpretCompletedProcessResult(0, VerificationStatus.VerifiedRemoved, false); }, () => false);
                if (calls != 2 || batch.Failed != 1 || batch.Successful != 1 || batch.Items[0].ExitCode is not null || batch.Items[0].FailureCode != "LaunchFailed" || UninstallService.HasPendingOperation)
                    throw new Exception("Definite unstarted failure blocked the next item or invented exit evidence.");
                return "CreateProcess rejected an actual invalid owned EXE; no child started, no fake exit, next fixture continued.";
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
        });
        await RunTest("History never displays sentinel exits for unlaunched attempts", results, logger, () =>
        {
            var missing = new UninstallRecord { ExitCode = -1, Outcome = "Failed", Notes = "Uninstaller executable was not found.\nMissingExecutable" };
            var observed = new UninstallRecord { ExitCode = -2, Outcome = "Failed", Notes = "Uninstaller returned a failure exit code.\nUninstallerExitError\nExit code: -2" };
            if (missing.ObservedExitCode is not null || observed.ObservedExitCode != -2)
                throw new Exception("History manufactured an exit or hid a real observed vendor exit.");
            return "No database change or rewrite of historical numeric fields; display requires explicit matching evidence.";
        });
        await RunTest("Journal round-trip preserves per-item failures, observed exits and old optional-field compatibility", results, logger, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Uninstaller77_ExitJournal_" + Guid.NewGuid().ToString("N"));
            var journal = new QueueJournal(root);
            journal.Save(new("owned-queue", DateTimeOffset.UtcNow, false, false, new[] {
                new QueueJournalItem("one", new InstalledApp { Id = "failed" }, UninstallOperationState.Failed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "failure", 93, "UninstallerExitError"),
                new QueueJournalItem("two", new InstalledApp { Id = "ok" }, UninstallOperationState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "removed", 0) }));
            var read = new QueueJournal(root).Read()!;
            if (read.Items[0].ExitCode != 93 || read.Items[0].FailureCode != "UninstallerExitError" || read.Items[1].FailureCode != "")
                throw new Exception("Journal dropped per-item diagnosis.");
            File.WriteAllText(Path.Combine(root, "queue.json"), "{\"QueueId\":\"legacy\",\"CreatedAt\":\"2026-01-01T00:00:00Z\",\"Active\":false,\"StopRequested\":false,\"Items\":[{\"OperationId\":\"old\",\"App\":{},\"State\":7,\"StartedAt\":null,\"FinishedAt\":null}]}");
            if (journal.Read()!.Items[0].FailureCode != "") throw new Exception("Legacy journal optional field failed.");
            return "Saved/reloaded exact failure 93 after later success; legacy journal still reads without invented diagnosis.";
        });
        await RunTest("MSIX evaluated completion carries observed exit; prelaunch cancellation has none", results, logger, async () =>
        {
            var result = MsixDeploymentService.Evaluate(5, new("owned", "owned", "Protected", "fixture", ""), new(VerificationStatus.StillPresent, "", "", 0), "");
            if (!result.ExitObserved || !UninstallService.FormatResultEvidence(result).Contains("Exit code: 5")) throw new Exception("Lost MSIX worker exit.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var unstarted = await _uninstaller.UninstallAsync(new InstalledApp { Id = "owned-cancel", Name = "Owned cancellation" }, ct: cancelled.Token);
            if (unstarted.ExitObserved || unstarted.FailureCode != "Cancelled" || BatchUninstallCoordinator.Classify(unstarted) != BatchItemState.Cancelled)
                throw new Exception("Prelaunch cancellation invented a process exit.");
            return "Pure MSIX evaluator and cancelled owned request only; no real package was removed.";
        });
        await RunTest("Queue keeps Edge-style exit diagnosis after a later item succeeds", results, logger, async () =>
        {
            var apps = new[] { new InstalledApp { Id = "q-absent", Name = "Absent" }, new InstalledApp { Id = "q-fail", Name = "Failing" }, new InstalledApp { Id = "q-ok", Name = "Removed" } };
            var batch = await BatchUninstallCoordinator.RunAsync(apps, app => Task.FromResult(app.Id switch
            {
                "q-absent" => UninstallService.AlreadyAbsentResult(),
                "q-fail" => UninstallService.InterpretCompletedProcessResult(93, VerificationStatus.StillPresent, false),
                _ => UninstallService.InterpretCompletedProcessResult(0, VerificationStatus.VerifiedRemoved, false)
            }), () => false);
            if (batch.Skipped != 1 || batch.Failed != 1 || batch.Successful != 1 || batch.Items[1].ExitCode != 93 || batch.Items[1].FailureCode != "UninstallerExitError")
                throw new Exception("Per-item diagnosis was lost or miscounted.");
            return "Skipped, failed with exit 93, then verified success; the failed item retains code and exit.";
        });
    }
}
