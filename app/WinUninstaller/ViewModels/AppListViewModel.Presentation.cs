using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class AppListViewModel
{
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
        OnPropertyChanged(nameof(OpenSystemRestoreLabel)); OnPropertyChanged(nameof(ProtectionWarning));
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
}
