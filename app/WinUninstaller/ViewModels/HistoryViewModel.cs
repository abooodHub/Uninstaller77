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
    private List<HistoryItemViewModel> _allRecords = [];

    public bool UseWebUi { get; set; }

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

    partial void OnLanguageCodeChanged(string value)
    {
        foreach (var name in new[] { nameof(HistoryTitle), nameof(HistoryDescription), nameof(ShowTestRecordsLabel), nameof(RefreshLabel), nameof(MoreActionsLabel), nameof(ClearHistoryLabel), nameof(ApplicationLabel), nameof(InstallerTypeLabel), nameof(ResultLabel), nameof(ResidualStatusLabel), nameof(DateTimeLabel), nameof(ActionsLabel), nameof(NoHistoryLabel), nameof(HistoryHintLabel), nameof(DetailsActionLabel), nameof(UninstallDateLabel), nameof(ExitCodeLabel), nameof(ResidualCleanupLabel), nameof(RetainedIconLabel), nameof(ExecutionNotesLabel), nameof(CloseLabel) }) OnPropertyChanged(name);
        foreach (var item in _allRecords) item.LanguageCode = value;
    }

    partial void OnShowTestRecordsChanged(bool value) => ApplyRecordFilter();

    public HistoryViewModel(DatabaseService db) => _db = db;

    [RelayCommand]
    public void LoadHistory()
    {
        _allRecords = _db.GetHistory().Select(r => new HistoryItemViewModel(r) { LanguageCode = LanguageCode }).ToList();
        ApplyRecordFilter();
    }

    private void ApplyRecordFilter()
    {
        Records = new ObservableCollection<HistoryItemViewModel>(_allRecords
            .Where(item => ShowTestRecords || !item.IsLegacyTestRecord));
        IsEmpty = Records.Count == 0;
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

        if (UseWebUi || MessageBox.Show(
                UiStrings.Get(LanguageCode, "clearHistoryConfirm"),
                UiStrings.Get(LanguageCode, "clearHistoryTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _db.ClearHistory();
            _allRecords.Clear();
            Records.Clear();
            IsEmpty = true;
        }
    }
}

public partial class HistoryItemViewModel : ObservableObject
{
    public UninstallRecord Record { get; }

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty] private string _languageCode = "en";
    partial void OnLanguageCodeChanged(string value) => OnPropertyChanged(nameof(StatusText));

    public HistoryItemViewModel(UninstallRecord record)
    {
        Record = record;

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

    public string Name => Record.AppName;
    public string Publisher => string.IsNullOrEmpty(Record.Publisher) ? UiStrings.Get(LanguageCode, "unknownPublisher") : Record.Publisher;
    public string Version => string.IsNullOrEmpty(Record.Version) ? "—" : Record.Version;
    public string Date => Record.UninstallDate;
    public string InstallerType => Record.InstallerType;
    public bool Success => Record.Success;
    public bool IsLegacyTestRecord => Record.AppId == "DiagnosticTestApp" ||
        Record.AppId.StartsWith("TestApp_", StringComparison.OrdinalIgnoreCase) ||
        Record.AppName == "Automated SQLite Test App";
    public string ResidualStatus => Record.ResidualStatus.StartsWith("ResidualScan:", StringComparison.Ordinal)
        ? UiStrings.Get(LanguageCode, Record.ResidualStatus == "ResidualScan:Scanning" ? "residualScanIncomplete" : "residualScan" + Record.ResidualStatus[13..])
        : Record.InstallerType.Contains("MSIX", StringComparison.OrdinalIgnoreCase) && Record.ResidualStatus.StartsWith("Clean", StringComparison.OrdinalIgnoreCase)
            ? UiStrings.Get(LanguageCode, "residualScanUnsupported") : Record.ResidualStatus;
    public string OutcomeState => HistoryOutcome.Normalize(Record.Success, Record.Outcome);
    public string StatusText => IsLegacyTestRecord ? UiStrings.Get(LanguageCode, "testRecord") : UiStrings.Get(LanguageCode, "outcome" + OutcomeState);
    public string Notes => Record.Notes;
}
