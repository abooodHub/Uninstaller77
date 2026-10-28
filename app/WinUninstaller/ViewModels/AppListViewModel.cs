using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public enum AppCategory
{
    All,
    Bundleware,
    Logged,
    Recent,
    Large,
    Infrequent,
    WindowsApps,
    ThirdPartyApps,
    Store
}

public enum SortMode
{
    NameAsc,
    NameDesc,
    SizeDesc,
    SizeAsc,
    DateDesc,
    PublisherAsc
}

public record BatchResultDisplay(InstalledApp App, BatchItemState State, string Message, string StateLabel, Brush StateBrush);

public partial class AppListViewModel : ObservableObject
{
    private readonly RegistryDiscoveryService _registry;
    private readonly IconCacheService _iconCache;
    private readonly DatabaseService _db;
    private readonly UninstallService _uninstaller;
    private readonly ResidualScanService _residualScanner;
    private readonly Dispatcher _dispatcher;

    // Set by the WebView2 host so review/result surfaces are rendered by the HTML UI.
    public bool UseWebUi { get; set; }
    public string WebNotice { get; private set; } = string.Empty;
    public bool WebNoticeIsError { get; private set; }

    public void SetWebNotice(string message, bool isError = false)
    {
        WebNotice = message;
        WebNoticeIsError = isError;
        OnPropertyChanged(nameof(WebNotice));
        OnPropertyChanged(nameof(WebNoticeIsError));
    }

    private List<InstalledApp> _allApps = [];
    private CancellationTokenSource? _uninstallCts;
    private CancellationTokenSource? _loadCts;
    private InstalledApp? _cleanupApp;
    private readonly DispatcherTimer _queueClock;
    private DateTime _queueStartedAt;

    [ObservableProperty]
    private ObservableCollection<InstalledApp> _apps = [];


    [ObservableProperty]
    private InstalledApp? _selectedApp;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "Ready";


    [ObservableProperty]
    private AppCategory _currentCategory = AppCategory.All;

    [ObservableProperty]
    private SortMode _currentSort = SortMode.NameAsc;

    // --- Modal / Overlay States ---

    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private InstalledApp? _detailsApp;

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private InstalledApp? _confirmApp;

    [ObservableProperty]
    private bool _isQuietMode;

    [ObservableProperty]
    private bool _autoScanResiduals = true;

    [ObservableProperty]
    private bool _isProgressOpen;

    [ObservableProperty]
    private string _progressTitle = "Uninstalling Application";

    [ObservableProperty]
    private InstalledApp? _progressApp;

    [ObservableProperty]
    private string _progressStatus = "Initializing...";

    [ObservableProperty]
    private ObservableCollection<string> _progressLogs = [];

    [ObservableProperty]
    private bool _isResidualOpen;

    [ObservableProperty]
    private ObservableCollection<ResidualItem> _residualItems = [];

    [ObservableProperty]
    private bool _backupRegistry = true;

    [ObservableProperty]
    private string _residualStatusMessage = string.Empty;

    [ObservableProperty] private string _residualScanStage = string.Empty;
    [ObservableProperty] private int _residualScannedLocations;

    [ObservableProperty]
    private bool _isCleaningResiduals;

    [ObservableProperty]
    private int _lastInsertedRecordId;

    [ObservableProperty]
    private bool _isBatchRunning;

    [ObservableProperty]
    private bool _isCurrentOperationIndeterminate;

    [ObservableProperty]
    private bool _reduceMotion;

    [ObservableProperty]
    private string _languageCode = "en";

