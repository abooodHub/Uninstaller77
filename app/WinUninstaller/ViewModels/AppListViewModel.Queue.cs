using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class AppListViewModel
{
    private readonly QueueJournal _journal;
    private QueueJournalSnapshot? _queueSnapshot;
    private QueueStopController _queueControl = new();
    private bool _closeAfterReview;
    private bool _recoveringQueue;
    public bool IsQueuePreview { get; private set; }
    [ObservableProperty] private UninstallOperationState _operationState = UninstallOperationState.Idle;
    [ObservableProperty] private bool _verificationRetrying;
    [ObservableProperty] private string _attentionDetails = "";
    [ObservableProperty] private string _attentionFailureCode = "";
    [ObservableProperty] private int? _attentionExitCode;
    [ObservableProperty] private ResidualScanState _residualState = ResidualScanState.NotScanned;
    public IReadOnlyList<QueueJournalItem> QueueItems => _queueSnapshot?.Items ?? Array.Empty<QueueJournalItem>();
    public bool NeedsQueueReview => _queueSnapshot?.AcknowledgedUnresolvedAt is null && (_recoveringQueue || OperationState == UninstallOperationState.NeedsUserAttention);
    public bool HasPendingQueueReview => !IsBatchRunning && QueueJournal.NeedsReview(_queueSnapshot);
    public bool CanAcknowledgeReview => !IsBatchRunning && !VerificationRetrying && _queueSnapshot is not null && NeedsQueueReview;
    public string ReviewQueueId => _queueSnapshot?.QueueId ?? "";
    public QueueJournalSnapshot? ReviewSnapshot => _queueSnapshot;
    [ObservableProperty] private string _reviewObservation = "";
    [ObservableProperty] private string _reviewRegistrationState = "NotChecked";
    public bool CanCancelCurrent => IsBatchRunning && OperationState == UninstallOperationState.Preparing;
    public int QueueRemaining => QueueItems.Count(item => item.StartedAt is null && item.State != UninstallOperationState.Cancelled);
    partial void OnOperationStateChanged(UninstallOperationState value)
    { OnPropertyChanged(nameof(NeedsQueueReview)); OnPropertyChanged(nameof(CanCancelCurrent)); }

    private void RecoverInterruptedQueue()
    {
        try
        {
            var saved = _journal.Read();
            if (!QueueJournal.NeedsReview(saved)) return;
            _queueSnapshot = saved!.InterruptionDetectedAt is null
                ? _journal.RecordReview(saved.QueueId, "RecoveryDetected", "Interruption detected on application startup; no operation resumed.") : saved;
            _recoveringQueue = true;
            IsQueuePreview = Environment.GetCommandLineArgs().Contains("--ui-phase11-test");
            OperationState = UninstallOperationState.NeedsUserAttention;
            BatchState = BatchQueueState.NeedsUserAttention;
            IsBatchQueue = true;
            BatchTotal = saved.Items.Count;
            BatchCompleted = saved.Items.Count(item => item.StartedAt is not null);
            BatchResults = new(saved.Items.Where(item => item.FinishedAt is not null).Select(item =>
                ToBatchDisplay(new(item.App, item.State == UninstallOperationState.Completed ? BatchItemState.Succeeded
                    : item.State == UninstallOperationState.Failed ? BatchItemState.Failed : item.State == UninstallOperationState.NotInstalled ? BatchItemState.Skipped : BatchItemState.Cancelled, item.Message, item.ExitCode, item.FailureCode))));
            ProgressTitle = "Uninstaller 77";
            ProgressStatus = LanguageCode == "ar" ? "انقطع طابور سابق. راجع حالة البرامج يدويًا؛ لن تُستأنف أي إزالة تلقائيًا." : "A previous queue was interrupted. Review the applications manually; no removal will resume automatically.";
            CurrentStage = ProgressStatus;
            ProgressApp = _queueSnapshot.Items.FirstOrDefault(item => item.State == UninstallOperationState.NeedsUserAttention || item.StartedAt is not null && item.FinishedAt is null)?.App ?? _queueSnapshot.Items.FirstOrDefault()?.App;
            AttentionDetails = UiStrings.Get(LanguageCode, "queueReviewUncertain");
            ReviewObservation = _queueSnapshot.Reviews?.LastOrDefault()?.Evidence ?? "";
            IsProgressOpen = true;
        }
        catch (Exception ex)
        {
            // A corrupt journal must not permit an overlapping destructive operation.
            _recoveringQueue = true;
            OperationState = UninstallOperationState.NeedsUserAttention;
            SetWebNotice("Queue recovery requires manual review: " + ex.Message, true);
        }
    }

    private void UpdateQueueItem(InstalledApp app, UninstallOperationState state, string message = "", int? exitCode = null, string failureCode = "")
    {
        OperationState = state;
        if (_queueSnapshot is null) return;
        var now = DateTimeOffset.Now;
        _queueSnapshot = _queueSnapshot with
        {
            StopRequested = StopBatchAfterCurrent,
            Items = _queueSnapshot.Items.Select(item => item.App.Id != app.Id ? item : item with
            {
                State = state, StartedAt = item.StartedAt ?? now,
                FinishedAt = state is UninstallOperationState.Completed or UninstallOperationState.Failed or UninstallOperationState.Cancelled or UninstallOperationState.NotInstalled ? now : null,
                Message = message, ExitCode = exitCode, FailureCode = failureCode
            }).ToArray()
        };
        _journal.Save(_queueSnapshot);
        OnPropertyChanged(nameof(QueueItems)); OnPropertyChanged(nameof(QueueRemaining));
    }

    private async Task RunQueueAsync(List<InstalledApp> queue, bool quiet, bool scanResiduals,
        Func<InstalledApp, Task<UninstallService.UninstallResult>>? fixtureExecutor = null)
    {
        if (queue.Count == 0 || IsBatchRunning || IsUnregisteredBusy || IsCleaningResiduals) return;
        IsQueuePreview = fixtureExecutor is not null;
        if (fixtureExecutor is not null && queue.Any(app => app.Source != "Fixture" || !string.IsNullOrWhiteSpace(app.UninstallString)))
            throw new InvalidOperationException("Only isolated command-free fixtures may use the test executor.");
        if (_recoveringQueue || QueueJournal.NeedsReview(_journal.Read()))
        { IsProgressOpen = true; SetWebNotice(LanguageCode == "ar" ? "توجد عملية غير محسومة تحتاج مراجعة أولًا." : "An unresolved operation needs review first.", true); return; }
        if (fixtureExecutor is null && UninstallService.HasPendingOperation)
        {
            SetWebNotice(LanguageCode == "ar" ? "توجد أداة إزالة لم يُثبت انتهاؤها بعد. أعد التحقق من المحاولة السابقة؛ لم يبدأ أي برنامج جديد."
                : "An earlier uninstaller has not completed reliably. Recheck that attempt; no new application was started.", true);
            return;
        }
        using var lease = _journal.AcquireExecutionLease();
        var now = DateTimeOffset.Now;
        foreach (var app in queue)
            app.CachedIconPath = _iconCache.PersistIcon(string.IsNullOrWhiteSpace(app.RegistryKeyPath) ? app.Id : app.RegistryKeyPath, app.IconImage, app.Name);
        _queueSnapshot = new(Guid.NewGuid().ToString("N"), now, true, false,
            queue.Select(app => new QueueJournalItem(Guid.NewGuid().ToString("N"), app, UninstallOperationState.Idle, null, null)).ToArray());
        _journal.Save(_queueSnapshot);
        IsBatchRunning = true; IsBatchQueue = true; BatchResults.Clear();
        _queueControl = new(); _closeAfterReview = false;
        AttentionDetails = ""; AttentionFailureCode = ""; AttentionExitCode = null;
        BatchState = BatchQueueState.Running; StopBatchAfterCurrent = false;
        OperationState = UninstallOperationState.Preparing;
        BatchTotal = queue.Count; BatchCompleted = 0;
        _queueStartedAt = DateTime.Now; ElapsedTime = "00:00:00"; _queueClock.Start();
        IsProgressOpen = true; IsCurrentOperationIndeterminate = true;
        ProgressTitle = UiStrings.Get(LanguageCode, "queueTitle"); ProgressLogs.Clear();
        OnPropertyChanged(nameof(QueueItems));
        try
        {
            var runResult = await BatchUninstallCoordinator.RunAsync(queue, async app =>
            {
                ProgressApp = app;
                ReviewRegistrationState = "NotChecked"; ReviewObservation = "";
                _uninstallCts?.Dispose(); _uninstallCts = new();
                UpdateQueueItem(app, UninstallOperationState.Preparing);
                var result = fixtureExecutor is not null ? await fixtureExecutor(app) : await _uninstaller.UninstallAsync(app, quiet,
                    new DispatcherProgress<string>(_dispatcher, message =>
                    {
                        var shown = LocalizeEngineProgress(message, app);
                        CurrentStage = shown; ProgressStatus = shown;
                        ProgressLogs.Add($"[{DateTime.Now:HH:mm:ss}] {shown}");
                    }), _uninstallCts.Token,
                    new DispatcherProgress<UninstallOperationState>(_dispatcher, state => UpdateQueueItem(app, state)));

                AttentionDetails = result.Error;
                AttentionFailureCode = result.FailureCode;
                AttentionExitCode = result.FailureCode == "RegistrationAbsent" || !result.ExitObserved ? null : result.ExitCode;
                if (!result.Success)
                    ProgressLogs.Add($"[{DateTime.Now:HH:mm:ss}] {UninstallService.FormatResultEvidence(result).ReplaceLineEndings(" | ")}");

                if (BatchUninstallCoordinator.Classify(result) == BatchItemState.NeedsAttention)
                {
                    UpdateQueueItem(app, UninstallOperationState.NeedsUserAttention,
                        UninstallService.FormatResultEvidence(result), result.ExitObserved ? result.ExitCode : null, ItemFailureCode(result));
                    await ObserveReviewRegistrationAsync(app);
                    CurrentStage = ProgressStatus = LanguageCode == "ar"
                        ? "تعذر تأكيد انتهاء أداة الإزالة. الطابور متوقف للمراجعة؛ لن يبدأ البرنامج التالي."
                        : "Completion is uncertain. The queue is paused for review; the next application will not start.";
                    var reviewed = await _queueControl.WaitForReviewAsync();
                    if (reviewed is not null) result = reviewed;
                }

                var itemState = BatchUninstallCoordinator.Classify(result);
                var terminal = itemState switch
                { BatchItemState.Succeeded => UninstallOperationState.Completed, BatchItemState.Cancelled => UninstallOperationState.Cancelled,
                    BatchItemState.Skipped => UninstallOperationState.NotInstalled,
                    BatchItemState.NeedsAttention => UninstallOperationState.NeedsUserAttention, _ => UninstallOperationState.Failed };
                UpdateQueueItem(app, terminal, UninstallService.FormatResultEvidence(result),
                    itemState == BatchItemState.Skipped || !result.ExitObserved ? null : result.ExitCode, ItemFailureCode(result));
                if (itemState != BatchItemState.Skipped) BatchCompleted++;
                BatchResults.Add(ToBatchDisplay(new(app, itemState, result.StatusMessage + " " + result.Error, result.ExitObserved ? result.ExitCode : null, ItemFailureCode(result))));
                try
                {
                    LastInsertedRecordId = _db.AddRecord(new UninstallRecord
                    {
                        AppId = app.Id, AppName = app.Name, Publisher = app.Publisher, Version = app.Version,
                        CachedIconPath = app.CachedIconPath, UninstallDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        InstallerType = app.TypeDisplay, ExitCode = result.ExitCode, Success = itemState == BatchItemState.Succeeded, Outcome = itemState == BatchItemState.Skipped ? "NotInstalled" : itemState.ToString(),
                        ResidualStatus = "ResidualScan:NotScanned", Notes = UninstallService.FormatResultEvidence(result) + "\nOperation: " + _queueSnapshot!.Items.First(item => item.App.Id == app.Id).OperationId
                    });
                }
                catch (Exception ex) { throw new BatchHistoryWriteException(app, result, ex); }
                if (itemState is BatchItemState.Succeeded or BatchItemState.Skipped)
                {
                    _allApps.Remove(app); Apps.Remove(app); app.IsBatchSelected = false; UpdateCounts();
                }
                OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchProgressText));
                return result;
            }, () => _queueControl.StopRequested);

            BatchResults = new ObservableCollection<BatchResultDisplay>(runResult.Items.Select(ToBatchDisplay));
            BatchState = runResult.State;
            _queueSnapshot = _queueSnapshot with
            {
                Active = false, StopRequested = StopBatchAfterCurrent,
                Items = _queueSnapshot.Items.Select(item => item.StartedAt is null
                    ? item with { State = UninstallOperationState.Cancelled, FinishedAt = DateTimeOffset.Now, Message = "Not started." } : item).ToArray()
            };
            _journal.Save(_queueSnapshot);
            OperationState = runResult.NeedsUserAttention ? UninstallOperationState.NeedsUserAttention
                : runResult.Failed > 0 ? UninstallOperationState.Failed : runResult.Stopped ? UninstallOperationState.Cancelled : UninstallOperationState.Completed;
            ProgressStatus = CurrentStage = UiStrings.Get(LanguageCode, runResult.NeedsUserAttention ? "queueNeedsAttention" : runResult.Stopped ? "queueStoppedAfter" : "queueFinished");
            ProgressLogs.Add($"[{DateTime.Now:HH:mm:ss}] {ProgressStatus}");
            OnPropertyChanged(nameof(QueueItems)); OnPropertyChanged(nameof(HistoryCount));
            // Residual review is never a deletion. Keep each successful single-app scan explicit.
            if (queue.Count == 1 && runResult.Successful == 1 && scanResiduals && !StopBatchAfterCurrent)
                await ReviewSingleResidualsAsync(queue[0]);
        }
        catch (Exception ex)
        {
            OperationState = UninstallOperationState.NeedsUserAttention;
            BatchState = BatchQueueState.CriticalFailure;
            ProgressStatus = CurrentStage = ex.Message;
            SetWebNotice(ex.Message, true);
            // Leave the journal active: a restart must show interrupted evidence.
        }
        finally
        {
            _queueClock.Stop(); IsBatchRunning = false; IsCurrentOperationIndeterminate = false;
            _uninstallCts?.Dispose(); _uninstallCts = null;
            if (_closeAfterReview) IsProgressOpen = false;
            OnPropertyChanged(nameof(QueueRemaining)); OnPropertyChanged(nameof(CanCancelCurrent));
        }
    }

    private static string ItemFailureCode(UninstallService.UninstallResult result) => result.FailureCode == "None" ? "" : result.FailureCode;

    private async Task ReviewSingleResidualsAsync(InstalledApp app)
    {
        OperationState = UninstallOperationState.ScanningResiduals;
        ResidualState = ResidualScanState.Scanning;
        ResidualItems.Clear(); ResidualScannedLocations = 0;
        _db.UpdateResidualInfo(LastInsertedRecordId, "ResidualScan:Scanning", 0, 0);
        CurrentStage = UiStrings.Get(LanguageCode, "scanningResiduals");
        var scan = await _residualScanner.ScanResidualsAsync(app, CancellationToken.None,
            new DispatcherProgress<ResidualScanService.ScanProgress>(_dispatcher, value =>
            { ResidualScanStage = LocalizeResidualStage(value.Stage); ResidualScannedLocations = value.ScannedLocations; }));
        ResidualState = scan.State;
        ResidualScannedLocations = scan.ScannedLocations;
        ResidualStatusMessage = scan.Message(LanguageCode);
        _db.UpdateResidualInfo(LastInsertedRecordId, scan.StorageStatus, 0, 0);
        if (!string.IsNullOrWhiteSpace(scan.Error)) _db.AppendRecordNote(LastInsertedRecordId, "Residual scan: " + scan.Error);
        if (scan.State == ResidualScanState.CompletedWithFindings)
        {
            ResidualItems = new(scan.Items);
            foreach (var item in ResidualItems) item.PropertyChanged += ResidualItemChanged;
            _cleanupApp = app;
            ResidualStatusMessage = string.Format(UiStrings.Get(LanguageCode, "foundResidualsStatus"), scan.Items.Count, app.Name, ResidualScannedLocations);
            IsResidualOpen = true;
        }
        else SetWebNotice(scan.Message(LanguageCode), scan.State == ResidualScanState.Failed);
        OperationState = UninstallOperationState.Completed;
    }

    public async Task RetryQueueVerificationAsync()
    {
        if (VerificationRetrying || !NeedsQueueReview || IsBatchRunning && ProgressApp is null) return;
        VerificationRetrying = true;
        try
        {
            if (!IsBatchRunning)
            {
                var observations = new List<string>();
                foreach (var item in QueueItems.Where(item => item.State == UninstallOperationState.NeedsUserAttention || item.StartedAt is not null && item.FinishedAt is null))
                {
                    var observation = await QueueReviewVerifier.ObserveAsync(item.App);
                    ApplyRegistrationObservation(item.App, observation);
                    observations.Add(item.OperationId + " | " + item.App.Id + " | " + observation.State + " | " + observation.Evidence);
                }
                ReviewObservation = string.Join("\n", observations);
                _queueSnapshot = _journal.RecordReview(ReviewQueueId, "ReadOnlyVerification", ReviewObservation);
                SetWebNotice(UiStrings.Get(LanguageCode, "queueReviewObservationOnly"));
                return;
            }
            var result = await _uninstaller.RetryVerificationAsync(ProgressApp!);
            AttentionDetails = result.Error; AttentionFailureCode = result.FailureCode; AttentionExitCode = result.ExitObserved ? result.ExitCode : null;
            await ObserveReviewRegistrationAsync(ProgressApp!);
            if (result.Verification == VerificationStatus.Unknown || !_queueControl.TryResolve(result))
                SetWebNotice(ReviewRegistrationState == "Absent"
                    ? LanguageCode == "ar" ? "تسجيل التثبيت غير موجود حاليًا. حُدّثت القائمة؛ تبقى نتيجة المحاولة السابقة غير محسومة." : "The registration is currently absent. The catalog was updated; the historical attempt remains unresolved."
                    : LanguageCode == "ar" ? "لا يزال انتهاء أداة الإزالة غير محسوم. أوقف الطابور للانتقال إلى المراجعة؛ لن يبدأ برنامج آخر." : "Uninstaller completion remains unresolved. Stop the queue to review; no further application will start.", ReviewRegistrationState != "Absent");
        }
        finally { VerificationRetrying = false; }
    }

    private void ApplyRegistrationObservation(InstalledApp app, QueueReviewObservation observation)
    {
        if (ProgressApp?.Id == app.Id) ReviewRegistrationState = observation.State;
        var updated = InventoryIntegrity.ReconcileRegistration(_allApps, app, observation.State);
        if (updated.Count == _allApps.Count) return;
        _allApps = updated; ApplyFilterAndSort(); UpdateCounts();
    }

    private async Task ObserveReviewRegistrationAsync(InstalledApp app)
    {
        var queueId = ReviewQueueId;
        var observation = await QueueReviewVerifier.ObserveAsync(app);
        if (_queueSnapshot?.QueueId != queueId || ProgressApp?.Id != app.Id) return;
        ApplyRegistrationObservation(app, observation);
        ReviewObservation = app.Id + " | " + observation.State + " | " + observation.Evidence;
        // The running queue owns the journal lease. Save its observation without
        // trying to reacquire that lease, and without changing any item outcome.
        _queueSnapshot = _queueSnapshot with { Reviews = (_queueSnapshot.Reviews ?? [])
            .Append(new QueueReviewEvent(DateTimeOffset.UtcNow, "ReadOnlyVerification", ReviewObservation)).ToArray() };
        _journal.Save(_queueSnapshot);
    }

    public void OpenQueueReview()
    {
        if (IsBatchRunning) { IsProgressOpen = true; return; }
        RecoverInterruptedQueue();
    }

    public void AcknowledgeUnresolvedQueue(string expectedQueueId)
    {
        if (!CanAcknowledgeReview || expectedQueueId != ReviewQueueId)
            throw new InvalidOperationException("Acknowledge only the displayed stopped queue.");
        _queueControl.RequestStop();
        _queueSnapshot = _journal.RecordReview(expectedQueueId, "AcknowledgedUnresolved", "User acknowledged unresolved status. This is not a successful uninstall. No pending item was started.");
        if (ProgressApp is not null) _uninstaller.TryReleaseCompletedReview(ProgressApp);
        _recoveringQueue = false; StopBatchAfterCurrent = true;
        IsProgressOpen = false; IsBatchQueue = false;
        SetWebNotice(UiStrings.Get(LanguageCode, "queueReviewAcknowledged"));
        OnPropertyChanged(nameof(NeedsQueueReview)); OnPropertyChanged(nameof(HasPendingQueueReview));
    }

    public void MarkQueueForReview()
    {
        if (!IsBatchRunning && _queueSnapshot is not null && NeedsQueueReview)
            _queueSnapshot = _journal.RecordReview(ReviewQueueId, "KeepPending", "User kept the unresolved operation pending for review.");
        // While running, finish the paused queue and keep the review visible so
        // explicit acknowledgment becomes available. A stopped review may be closed.
        _closeAfterReview = !IsBatchRunning;
        _queueControl.RequestStop();
        StopBatch();
        // Recovery evidence is deliberately retained. A new queue cannot silently clear it.
        if (!IsBatchRunning) IsProgressOpen = false;
    }

    // Explicit command-line QA only; no executable commands or real applications.
    public Task ShowAttentionFixtureAsync()
    {
        var queue = Enumerable.Range(1, 3).Select(index => new InstalledApp
        { Id = "AttentionFixture_" + index, Name = "Isolated queue test " + index, Source = "Fixture", RegistryHive = "Fixture" }).ToList();
        _allApps = queue; ObserveSelection(queue); ApplyFilterAndSort(); UpdateCounts();
        HasConfirmedInventory = true; InventoryState = "Loaded";
        return RunQueueAsync(queue, false, false, _ => Task.FromResult(new UninstallService.UninstallResult(false, -3,
            VerificationStatus.Unknown, "Synthetic verification uncertainty.", "Explicit UI test fixture; no uninstaller was launched.", "FixtureUncertain")));
    }
}
