using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunReviewObservationTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        await RunTest("Retry accepts completed system MSI semantics without weakening EXE tracking", results, logger, () =>
        {
            foreach (var code in new int?[] { 0, 1605, 3010 })
            {
                if (!UninstallService.CanFinalizeObservedProcess(true, false, false, code)) throw new Exception("A completed normalized MSI retry was stuck.");
                if (UninstallService.CanFinalizeObservedProcess(false, false, false, code) || UninstallService.CanFinalizeObservedProcess(false, true, false, code))
                    throw new Exception("An incomplete EXE tree was treated as complete.");
                if (!UninstallService.CanFinalizeObservedProcess(false, true, true, code)) throw new Exception("A completed job tree was rejected.");
            }
            if (UninstallService.CanFinalizeObservedProcess(true, false, false, null)) throw new Exception("A still-running MSI was finalized.");
            return "MSI retry uses the same synchronous exit boundary as launch; ordinary elevated EXEs and active MSI remain unresolved.";
        });
        await RunTest("Untracked completion preserves an observed exit without claiming success", results, logger, () =>
        {
            var exited = UninstallService.IncompleteProcessResult(true, 0);
            var running = UninstallService.IncompleteProcessResult(false, null);
            if (exited.Success || exited.ExitCode != 0 || exited.Verification != VerificationStatus.Unknown || exited.FailureCode != "UntrackedElevation")
                throw new Exception("An observed exit was lost or treated as proof of completion.");
            if (running.Success || running.ExitCode != -3 || running.FailureCode != "NeedsUserAttention")
                throw new Exception("Running uncertainty was misrepresented.");
            return "Observed exit 0 remains unknown with explicit elevation tracking diagnosis; running process retains unknown exit.";
        });
        await RunTest("Live review reconciles only a currently absent exact registration", results, logger, async () =>
        {
            var keyName="WinUninstaller_Test_"+Guid.NewGuid().ToString("N");
            var path=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\"+keyName;
            var app=new InstalledApp { Id=keyName,Source="Registry",Name="Owned review fixture",RegistryKeyPath="HKCU\\"+path,RegistryHive="HKCU" };
            var other=new InstalledApp { Id="other-"+keyName,Source="Registry",Name=app.Name,RegistryKeyPath=app.RegistryKeyPath+"Other" };
            try
            {
                using(var key=Registry.CurrentUser.CreateSubKey(path)) key.SetValue("DisplayName",app.Name);
                var present=await QueueReviewVerifier.ObserveAsync(app);
                if(present.State!="Present" || InventoryIntegrity.ReconcileRegistration([app,other],app,present.State).Count!=2) throw new Exception("Present registration was hidden.");
                Registry.CurrentUser.DeleteSubKeyTree(path,false); // Only the unique fixture created above.
                var absent=await QueueReviewVerifier.ObserveAsync(app);
                var catalog=InventoryIntegrity.ReconcileRegistration([app,other],app,absent.State);
                if(absent.State!="Absent" || catalog.Count!=1 || catalog[0].Id!=other.Id) throw new Exception("Absent observation did not reconcile the exact identity only.");
                var attempt=new UninstallService.UninstallResult(false,0,VerificationStatus.Unknown,"Historical uncertainty","", "NeedsUserAttention");
                if(BatchUninstallCoordinator.Classify(attempt)!=BatchItemState.NeedsAttention) throw new Exception("Current absence rewrote the historical outcome.");
                return "Real unique HKCU fixture disappeared from the cached catalog; same-name sibling and historical unknown result were preserved.";
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(path,false); }
        });
        await RunTest("Unknown or mismatched registration observations never hide an application", results, logger, () =>
        {
            var app=new InstalledApp{Id="owned",Source="Registry",RegistryKeyPath=@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Owned"};
            foreach(var state in new[]{"Unknown","Present","NotChecked"})
                if(InventoryIntegrity.ReconcileRegistration([app],app,state).Count!=1) throw new Exception("Non-absence hid an app.");
            foreach(var observed in new[]{new InstalledApp{Id="other",Source=app.Source,RegistryKeyPath=app.RegistryKeyPath},new InstalledApp{Id=app.Id,Source=app.Source,RegistryKeyPath=app.RegistryKeyPath+"Different"},new InstalledApp{Id=app.Id,Source="MSIX",RegistryKeyPath=app.RegistryKeyPath}})
                if(InventoryIntegrity.ReconcileRegistration([app],observed,"Absent").Count!=1) throw new Exception("Observation crossed an identity boundary.");
            return "Unknown/present observations, different IDs, sources and Registry keys preserve the catalog.";
        });
        await RunTest("Real WPF queue review stops, retains unknown evidence and acknowledges without launching", results, logger, async () =>
        {
            var ready = new TaskCompletionSource<System.Windows.Threading.Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                foreach (var name in new[] { "AccentGreen", "AccentRed", "AccentPeach", "AccentBlue" })
                    application.Resources[name] = System.Windows.Media.Brushes.Gray;
                ready.TrySetResult(application.Dispatcher);
                application.Run();
            }) { IsBackground = true, Name = "Owned queue review dispatcher" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            var dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Uninstaller77_Review_" + Guid.NewGuid().ToString("N"));
            try
            {
                await (await dispatcher.InvokeAsync(async () =>
                {
                    var extractor = new IconExtractionService();
                    var journal = new QueueJournal(root);
                    var vm = new WinUninstaller.ViewModels.AppListViewModel(new RegistryDiscoveryService(),
                        new IconCacheService(extractor, System.IO.Path.Combine(root, "Icons")),
                        new DatabaseService(System.IO.Path.Combine(root, "history.db")), new UninstallService(), new ResidualScanService(), journal);
                    var queue = vm.ShowAttentionFixtureAsync(); // Command-free owned Fixture identities only.
                    for (var i = 0; i < 100 && !vm.NeedsQueueReview; i++) await Task.Delay(20);
                    if (!vm.IsBatchRunning || !vm.NeedsQueueReview) throw new Exception("The fixture did not pause for review.");
                    await vm.RetryQueueVerificationAsync();
                    if (!vm.IsBatchRunning || !vm.NeedsQueueReview || vm.ReviewRegistrationState != "Unknown")
                        throw new Exception("Retry invented completion for a command-free fixture.");
                    vm.MarkQueueForReview();
                    await queue.WaitAsync(TimeSpan.FromSeconds(3));
                    if (vm.IsBatchRunning || !vm.IsProgressOpen || !vm.CanAcknowledgeReview || vm.BatchSuccessful != 0 || vm.BatchSkipped != 2)
                        throw new Exception("Stop did not reach visible stopped review with remaining items skipped.");
                    var id = vm.ReviewQueueId;
                    vm.AcknowledgeUnresolvedQueue(id);
                    try { await vm.ConfirmUnregisteredAsync(Guid.NewGuid().ToString("N"), true); throw new Exception("Unissued plan was accepted."); }
                    catch (System.IO.InvalidDataException) { /* No current reviewed capability exists. */ }
                    var saved = journal.Read()!;
                    vm.SelectAllVisible(true);
                    if (!vm.IsAllVisibleSelected || vm.SelectedCount != 3) throw new Exception("Visible select-all did not select the fixture catalog.");
                    vm.SearchText = vm.Apps[0].Name;
                    if (vm.Apps.Count != 1 || vm.SelectedCount != 1) throw new Exception("Hidden selections inflated the visible removal count.");
                    vm.Apps[0].IsBatchSelected = false;
                    if (vm.IsAllVisibleSelected || vm.SelectedCount != 0) throw new Exception("Individual deselection left select-all stale.");
                    vm.SearchText = "";
                    vm.IsCleaningResiduals = true;
                    try { vm.RequireMaintenanceIdle(); throw new Exception("Concurrent maintenance was accepted."); }
                    catch (InvalidOperationException ex) when (ex.Message == "UnregisteredApp:OperationBusy") { }
                    vm.IsCleaningResiduals = false;
                    if (vm.IsProgressOpen || saved.Active || saved.AcknowledgedUnresolvedAt is null ||
                        saved.Items[0].State != UninstallOperationState.NeedsUserAttention || saved.Items.Skip(1).Any(item => item.StartedAt is not null))
                        throw new Exception("Acknowledgment lost unknown evidence or launched a pending item.");
                    var profile = System.IO.Path.Combine(root, "OwnedProfile");
                    var deployment = System.IO.Path.Combine(profile, "Apps", "OwnedApp");
                    System.IO.Directory.CreateDirectory(deployment);
                    var target = System.IO.Path.Combine(deployment, "OwnedApp.exe");
                    System.IO.File.Copy(Environment.ProcessPath!, target);
                    var store = System.IO.Path.Combine(profile, "Quarantine");
                    var portable = new WinUninstaller.ViewModels.AppListViewModel(new RegistryDiscoveryService(),
                        new IconCacheService(extractor, System.IO.Path.Combine(root, "PortableIcons")),
                        new DatabaseService(System.IO.Path.Combine(root, "portable-history.db")), new UninstallService(), new ResidualScanService(),
                        new QueueJournal(System.IO.Path.Combine(root, "PortableQueue")), new UnregisteredAppService(profile, store, AppContext.BaseDirectory));
                    await portable.ReviewUnregisteredAsync(target);
                    var capability = portable.UnregisteredPlan?.Id ?? throw new Exception("Owned portable review did not open.");
                    var changed = System.IO.Path.Combine(deployment, "unreviewed.txt");
                    System.IO.File.WriteAllText(changed, "Added after review");
                    await portable.ConfirmUnregisteredAsync(capability, true);
                    if (portable.IsUnregisteredBusy || portable.UnregisteredState != "Review" || string.IsNullOrWhiteSpace(portable.UnregisteredError) || !System.IO.File.Exists(target))
                        throw new Exception("Refused deletion did not return to a usable review state.");
                    System.IO.File.Delete(changed); // Owned fixture only; restore its reviewed contents for the next assertion.
                    await portable.ConfirmUnregisteredAsync(capability, true);
                    if (portable.UnregisteredState != "Deleted" || System.IO.File.Exists(target) || portable.UnregisteredDeletion?.Complete != true)
                        throw new Exception("Real ViewModel permanent deletion failed: " + portable.UnregisteredError);
                    portable.CloseUnregisteredReview();
                    // Previous reversible backups remain restorable from Control Center.
                    System.IO.Directory.CreateDirectory(deployment); System.IO.File.Copy(Environment.ProcessPath!, target);
                    var legacyService = new UnregisteredAppService(profile, store, AppContext.BaseDirectory);
                    var legacyPlan = legacyService.Prepare(target, []);
                    var receipt = legacyService.Quarantine(legacyPlan, []);
                    if (!portable.IsUnregisteredBackup(receipt)) throw new Exception("Persistent restore receipt was not recognized.");
                    await portable.RestoreUnregisteredAsync(receipt);
                    if (!portable.IsUnregisteredOpen || portable.UnregisteredState != "Restored" || !System.IO.File.Exists(target) || portable.UnregisteredIdentity?.Name != "OwnedApp")
                        throw new Exception("Control-center restore failed: " + portable.UnregisteredError);
                    var noOp = new WinUninstaller.ViewModels.AppListViewModel(new RegistryDiscoveryService(),
                        new IconCacheService(extractor, System.IO.Path.Combine(root, "NoOpIcons")),
                        new DatabaseService(System.IO.Path.Combine(root, "noop-history.db")), new UninstallService(), new ResidualScanService(),
                        new QueueJournal(System.IO.Path.Combine(root, "NoOpQueue")));
                    var ownedQueue = Enumerable.Range(1, 3).Select(i => new InstalledApp { Id = "OwnedNoOp_" + i, Name = "Owned no-op " + i, Source = "Fixture" }).ToList();
                    Func<InstalledApp, Task<UninstallService.UninstallResult>> completedNoOp = _ => Task.FromResult(
                        UninstallService.InterpretCompletedProcessResult(0, VerificationStatus.StillPresent, false));
                    var execute = typeof(WinUninstaller.ViewModels.AppListViewModel).GetMethod("RunQueueAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    await (Task)execute.Invoke(noOp, [ownedQueue, false, false, completedNoOp])!;
                    if (noOp.IsBatchRunning || noOp.NeedsQueueReview || noOp.BatchState != BatchQueueState.PartialFailure ||
                        noOp.BatchSuccessful != 0 || noOp.BatchFailed != 1 || noOp.BatchSkipped != 2 || !noOp.IsProgressOpen ||
                        noOp.QueueItems.Skip(1).Any(item => item.StartedAt is not null))
                        throw new Exception("A completed no-op ViewModel queue did not reach finite, closable results without another launch.");
                    noOp.CloseProgress();
                    if (noOp.OperationState != UninstallOperationState.Failed)
                        throw new Exception("The failed current application's header phase was overwritten as cancelled after a policy stop.");
                    if (noOp.IsProgressOpen) throw new Exception("Completed no-op results could not be closed normally.");
                    var selected = ownedQueue.Take(2).ToList();
                    Func<InstalledApp, Task<UninstallService.UninstallResult>> vendorThenSuccess = app => Task.FromResult(app.Id == selected[0].Id
                        ? new UninstallService.UninstallResult(false, 0, VerificationStatus.StillPresent, VendorUninstallDiagnostics.RiotDependencyMessage, VendorUninstallDiagnostics.RiotDependencyAdvice, "VendorDependenciesInstalled")
                        : new UninstallService.UninstallResult(true, 0, VerificationStatus.VerifiedRemoved, "Owned selected fixture removed.", ""));
                    await (Task)execute.Invoke(noOp, [selected, false, false, vendorThenSuccess])!;
                    if (noOp.IsBatchRunning || noOp.NeedsQueueReview || noOp.BatchFailed != 1 || noOp.BatchSuccessful != 1 || noOp.BatchSkipped != 0 || noOp.BatchCompleted != 2 ||
                        noOp.QueueItems[0].State != UninstallOperationState.Failed || !noOp.QueueItems[0].Message.Contains(VendorUninstallDiagnostics.RiotDependencyMessage) ||
                        noOp.QueueItems[1].State != UninstallOperationState.Completed || noOp.ProgressApp?.Id != selected[1].Id)
                        throw new Exception("Completed vendor refusal stopped the ViewModel or lost its own diagnosis after the next selected fixture succeeded.");
                    noOp.CloseProgress();
                    Func<InstalledApp, Task<UninstallService.UninstallResult>> removedPeer = app => Task.FromResult(app.Id == selected[0].Id
                        ? new UninstallService.UninstallResult(true, 0, VerificationStatus.VerifiedRemoved, "Owned parent removed.", "") : UninstallService.AlreadyAbsentResult());
                    await (Task)execute.Invoke(noOp, [selected, false, false, removedPeer])!;
                    var skippedRecord = noOp.Database.GetHistory().First();
                    if (noOp.BatchState != BatchQueueState.CompletedWithSkips || noOp.BatchSuccessful != 1 || noOp.BatchFailed != 0 || noOp.BatchSkipped != 1 || noOp.BatchCompleted != 1 ||
                        noOp.QueueItems[1].State != UninstallOperationState.NotInstalled || noOp.QueueItems[1].ExitCode is not null || noOp.HasPendingQueueReview ||
                        skippedRecord.Success || skippedRecord.Outcome != "NotInstalled")
                        throw new Exception("Already-absent peer failed real ViewModel accounting, terminal journal state or honest history outcome.");
                    noOp.CloseProgress();
                })).WaitAsync(TimeSpan.FromSeconds(15));
                return "Live WPF dispatcher/ViewModel: review/stop/acknowledge, finite closable no-op results (1 failed, 2 skipped, no auto launch), plus owned permanent file deletion and legacy backup restore.";
            }
            finally
            {
                await dispatcher.InvokeAsync(() => System.Windows.Application.Current.Shutdown());
                thread.Join(TimeSpan.FromSeconds(3));
            }
        });
    }
}