    public string PageTitle => UiStrings.Get(LanguageCode, CurrentCategory switch
    {
        AppCategory.Recent => "pageRecentTitle",
        AppCategory.Large => "pageLargeTitle",
        AppCategory.Store => "pageStoreTitle",
        _ => "pageTitle"
    });
    public string ShownLabel => UiStrings.Get(LanguageCode, "shown");
    public string RegisteredAppsLabel => UiStrings.Get(LanguageCode, "registeredApps");
    public string RefreshLabel => UiStrings.Get(LanguageCode, "refresh");
    public string SearchPlaceholder => UiStrings.Get(LanguageCode, "search");
    public string SelectVisibleLabel => UiStrings.Get(LanguageCode, "selectVisible");
    public string UninstallSelectedLabel => UiStrings.Get(LanguageCode, "uninstallSelected");
    public string SortLabel => UiStrings.Get(LanguageCode, "sort");
    public string ApplicationColumnLabel => UiStrings.Get(LanguageCode, "application");
    public string VersionColumnLabel => UiStrings.Get(LanguageCode, "version");
    public string SizeColumnLabel => UiStrings.Get(LanguageCode, "size");
    public string InstalledColumnLabel => UiStrings.Get(LanguageCode, "installed");
    public string ActionsColumnLabel => UiStrings.Get(LanguageCode, "actions");
    public string DetailsActionLabel => UiStrings.Get(LanguageCode, "detailsAction");
    public string UninstallActionLabel => UiStrings.Get(LanguageCode, "uninstallAction");
    public string SortNameLabel => UiStrings.Get(LanguageCode, "sortName");
    public string SortSizeLabel => UiStrings.Get(LanguageCode, "sortSize");
    public string SortDateLabel => UiStrings.Get(LanguageCode, "sortDate");
    public string SortPublisherLabel => UiStrings.Get(LanguageCode, "sortPublisher");
    public string NoMatchesLabel => UiStrings.Get(LanguageCode, "noMatchingApps");
    public string SearchHintLabel => UiStrings.Get(LanguageCode, "adjustSearch");
    public string ScanAppsLabel => UiStrings.Get(LanguageCode, "scanningApps");
    public string ScanRegistrationsLabel => UiStrings.Get(LanguageCode, "scanRegistrations");
    public string DetailsVersionLabel => UiStrings.Get(LanguageCode, "detailsVersion");
    public string ArchitectureLabel => UiStrings.Get(LanguageCode, "architecture");
    public string InstallerEngineLabel => UiStrings.Get(LanguageCode, "installerEngine");
    public string EstimatedSizeLabel => UiStrings.Get(LanguageCode, "estimatedSize");
    public string InstallDateLabel => UiStrings.Get(LanguageCode, "installDate");
    public string InstallLocationLabel => UiStrings.Get(LanguageCode, "installLocation");
    public string RegistryKeyLabel => UiStrings.Get(LanguageCode, "registryKey");
    public string UninstallCommandLabel => UiStrings.Get(LanguageCode, "uninstallCommand");
    public string QuietUninstallCommandLabel => UiStrings.Get(LanguageCode, "quietUninstallCommand");
    public string ConfirmUninstallLabel => UiStrings.Get(LanguageCode, "confirmUninstall");
    public string UninstallModeLabel => UiStrings.Get(LanguageCode, "uninstallMode");
    public string StandardModeLabel => UiStrings.Get(LanguageCode, "standardMode");
    public string StandardModeHintLabel => UiStrings.Get(LanguageCode, "standardModeHint");
    public string QuietModeLabel => UiStrings.Get(LanguageCode, "quietMode");
    public string QuietModeHintLabel => UiStrings.Get(LanguageCode, "quietModeHint");
    public string ReviewLeftoversLabel => UiStrings.Get(LanguageCode, "reviewLeftovers");
    public string ReviewLeftoversHintLabel => UiStrings.Get(LanguageCode, "reviewLeftoversHint");
    public string CancelLabel => UiStrings.Get(LanguageCode, "cancel");
    public string UninstallNowLabel => UiStrings.Get(LanguageCode, "uninstallNow");
    public string ResidualTitleLabel => UiStrings.Get(LanguageCode, "residualTitle");
    public string ResidualSizeLabel => UiStrings.Get(LanguageCode, "residualSize");
    public string CheckedLocationsLabel => UiStrings.Get(LanguageCode, "checkedLocations");
    public string SelectAllLabel => UiStrings.Get(LanguageCode, "selectAll");
    public string DeselectAllLabel => UiStrings.Get(LanguageCode, "deselectAll");
    public string QuarantineNoticeLabel => UiStrings.Get(LanguageCode, "quarantineNotice");
    public string SkipResidualLabel => UiStrings.Get(LanguageCode, "skipResidual");
    public string MoveToQuarantineLabel => UiStrings.Get(LanguageCode, "moveToQuarantine");

    partial void OnLanguageCodeChanged(string value)
    {
        NotifyLanguageStrings(); NotifyQueueLabels();
        if (BatchResults.Count > 0)
            BatchResults = new ObservableCollection<BatchResultDisplay>(BatchResults.Select(r => ToBatchDisplay(new BatchItemResult(r.App, r.State, r.Message))));
    }
    partial void OnCurrentCategoryChanged(AppCategory value) { ApplyFilterAndSort(); NotifyLanguageStrings(); }

