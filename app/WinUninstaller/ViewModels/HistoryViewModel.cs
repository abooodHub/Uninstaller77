using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    internal const int PageSize = 100;
    private readonly Stack<long> _pageStarts = new();
    private long _pageStart;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _pageNumber = 1;
    [ObservableProperty] private bool _hasOlderPage;
    [ObservableProperty] private bool _hasNewerPage;

    [ObservableProperty]
    private ObservableCollection<HistoryItemViewModel> _records = [];

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private HistoryItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _showTestRecords;

    [ObservableProperty] private string _languageCode = "en";

    public string HistoryTitle => UiStrings.Get(LanguageCode, "history");
    public string HistoryDescription => UiStrings.Get(LanguageCode, "historyDescription");
    public string ShowTestRecordsLabel => UiStrings.Get(LanguageCode, "showTestRecords");
    public string RefreshLabel => UiStrings.Get(LanguageCode, "refresh");
    public string MoreActionsLabel => UiStrings.Get(LanguageCode, "moreActions");
    public string ClearHistoryLabel => UiStrings.Get(LanguageCode, "clearHistory");
    public string ApplicationLabel => UiStrings.Get(LanguageCode, "application");
    public string InstallerTypeLabel => UiStrings.Get(LanguageCode, "installerType");
    public string ResultLabel => UiStrings.Get(LanguageCode, "result");
    public string ResidualStatusLabel => UiStrings.Get(LanguageCode, "residualStatus");
    public string DateTimeLabel => UiStrings.Get(LanguageCode, "dateTime");
    public string ActionsLabel => UiStrings.Get(LanguageCode, "actions");
    public string NoHistoryLabel => UiStrings.Get(LanguageCode, "noHistory");
    public string HistoryHintLabel => UiStrings.Get(LanguageCode, "historyHint");
    public string DetailsActionLabel => UiStrings.Get(LanguageCode, "detailsAction");
    public string UninstallDateLabel => UiStrings.Get(LanguageCode, "uninstallDate");
    public string ExitCodeLabel => UiStrings.Get(LanguageCode, "exitCode");
    public string ResidualCleanupLabel => UiStrings.Get(LanguageCode, "residualCleanup");
    public string RetainedIconLabel => UiStrings.Get(LanguageCode, "retainedIcon");
    public string ExecutionNotesLabel => UiStrings.Get(LanguageCode, "executionNotes");
    public string CloseLabel => UiStrings.Get(LanguageCode, "close");
    public string OlderPageLabel => UiStrings.Get(LanguageCode, "historyOlderPage");
    public string NewerPageLabel => UiStrings.Get(LanguageCode, "historyNewerPage");
    public string PageRange => FormattableString.Invariant(
        $"{(IsEmpty ? 0 : (PageNumber - 1) * PageSize + 1)}–{(PageNumber - 1) * PageSize + Records.Count} / {TotalCount}");

    partial void OnLanguageCodeChanged(string value)
    {
        foreach (var name in new[] { nameof(HistoryTitle), nameof(HistoryDescription), nameof(ShowTestRecordsLabel), nameof(RefreshLabel), nameof(MoreActionsLabel), nameof(ClearHistoryLabel), nameof(ApplicationLabel), nameof(InstallerTypeLabel), nameof(ResultLabel), nameof(ResidualStatusLabel), nameof(DateTimeLabel), nameof(ActionsLabel), nameof(NoHistoryLabel), nameof(HistoryHintLabel), nameof(DetailsActionLabel), nameof(UninstallDateLabel), nameof(ExitCodeLabel), nameof(ResidualCleanupLabel), nameof(RetainedIconLabel), nameof(ExecutionNotesLabel), nameof(CloseLabel) }) OnPropertyChanged(name);
        OnPropertyChanged(nameof(OlderPageLabel));
        OnPropertyChanged(nameof(NewerPageLabel));
        foreach (var item in Records) item.LanguageCode = value;
    }

    partial void OnShowTestRecordsChanged(bool value) => LoadHistory();
    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(PageRange));

    public HistoryViewModel(DatabaseService db) => _db = db;

    [RelayCommand]
    public void LoadHistory()
    {
        var page = _db.GetHistoryPage(ShowTestRecords, beforeId: null, PageSize + 1);
        _pageStart = page.Count == 0 ? 0 : (long)page[0].Id + 1;
        TotalCount = _db.CountHistory(ShowTestRecords, _pageStart);
        _pageStarts.Clear();
        ShowPage(page);
    }

    public void RefreshCount() => TotalCount = _db.CountHistory(ShowTestRecords);

    [RelayCommand]
    public void OpenOlderPage()
    {
        if (!HasOlderPage || Records.Count == 0) return;
        var beforeId = Records[^1].Record.Id;
        var page = _db.GetHistoryPage(ShowTestRecords, beforeId, PageSize + 1);
        if (page.Count == 0) { LoadHistory(); return; }
        _pageStarts.Push(_pageStart);
        _pageStart = beforeId;
        ShowPage(page);
    }

    [RelayCommand]
    public void OpenNewerPage()
    {
        if (!_pageStarts.TryPeek(out var beforeId)) return;
        var page = _db.GetHistoryPage(ShowTestRecords, beforeId, PageSize + 1);
        if (page.Count == 0) { LoadHistory(); return; }
        _pageStart = _pageStarts.Pop();
        ShowPage(page);
    }

    private void ShowPage(IReadOnlyList<UninstallRecord> page)
    {
        CloseDetails();
        Records = new ObservableCollection<HistoryItemViewModel>(page.Take(PageSize)
            .Select(record => new HistoryItemViewModel(record) { LanguageCode = LanguageCode }));
        PageNumber = _pageStarts.Count + 1;
        HasOlderPage = page.Count > PageSize;
        HasNewerPage = _pageStarts.Count > 0;
        IsEmpty = Records.Count == 0;
        OnPropertyChanged(nameof(PageRange));
    }

    [RelayCommand]
    public void OpenDetails(HistoryItemViewModel? item)
    {
        if (item == null) return;
        SelectedItem = item;
        IsDetailsOpen = true;
    }

    [RelayCommand]
    public void CloseDetails()
    {
        IsDetailsOpen = false;
        SelectedItem = null;
    }

    [RelayCommand]
    public void ClearAllHistory()
    {
        if (Records.Count == 0) return;

        if (MessageBox.Show(
                UiStrings.Get(LanguageCode, "clearHistoryConfirm"),
                UiStrings.Get(LanguageCode, "clearHistoryTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _db.ClearHistory();
            LoadHistory();
        }
    }
}

public partial class HistoryItemViewModel : ObservableObject
{
    public UninstallRecord Record { get; }

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty] private string _languageCode = "en";
    partial void OnLanguageCodeChanged(string value)
    {
        OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ResidualStatus)); OnPropertyChanged(nameof(Notes));
    }

    public HistoryItemViewModel(UninstallRecord record)
    {
        Record = record;
        if (record.Outcome == "QueueProtection") Protection = QueueProtectionReport.Read(record.Notes);

        // Load cached icon from disk if available, or generate avatar
        if (!string.IsNullOrEmpty(record.CachedIconPath))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var img = IconCacheService.LoadPng(record.CachedIconPath);
                    img ??= IconExtractionService.GenerateAppAvatar(record.AppName);
                    var dispatcher = Application.Current?.Dispatcher;
                    if (img != null && dispatcher is { HasShutdownStarted: false })
                        _ = dispatcher.BeginInvoke(() => Icon = img);
                }
                catch { /* A missing history icon must not affect the history view. */ }
            });
        }
        else
        {
            Icon = IconExtractionService.GenerateAppAvatar(record.AppName);
        }
    }

    internal QueueProtectionReport? Protection { get; }
    public string Name => Protection is null ? Record.AppName : UiStrings.Get(LanguageCode, "outcomeQueueProtection");
    public string Publisher => string.IsNullOrEmpty(Record.Publisher) ? UiStrings.Get(LanguageCode, "unknownPublisher") : Record.Publisher;
    public string Version => string.IsNullOrEmpty(Record.Version) ? "—" : Record.Version;
    public string Date => Record.UninstallDate;
    public string InstallerType => Record.InstallerType;
    public bool Success => Record.Success;
    public bool IsLegacyTestRecord => Record.AppId.Equals("DiagnosticTestApp", StringComparison.OrdinalIgnoreCase) ||
        Record.AppId.StartsWith("TestApp_", StringComparison.OrdinalIgnoreCase) ||
        Record.AppName.Equals("Automated SQLite Test App", StringComparison.OrdinalIgnoreCase);
    public string ResidualStatus => Protection is not null ? Protection.RegistryText(LanguageCode)
        : Record.ResidualStatus.StartsWith("ResidualPermanent:", StringComparison.Ordinal)
        ? string.Format(UiStrings.Get(LanguageCode, "residualHistoryDeleted"), Record.CleanedItemsCount)
        : Record.ResidualStatus.StartsWith("ResidualScan:", StringComparison.Ordinal)
        ? UiStrings.Get(LanguageCode, Record.ResidualStatus == "ResidualScan:Scanning" ? "residualScanIncomplete" : "residualScan" + Record.ResidualStatus[13..])
        : Record.InstallerType.Contains("MSIX", StringComparison.OrdinalIgnoreCase) && Record.ResidualStatus.StartsWith("Clean", StringComparison.OrdinalIgnoreCase)
            ? UiStrings.Get(LanguageCode, "residualScanUnsupported") : Record.ResidualStatus;
    public string OutcomeState => HistoryOutcome.Normalize(Record.Success, Record.Outcome);
    public string StatusText => Protection is not null ? Protection.RestoreText(LanguageCode)
        : IsLegacyTestRecord ? UiStrings.Get(LanguageCode, "testRecord") : UiStrings.Get(LanguageCode, "outcome" + OutcomeState);
    public string Notes => Protection?.Details(LanguageCode) ?? Record.Notes;
}
