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

public record BatchResultDisplay(InstalledApp App, BatchItemState State, string Message, string StateLabel, Brush StateBrush,
    int? ExitCode = null, string FailureCode = "");

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Cancellation sources are transient operation fields and are cancelled/disposed when replaced, completed, or when the owning window closes.")]
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
    private bool _deepResidualScan;

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
    [ObservableProperty] private int _lastResidualDeletedCount;
    [ObservableProperty] private int _lastResidualKeptCount;
    [ObservableProperty] private long _lastResidualDeletedBytes;
    [ObservableProperty] private bool _residualDeletionAttempted;
    [ObservableProperty] private int _residualDeleteProcessedCount;
    [ObservableProperty] private int _residualDeleteTotalCount;
    [ObservableProperty] private int _lastResidualSkippedCount;
    [ObservableProperty] private int _lastResidualFailedCount;
    [ObservableProperty] private string _residualDeleteCurrentApp = "";
    [ObservableProperty] private string _residualDeleteCurrentPath = "";
    [ObservableProperty] private bool _residualDeleteStopRequested;
    [ObservableProperty] private int _lastResidualDeletedRegistryCount;
    [ObservableProperty] private long _lastResidualDeletedFileBytes;

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


    public int SelectedCount => Apps.Count(a => a.IsBatchSelected && a.CanUninstall);
    public bool HasSelectedApps => SelectedCount > 0;
    public string BatchProgressText => $"{BatchCompleted} / {BatchTotal} {UiStrings.Get(LanguageCode, "processedItems")}";
    public double BatchProgressPercent => BatchTotal == 0 ? 0 : Math.Clamp(BatchCompleted * 100.0 / BatchTotal, 0, 100);

    public DatabaseService Database => _db;
    internal InstallationJournal InstallationLogs { get; }
    public InstalledApp[] GetInventorySnapshot() => _allApps.ToArray();
    public int TotalCount => _allApps.Count;
    public int FilteredCount => Apps.Count;
    public int RecentCount => _allApps.Count(a => a.IsRecentApp);
    public int LargeCount => _allApps.Count(a => a.IsLargeApp);
    public int StoreCount => _allApps.Count(a => a.Source == "MSIX");
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "This property is an instance member of the WPF/WebView binding surface.")]
    public int BundlewareCount => 0; // No reliable bundle evidence is available.
    public int LoggedCount => _allApps.Count(a => a.IsLogged);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "This property is an instance member of the WPF/WebView binding surface.")]
    public int InfrequentCount => 0; // No reliable usage evidence is available.
    public int WindowsAppsCount => _allApps.Count(a => a.Source == "MSIX" && a.IsBuiltInWindowsApp);
    public int ThirdPartyAppsCount => _allApps.Count(a => a.Source == "MSIX" && a.IsThirdPartyWindowsApp);
    public int HistoryCount => _db.CountHistory();
    public string KnownSizeDisplay => $"{_allApps.Sum(a => a.EstimatedSizeKB) / 1024.0 / 1024.0:F1} GB";

    public long TotalResidualSizeBytes => ResidualItems.Where(i => i.CanClean && i.IsSelected).Sum(i => i.SizeBytes);
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
        _queueProtection = new QueueProtectionService(System.IO.Path.Combine(db.DataDirectory, "History"));
        _uninstaller = uninstaller;
        _residualScanner = residualScanner;
        InstallationLogs = new InstallationJournal(db.DataDirectory);
        _residualScanner.Installations = InstallationLogs;
        _journal = journal ?? new QueueJournal();
        _unregistered = unregistered ?? new UnregisteredAppService();
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        RecoverInterruptedQueue();
        _queueClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _queueClock.Tick += (_, _) =>
        {
            ElapsedTime = (DateTime.Now - _queueStartedAt).ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
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

#if DEBUG
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

    public void ShowSyntheticQueuePreview(bool running = false, int previewCount = 4)
    {
        IsQueuePreview = true;
        previewCount = Math.Clamp(previewCount, 2, 4);
        var previewApps = new[]
        {
            new InstalledApp { Id = "QueuePreview_1", Name = "Preview App — verified removal", EstimatedSizeKB = 138240, IconImage = IconExtractionService.GenerateAppAvatar("Preview App", 64) },
            new InstalledApp { Id = "QueuePreview_2", Name = "Preview App — removal failed", EstimatedSizeKB = 317440, IconImage = IconExtractionService.GenerateAppAvatar("Preview App", 64) },
            new InstalledApp { Id = "QueuePreview_3", Name = "Preview App — skipped", EstimatedSizeKB = 84480, IconImage = IconExtractionService.GenerateAppAvatar("Preview App", 64) },
            new InstalledApp { Id = "QueuePreview_4", Name = "Preview App — skipped", EstimatedSizeKB = 57344, IconImage = IconExtractionService.GenerateAppAvatar("Preview App", 64) }
        };
        var now = DateTimeOffset.UtcNow;
        QueueJournalItem[] previewItems =
        [
            new("preview-operation-1", previewApps[0], UninstallOperationState.Completed, now.AddSeconds(-12), now.AddSeconds(-7), UiStrings.Get(LanguageCode, "previewVerified"), 0),
            new("preview-operation-2", previewApps[1], running ? UninstallOperationState.Running : UninstallOperationState.Failed, now.AddSeconds(-6), running ? null : now, running ? "" : UiStrings.Get(LanguageCode, "previewStillPresent"), running ? null : 1603),
            new("preview-operation-3", previewApps[2], running ? UninstallOperationState.Idle : UninstallOperationState.Cancelled, null, running ? null : now, UiStrings.Get(LanguageCode, "previewSkipped")),
            new("preview-operation-4", previewApps[3], running ? UninstallOperationState.Idle : UninstallOperationState.Cancelled, null, running ? null : now, UiStrings.Get(LanguageCode, "previewSkipped"))
        ];
        _queueSnapshot = new("synthetic-ui-preview", now, running, false, previewItems[..previewCount]);
        OperationState = running ? UninstallOperationState.Running : UninstallOperationState.Failed;
        IsBatchQueue = true;
        IsBatchRunning = running;
        BatchTotal = previewCount;
        BatchCompleted = running ? 1 : 2;
        BatchState = running ? BatchQueueState.Running : BatchQueueState.PartialFailure;
        ProgressTitle = running ? UiStrings.Get(LanguageCode, "queueRunning") : UiStrings.Get(LanguageCode, "queuePreviewTitle");
        ProgressStatus = UiStrings.Get(LanguageCode, "queuePreviewMessage");
        CurrentStage = running ? UiStrings.Get(LanguageCode, "queueRunning") : UiStrings.Get(LanguageCode, "queueFinished");
        ProgressApp = previewApps[1];
        ElapsedTime = "00:00:12";
        BatchResults = running
            ? new ObservableCollection<BatchResultDisplay> { ToBatchDisplay(new(previewApps[0], BatchItemState.Succeeded, UiStrings.Get(LanguageCode, "previewVerified"), 0)) }
            : new ObservableCollection<BatchResultDisplay>
            {
                ToBatchDisplay(new(previewApps[0], BatchItemState.Succeeded, UiStrings.Get(LanguageCode, "previewVerified"), 0)),
                ToBatchDisplay(new(previewApps[1], BatchItemState.Failed, UiStrings.Get(LanguageCode, "previewStillPresent"), 1603)),
                ToBatchDisplay(new(previewApps[2], BatchItemState.Skipped, UiStrings.Get(LanguageCode, "previewSkipped"))),
                ToBatchDisplay(new(previewApps[3], BatchItemState.Skipped, UiStrings.Get(LanguageCode, "previewSkipped")))
            };
        while (BatchResults.Count > previewCount) BatchResults.RemoveAt(BatchResults.Count - 1);
        IsProgressOpen = true;
        OnPropertyChanged(nameof(QueueItems)); OnPropertyChanged(nameof(BatchProgressText)); OnPropertyChanged(nameof(BatchProgressPercent)); OnPropertyChanged(nameof(BatchStatusBrush));
    }
#endif

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
