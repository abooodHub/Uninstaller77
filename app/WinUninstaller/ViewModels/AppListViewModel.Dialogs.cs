using CommunityToolkit.Mvvm.Input;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class AppListViewModel
{
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
        await RunQueueAsync(queue, false, UseWebUi);
    }
    [RelayCommand]
    public void StopBatch()
    {
        if (OperationState == UninstallOperationState.ScanningResiduals) return;
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
        if (!IsBatchRunning && !IsResidualOpen && _uninstallCts == null)
        {
            IsProgressOpen = false;
            IsBatchQueue = false;
            ResidualReviewGroups = [];
        }
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
}