    private void NotifyLanguageStrings()
    {
        OnPropertyChanged(nameof(PageTitle)); OnPropertyChanged(nameof(ShownLabel));
        OnPropertyChanged(nameof(RegisteredAppsLabel)); OnPropertyChanged(nameof(RefreshLabel));
        OnPropertyChanged(nameof(SearchPlaceholder)); OnPropertyChanged(nameof(SelectVisibleLabel));
        OnPropertyChanged(nameof(UninstallSelectedLabel)); OnPropertyChanged(nameof(SortLabel));
        OnPropertyChanged(nameof(ApplicationColumnLabel)); OnPropertyChanged(nameof(VersionColumnLabel));
        OnPropertyChanged(nameof(SizeColumnLabel)); OnPropertyChanged(nameof(InstalledColumnLabel));
        OnPropertyChanged(nameof(ActionsColumnLabel)); 
        OnPropertyChanged(nameof(DetailsActionLabel)); OnPropertyChanged(nameof(UninstallActionLabel));
        foreach (var name in new[] { nameof(SortNameLabel), nameof(SortSizeLabel), nameof(SortDateLabel), nameof(SortPublisherLabel), nameof(NoMatchesLabel), nameof(SearchHintLabel), nameof(ScanAppsLabel), nameof(ScanRegistrationsLabel), nameof(DetailsVersionLabel), nameof(ArchitectureLabel), nameof(InstallerEngineLabel), nameof(EstimatedSizeLabel), nameof(InstallDateLabel), nameof(InstallLocationLabel), nameof(RegistryKeyLabel), nameof(UninstallCommandLabel), nameof(QuietUninstallCommandLabel), nameof(ConfirmUninstallLabel), nameof(UninstallModeLabel), nameof(StandardModeLabel), nameof(StandardModeHintLabel), nameof(QuietModeLabel), nameof(QuietModeHintLabel), nameof(ReviewLeftoversLabel), nameof(ReviewLeftoversHintLabel), nameof(CancelLabel), nameof(UninstallNowLabel), nameof(ResidualTitleLabel), nameof(ResidualSizeLabel), nameof(CheckedLocationsLabel), nameof(SelectAllLabel), nameof(DeselectAllLabel), nameof(QuarantineNoticeLabel), nameof(SkipResidualLabel), nameof(MoveToQuarantineLabel) }) OnPropertyChanged(name);
    }

    private void NotifyQueueLabels()
    {
        foreach (var name in new[] { nameof(BatchStateLabel), nameof(BatchSuccessfulLabel), nameof(BatchFailedLabel), nameof(BatchSkippedLabel), nameof(BatchProcessedLabel), nameof(CurrentApplicationLabel), nameof(CurrentStageLabel), nameof(ElapsedLabel), nameof(ResultsLabel), nameof(StopAfterCurrentLabel), nameof(CloseLabel), nameof(CancelOperationLabel) }) OnPropertyChanged(name);
    }

    public bool ShowAnimatedProgress => (IsCurrentOperationIndeterminate || IsLoading) && !ReduceMotion;
    partial void OnIsCurrentOperationIndeterminateChanged(bool value) => OnPropertyChanged(nameof(ShowAnimatedProgress));
    partial void OnReduceMotionChanged(bool value) => OnPropertyChanged(nameof(ShowAnimatedProgress));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowAnimatedProgress));
    partial void OnBatchStateChanged(BatchQueueState value) { OnPropertyChanged(nameof(BatchStateLabel)); OnPropertyChanged(nameof(BatchStatusBrush)); }

    [ObservableProperty] private ObservableCollection<BatchResultDisplay> _batchResults = [];
    [ObservableProperty] private BatchQueueState _batchState = BatchQueueState.Running;
    [ObservableProperty] private string _elapsedTime = "00:00";
    [ObservableProperty] private string _currentStage = "Ready";
    [ObservableProperty] private bool _isBatchQueue;

    public int BatchSuccessful => BatchResults.Count(r => r.State == BatchItemState.Succeeded);
    public int BatchFailed => BatchResults.Count(r => r.State == BatchItemState.Failed);
    public int BatchSkipped => BatchResults.Count(item => item.State == BatchItemState.Skipped);
    public string BatchStateLabel => UiStrings.Get(LanguageCode, BatchState switch { BatchQueueState.Succeeded => "queueSucceeded", BatchQueueState.PartialFailure or BatchQueueState.CompletedWithSkips => "queuePartial", BatchQueueState.CriticalFailure => "queueCritical", BatchQueueState.Stopped => "queueStopped", BatchQueueState.NeedsUserAttention => "queueNeedsAttention", _ => "queueRunning" });
    public string BatchSuccessfulLabel => UiStrings.Get(LanguageCode, "successful");
    public string BatchFailedLabel => UiStrings.Get(LanguageCode, "failed");
    public string BatchSkippedLabel => UiStrings.Get(LanguageCode, "skipped");
    public string BatchProcessedLabel => UiStrings.Get(LanguageCode, "processed");
    public string CurrentApplicationLabel => UiStrings.Get(LanguageCode, "currentApplication");
    public string CurrentStageLabel => UiStrings.Get(LanguageCode, "currentStage");
    public string ElapsedLabel => UiStrings.Get(LanguageCode, "elapsed");
    public string ResultsLabel => UiStrings.Get(LanguageCode, "results");
    public string StopAfterCurrentLabel => UiStrings.Get(LanguageCode, "stopAfterCurrent");
    public string CloseLabel => UiStrings.Get(LanguageCode, "close");
    public string CancelOperationLabel => UiStrings.Get(LanguageCode, "cancelOperation");
    public Brush BatchStatusBrush => BatchState switch
    {
        BatchQueueState.Succeeded => (Brush)Application.Current.FindResource("AccentGreen"),
        BatchQueueState.PartialFailure or BatchQueueState.CompletedWithSkips => (Brush)Application.Current.FindResource("AccentPeach"),
        BatchQueueState.CriticalFailure => (Brush)Application.Current.FindResource("AccentRed"),
        _ => (Brush)Application.Current.FindResource("AccentBlue")
    };

    [ObservableProperty]
    private bool _stopBatchAfterCurrent;

    [ObservableProperty]
    private int _batchCompleted;

    [ObservableProperty]
    private int _batchTotal;

    public bool IsAllVisibleSelected
    {
        get => Apps.Count > 0 && Apps.All(app => app.IsBatchSelected);
        set
        {
            foreach (var app in Apps) app.IsBatchSelected = value;
            OnPropertyChanged();
        }
    }

    partial void OnBatchResultsChanged(ObservableCollection<BatchResultDisplay> value)
    {
        value.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(BatchSuccessful)); OnPropertyChanged(nameof(BatchFailed)); OnPropertyChanged(nameof(BatchSkipped));
        };
        OnPropertyChanged(nameof(BatchSuccessful)); OnPropertyChanged(nameof(BatchFailed)); OnPropertyChanged(nameof(BatchSkipped));
    }

    private BatchResultDisplay ToBatchDisplay(BatchItemResult result) => new(result.App, result.State, result.Message,
        UiStrings.Get(LanguageCode, result.State switch { BatchItemState.Succeeded => "successful", BatchItemState.Failed => "failed", BatchItemState.NeedsAttention => "queueNeedsAttention", BatchItemState.Cancelled => "cancelled", _ => "skipped" }),
        (Brush)Application.Current.FindResource(result.State switch { BatchItemState.Succeeded => "AccentGreen", BatchItemState.Failed => "AccentRed", _ => "AccentPeach" }));

    private string LocalizeEngineProgress(string message, InstalledApp app)
    {
        if (message.StartsWith("Removed orphaned desktop shortcuts:", StringComparison.Ordinal)) return message.Replace("Removed orphaned desktop shortcuts:", UiStrings.Get(LanguageCode, "engineShortcutsRemoved"), StringComparison.Ordinal);
        if (message == "Some desktop shortcuts were kept for review.") return UiStrings.Get(LanguageCode, "engineShortcutsKept");
        if (message == "The uninstall launcher exited; waiting for related processes to finish...") return UiStrings.Get(LanguageCode, "engineWaitingDescendants");
        if (message == "External browser handoff was verified; the browser can remain open.") return UiStrings.Get(LanguageCode, "engineExternalBrowser");
        if (message.StartsWith("Preparing uninstallation for", StringComparison.OrdinalIgnoreCase)) return string.Format(UiStrings.Get(LanguageCode, "enginePreparing"), app.Name);
        if (message.StartsWith("Launching uninstaller for", StringComparison.OrdinalIgnoreCase)) return string.Format(UiStrings.Get(LanguageCode, "engineLaunching"), app.Name);
        if (message.StartsWith("Uninstaller running (PID:", StringComparison.OrdinalIgnoreCase)) return message.Replace("Uninstaller running (PID:", UiStrings.Get(LanguageCode, "engineRunningPid"), StringComparison.Ordinal);
        if (message.StartsWith("Uninstaller exited with code", StringComparison.OrdinalIgnoreCase)) return message.Replace("Uninstaller exited with code", UiStrings.Get(LanguageCode, "engineExitedCode"), StringComparison.Ordinal);
        if (message.StartsWith("Verifying uninstallation status", StringComparison.OrdinalIgnoreCase)) return UiStrings.Get(LanguageCode, "engineVerifying");
        if (message.StartsWith("Removing MSIX Package", StringComparison.OrdinalIgnoreCase)) return string.Format(UiStrings.Get(LanguageCode, "engineRemovingMsix"), app.Name);
        return message;
    }

    private string LocalizeResidualStage(string stage)
    {
        if (stage.StartsWith("Checking former install directory", StringComparison.OrdinalIgnoreCase)) return UiStrings.Get(LanguageCode, "scanFormerDirectory");
        if (stage.StartsWith("Checking folder location:", StringComparison.OrdinalIgnoreCase)) return $"{UiStrings.Get(LanguageCode, "scanFolderLocation")}: {stage["Checking folder location:".Length..].Trim()}";
        if (stage.StartsWith("Checking Registry hive:", StringComparison.OrdinalIgnoreCase)) return $"{UiStrings.Get(LanguageCode, "scanRegistryHive")}: {stage["Checking Registry hive:".Length..].Trim()}";
        if (stage.StartsWith("No registry locations", StringComparison.OrdinalIgnoreCase)) return UiStrings.Get(LanguageCode, "noRegistryLocations");
        return stage;
    }

    public int SelectedCount => Apps.Count(a => a.IsBatchSelected);
    public bool HasSelectedApps => SelectedCount > 0;
    public string BatchProgressText => $"{BatchCompleted} / {BatchTotal} {UiStrings.Get(LanguageCode, "processedItems")}";
    public double BatchProgressPercent => BatchTotal == 0 ? 0 : Math.Clamp(BatchCompleted * 100.0 / BatchTotal, 0, 100);

    public DatabaseService Database => _db;
    public InstalledApp[] GetInventorySnapshot() => _allApps.ToArray();
    public int TotalCount => _allApps.Count;
    public int FilteredCount => Apps.Count;
    public int RecentCount => _allApps.Count(a => a.IsRecentApp);
    public int LargeCount => _allApps.Count(a => a.IsLargeApp);
    public int StoreCount => _allApps.Count(a => a.Source == "MSIX");
    public int BundlewareCount => 0; // No reliable bundle evidence is available.
    public int LoggedCount => _allApps.Count(a => a.IsLogged);
    public int InfrequentCount => 0; // No reliable usage evidence is available.
    public int WindowsAppsCount => _allApps.Count(a => a.Source == "MSIX" && a.IsBuiltInWindowsApp);
    public int ThirdPartyAppsCount => _allApps.Count(a => a.Source == "MSIX" && a.IsThirdPartyWindowsApp);
    public int HistoryCount => _db.GetHistory().Count;
    public string KnownSizeDisplay => $"{_allApps.Sum(a => a.EstimatedSizeKB) / 1024.0 / 1024.0:F1} GB";

    public long TotalResidualSizeBytes => ResidualItems.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
    public string TotalResidualSizeDisplay
    {
        get
        {
            var bytes = TotalResidualSizeBytes;
            return bytes switch
            {
                0 => "0 B",
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
                < 1024 * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
                _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB"
            };
        }
    }

    public AppListViewModel(
        RegistryDiscoveryService registry,
        IconCacheService iconCache,
        DatabaseService db,
        UninstallService uninstaller,
        ResidualScanService residualScanner, QueueJournal? journal = null, UnregisteredAppService? unregistered = null)
    {
        _registry = registry;
        _iconCache = iconCache;
        _db = db;
        _uninstaller = uninstaller;
        _residualScanner = residualScanner;
        _journal = journal ?? new QueueJournal();
        _unregistered = unregistered ?? new UnregisteredAppService();
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        RecoverInterruptedQueue();
        _queueClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _queueClock.Tick += (_, _) =>
        {
            ElapsedTime = (DateTime.Now - _queueStartedAt).ToString(@"hh\:mm\:ss");
        };
    }

    partial void OnSearchTextChanged(string value) => ApplyFilterAndSort();
    partial void OnCurrentSortChanged(SortMode value) => ApplyFilterAndSort();

    [RelayCommand]
    public void SetCategory(string category)
    {
        CurrentCategory = category switch
        {
            "Bundleware" => AppCategory.Bundleware,
            "Logged" => AppCategory.Logged,
            "Recent" => AppCategory.Recent,
            "Large" => AppCategory.Large,
            "Infrequent" => AppCategory.Infrequent,
            "WindowsApps" => AppCategory.WindowsApps,
            "ThirdPartyApps" => AppCategory.ThirdPartyApps,
            "Store" => AppCategory.Store,
            _ => AppCategory.All
        };
    }

    [RelayCommand]
    public void SetSort(string sort)
    {
        CurrentSort = sort switch
        {
            "NameDesc" => SortMode.NameDesc,
            "SizeDesc" => SortMode.SizeDesc,
            "SizeAsc" => SortMode.SizeAsc,
            "DateDesc" => SortMode.DateDesc,
            "PublisherAsc" => SortMode.PublisherAsc,
            _ => SortMode.NameAsc
        };
    }

    [RelayCommand]
    public Task LoadAppsAsync() => LoadInventoryAsync();

    public void LoadSyntheticApps(int count)
    {
        _allApps = Enumerable.Range(1, count).Select(i => new InstalledApp
        {
            Id = "TestApp_Synthetic_" + i,
            Name = $"Fixture Application {i:000}",
            Publisher = "WinUninstaller Test Data",
            Version = $"1.{i % 20}",
            EstimatedSizeKB = i * 1024L,
            InstallDate = DateTime.Today.AddDays(-(i % 180)).ToString("yyyyMMdd"),
            RegistryHive = "Fixture",
            RegistryKeyPath = "",
            UninstallString = ""
        }).ToList();
        ObserveSelection(_allApps);
        ApplyFilterAndSort();
        UpdateCounts();
        StatusText = $"Synthetic performance fixture: {count} applications (no uninstall commands).";
    }

    public void ShowSyntheticQueuePreview()
    {
        IsQueuePreview = true;
        OperationState = UninstallOperationState.Failed;
        var previewApps = new[]
        {
            new InstalledApp { Id = "QueuePreview_1", Name = "Preview App — verified removal", IconImage = IconExtractionService.GenerateAppAvatar("Preview App", 64) },
            new InstalledApp { Id = "QueuePreview_2", Name = "Preview App — removal failed", IconImage = IconExtractionService.GenerateAppAvatar("Preview App", 64) },
            new InstalledApp { Id = "QueuePreview_3", Name = "Preview App — skipped", IconImage = IconExtractionService.GenerateAppAvatar("Preview App", 64) },
            new InstalledApp { Id = "QueuePreview_4", Name = "Preview App — skipped", IconImage = IconExtractionService.GenerateAppAvatar("Preview App", 64) }
        };
        IsBatchQueue = true;
        IsBatchRunning = false;
        BatchTotal = previewApps.Length;
        BatchCompleted = 2;
        BatchState = BatchQueueState.PartialFailure;
        ProgressTitle = UiStrings.Get(LanguageCode, "queuePreviewTitle");
        ProgressStatus = UiStrings.Get(LanguageCode, "queuePreviewMessage");
        CurrentStage = UiStrings.Get(LanguageCode, "queueFinished");
        ProgressApp = previewApps[1];
        ElapsedTime = "00:00:12";
        BatchResults = new ObservableCollection<BatchResultDisplay>
        {
            ToBatchDisplay(new(previewApps[0], BatchItemState.Succeeded, UiStrings.Get(LanguageCode, "previewVerified"), 0)),
            ToBatchDisplay(new(previewApps[1], BatchItemState.Failed, UiStrings.Get(LanguageCode, "previewStillPresent"), 1603)),
            ToBatchDisplay(new(previewApps[2], BatchItemState.Skipped, UiStrings.Get(LanguageCode, "previewSkipped"))),
            ToBatchDisplay(new(previewApps[3], BatchItemState.Skipped, UiStrings.Get(LanguageCode, "previewSkipped")))
        };
        IsProgressOpen = true;
        OnPropertyChanged(nameof(BatchProgressText)); OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchStatusBrush));
    }

    private void ObserveSelection(IEnumerable<InstalledApp> apps)
    {
        foreach (var app in apps)
        {
            app.PropertyChanged -= AppSelectionChanged;
            app.PropertyChanged += AppSelectionChanged;
        }
    }

    private void AppSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledApp.IsBatchSelected))
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelectedApps));
            OnPropertyChanged(nameof(IsAllVisibleSelected));
        }
    }

    public void CancelBackgroundWork() => _loadCts?.Cancel();

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(RecentCount));
        OnPropertyChanged(nameof(LargeCount));
        OnPropertyChanged(nameof(StoreCount));
        OnPropertyChanged(nameof(KnownSizeDisplay));
        OnPropertyChanged(nameof(HistoryCount));
        OnPropertyChanged(nameof(SelectedCount));
    }

    private async Task LoadIconsInBackgroundAsync(CancellationToken token)
    {
        var snapshot = _allApps.ToArray();
        try
        {
            await Parallel.ForEachAsync(snapshot, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (app, cancellation) =>
                {
                    try
                    {
                        var icon = _iconCache.GetIcon(app);
                        if (icon is null || cancellation.IsCancellationRequested) return;
                        if (!icon.IsFrozen && icon.CanFreeze) icon.Freeze();
                        await _dispatcher.InvokeAsync(() => { if (!cancellation.IsCancellationRequested) app.IconImage = icon; },
                            DispatcherPriority.DataBind, cancellation);
                    }
                    catch (OperationCanceledException) { }
                    catch { /* A missing or inaccessible icon must not stop inventory. */ }
                });
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyFilterAndSort()
    {
        var filtered = QueryApps(_allApps, CurrentCategory, CurrentSort, SearchText);

        if (Application.Current != null)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Apps = new ObservableCollection<InstalledApp>(filtered);

                OnPropertyChanged(nameof(FilteredCount));
                NotifyVisibleSelection();
            });
        }
        else
        {
            Apps = new ObservableCollection<InstalledApp>(filtered);

            OnPropertyChanged(nameof(FilteredCount));
            NotifyVisibleSelection();
        }
    }

    private void NotifyVisibleSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelectedApps));
        OnPropertyChanged(nameof(IsAllVisibleSelected));
    }

    public static List<InstalledApp> QueryApps(IEnumerable<InstalledApp> apps,
        AppCategory category, SortMode sort, string? search)
    {
        IEnumerable<InstalledApp> query = apps;

        // 1. Category Filter
        query = category switch
        {
            AppCategory.Bundleware => Enumerable.Empty<InstalledApp>(),
            AppCategory.Logged => Enumerable.Empty<InstalledApp>(),
            AppCategory.Recent => query.Where(a => a.IsRecentApp),
            AppCategory.Large => query.Where(a => a.IsLargeApp),
            AppCategory.Infrequent => Enumerable.Empty<InstalledApp>(),
            AppCategory.WindowsApps => query.Where(a => a.Source == "MSIX" && a.IsBuiltInWindowsApp),
            AppCategory.ThirdPartyApps => query.Where(a => a.Source == "MSIX" && a.IsThirdPartyWindowsApp),
            AppCategory.Store => query.Where(a => a.Source == "MSIX"),
            _ => query
        };

        // 2. Search Filter
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(a =>
                a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                a.Version.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        // 3. Sorting
        query = sort switch
        {
            SortMode.NameDesc => query.OrderByDescending(a => a.Name, StringComparer.OrdinalIgnoreCase),
            SortMode.SizeDesc => query.OrderByDescending(a => a.EstimatedSizeKB),
            SortMode.SizeAsc => query.OrderBy(a => a.EstimatedSizeKB),
            SortMode.DateDesc => query.OrderByDescending(a => a.ParsedInstallDate ?? DateTime.MinValue),
            SortMode.PublisherAsc => query.OrderBy(a => a.Publisher, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        };

        return query.ToList();
    }

    [RelayCommand]
    public void SelectAllVisible(bool selected)
    {
        IsAllVisibleSelected = selected;
        OnPropertyChanged(nameof(SelectedCount));
    }

    [RelayCommand]
    public async Task UninstallSelectedAsync()
    {
        var queue = Apps.Where(app => app.IsBatchSelected).ToList();
        if (!UseWebUi && System.Windows.MessageBox.Show(UiStrings.Get(LanguageCode, "queueConfirmDetail"), "Uninstaller 77", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes) return;
        await RunQueueAsync(queue, false, false);
    }
    [RelayCommand]
    public void StopBatch()
    {
        _queueControl.RequestStop();
        if (!IsBatchRunning || StopBatchAfterCurrent) return;
        StopBatchAfterCurrent = true;
        ProgressStatus = LanguageCode == "ar" ? "سيتم إيقاف الطابور بعد انتهاء العملية الحالية" : "The queue will stop after the current operation.";
        try { if (ProgressApp is not null) UpdateQueueItem(ProgressApp, UninstallOperationState.StopRequested, ProgressStatus); }
        catch (Exception ex) { AttentionDetails = ex.Message; SetWebNotice(ex.Message, true); }
        OnPropertyChanged(nameof(QueueItems));
    }

    [RelayCommand]
    public void CloseProgress()
    {
        if (!IsBatchRunning && _uninstallCts == null) { IsProgressOpen = false; IsBatchQueue = false; }
    }

    // --- Details Modal ---

    [RelayCommand]
    public void OpenDetails(InstalledApp? app)
    {
        app ??= SelectedApp;
        if (app == null) return;
        DetailsApp = app;
        IsDetailsOpen = true;
    }

    [RelayCommand]
    public void CloseDetails()
    {
        IsDetailsOpen = false;
        DetailsApp = null;
    }

    // --- Uninstall Flow ---

    [RelayCommand]
    public void PromptUninstall(InstalledApp? app)
    {
        app ??= SelectedApp;
        if (app == null) return;

        ConfirmApp = app;
        IsQuietMode = false;
        AutoScanResiduals = true;
        IsConfirmOpen = true;
    }

    public async Task PromptUninstallFromShortcutAsync(string shortcutPath)
    {
        if (IsUnregisteredBusy || IsBatchRunning || IsCleaningResiduals || NeedsQueueReview || HasPendingQueueReview || UninstallService.HasPendingOperation)
        { SetWebNotice(UiStrings.Get(LanguageCode, "unregisteredBusy"), true); return; }
        if (!HasConfirmedInventory) await LoadInventoryAsync();
        if (!HasConfirmedInventory)
        {
            SetWebNotice(UiStrings.Get(LanguageCode, "shellUninstallInventoryFailed"), true);
            return;
        }

        var target = ShellUninstallContextMenuService.ResolveShortcutTarget(shortcutPath);
        if (target is null)
        {
            SetWebNotice(UiStrings.Get(LanguageCode, "shellUninstallInvalidShortcut"), true);
            return;
        }

        var matches = ShellUninstallContextMenuService.FindMatchingApps(target, _allApps);
        if (matches.Count == 0)
        {
            await ReviewUnregisteredAsync(target);
            return;
        }
        if (matches.Count != 1)
        {
            SetWebNotice(UiStrings.Get(LanguageCode, matches.Count == 0 ? "shellUninstallNoMatch" : "shellUninstallAmbiguous"), true);
            return;
        }

        PromptUninstall(matches[0]);
    }

    [RelayCommand]
    public void CancelConfirm()
    {
        IsConfirmOpen = false;
        ConfirmApp = null;
    }

    [RelayCommand]
    public async Task ConfirmAndExecuteUninstallAsync()
    {
        var app = ConfirmApp;
        if (app == null || IsBatchRunning) return;
        IsConfirmOpen = false;
        ConfirmApp = null;
        await RunQueueAsync([app], IsQuietMode, AutoScanResiduals);
    }
    [RelayCommand]
    public void CancelProgress()
    {
        if (CanCancelCurrent) { _uninstallCts?.Cancel(); StopBatch(); }
    }

    // --- Residual Cleanup Flow ---

    [RelayCommand]
    public async Task ExecuteResidualCleanupAsync()
    {
        RequireUnregisteredIdle();
        if (ResidualItems.Count == 0)
        {
            IsResidualOpen = false;
            return;
        }

        var app = _cleanupApp;
        if (app == null) return;
        IsCleaningResiduals = true;
        IsCurrentOperationIndeterminate = true;

        var progress = new DispatcherProgress<string>(_dispatcher, msg =>
        {
            ResidualStatusMessage = msg;
        });

        try
        {
            using var lease = _journal.AcquireExecutionLease();
            if (QueueJournal.NeedsReview(_journal.Read())) throw new InvalidOperationException("UnregisteredApp:OperationBusy");
            var cleanResult = await _residualScanner.CleanResidualsAsync(
                ResidualItems.ToList(),
                app,
                BackupRegistry,
                progress);

            IsCleaningResiduals = false;
            IsCurrentOperationIndeterminate = false;
            IsResidualOpen = false;

            // Persist a language-neutral status; the UI renders the stored counts in its current language.
            string status = FormattableString.Invariant($"Quarantined {cleanResult.CleanedCount} items ({cleanResult.CleanedBytes / 1024.0 / 1024.0:F1} MB)");
            if (LastInsertedRecordId > 0)
            {
                _db.UpdateResidualInfo(LastInsertedRecordId, "ResidualScan:" + ResidualState, cleanResult.CleanedCount, cleanResult.CleanedBytes);
                _db.AppendRecordNote(LastInsertedRecordId, status + "\n" + string.Join("\n", cleanResult.Errors));
            }

            string msg = string.Format(UiStrings.Get(LanguageCode, "residualReviewComplete"), cleanResult.CleanedCount, cleanResult.CleanedBytes / 1024.0 / 1024.0);
            if (!string.IsNullOrEmpty(cleanResult.BackupPath))
            {
                msg += $"\n• {UiStrings.Get(LanguageCode, "quarantineManifest")}: {cleanResult.BackupPath}";
            }

            if (cleanResult.Errors.Count > 0)
            {
                msg += $"\n\n{UiStrings.Get(LanguageCode, "warnings")}:\n" + string.Join("\n", cleanResult.Errors.Take(3));
            }
            SetWebNotice(msg, !cleanResult.Success);

            if (!UseWebUi) MessageBox.Show(msg, UiStrings.Get(LanguageCode, "cleanupCompleted"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            IsCleaningResiduals = false;
            IsCurrentOperationIndeterminate = false;
            SetWebNotice($"{UiStrings.Get(LanguageCode, "residualCleanupError")}: {ex.Message}", true);
            if (!UseWebUi) MessageBox.Show($"{UiStrings.Get(LanguageCode, "residualCleanupError")}:\n{ex.Message}", UiStrings.Get(LanguageCode, "error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public void SkipResidualCleanup()
    {
        if (IsCleaningResiduals) return;
        IsResidualOpen = false;
        if (LastInsertedRecordId > 0)
        {
            _db.AppendRecordNote(LastInsertedRecordId, "Residual cleanup declined; scan result retained.");
        }
    }

    [RelayCommand]
    public void SelectAllResiduals()
    {
        if (IsCleaningResiduals) return;
        foreach (var item in ResidualItems)
        {
            item.IsSelected = item.CanClean;
        }
        OnPropertyChanged(nameof(TotalResidualSizeBytes));
        OnPropertyChanged(nameof(TotalResidualSizeDisplay));
    }

    [RelayCommand]
    public void DeselectAllResiduals()
    {
        if (IsCleaningResiduals) return;
        foreach (var item in ResidualItems)
        {
            item.IsSelected = false;
        }
        OnPropertyChanged(nameof(TotalResidualSizeBytes));
        OnPropertyChanged(nameof(TotalResidualSizeDisplay));
    }

    private void ResidualItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResidualItem.IsSelected))
        {
            OnPropertyChanged(nameof(TotalResidualSizeBytes));
            OnPropertyChanged(nameof(TotalResidualSizeDisplay));
        }
    }

    private sealed class DispatcherProgress<T>(Dispatcher dispatcher, Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            if (dispatcher.CheckAccess())
            {
                report(value);
                return;
            }

            _ = dispatcher.InvokeAsync(() => report(value), DispatcherPriority.DataBind);
        }
    }
}
