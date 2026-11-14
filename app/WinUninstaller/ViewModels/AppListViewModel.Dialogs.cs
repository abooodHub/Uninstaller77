using System.IO;
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
        var queue = Apps.Where(app => app.IsBatchSelected && app.CanUninstall).ToList();
        if (!UseWebUi && System.Windows.MessageBox.Show(UiStrings.Get(LanguageCode, "queueConfirmDetail"), "Uninstaller 77", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes) return;
        await RunQueueAsync(queue, IsQuietMode, AutoScanResiduals);
    }

    internal Task UninstallConfirmedSelectionAsync(bool protect, bool review) =>
        RunQueueAsync(Apps.Where(app => app.IsBatchSelected && app.CanUninstall).ToList(), false, review, protect: protect, deep: false);
    [RelayCommand]
    public void StopBatch()
    {
        if (OperationState == UninstallOperationState.ScanningResiduals || !IsBatchRunning || StopBatchAfterCurrent) return;
        _queueControl.RequestStop();
        StopBatchAfterCurrent = true;
        ProgressStatus = LanguageCode == "ar" ? "سيتم إيقاف الطابور بعد انتهاء العملية الحالية" : "The queue will stop after the current operation.";
        try
        {
            if (_queueSnapshot is not null)
            {
                _queueSnapshot = _queueSnapshot with { StopRequested = true };
                _journal.Save(_queueSnapshot);
            }
        }
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
        if (!app.CanUninstall)
        {
            SetWebNotice(LanguageCode == "ar" ? "عطّل Windows أو الناشر إزالة هذا التثبيت." : RegisteredUninstallCommand.RemovalBlockedMessage, true);
            return;
        }

        ConfirmApp = app;
        IsQuietMode = false;
        AutoScanResiduals = true;
        DeepResidualScan = false;
        IsConfirmOpen = true;
    }

    public Task PromptUninstallFromShortcutAsync(string shortcutPath) =>
        PromptUninstallForTargetAsync(() => ShellUninstallContextMenuService.ResolveShortcutTarget(shortcutPath), "shellUninstallInvalidShortcut");

    internal Task PromptUninstallFromExecutableAsync(string path) => PromptUninstallForTargetAsync(
        () => Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null,
        "programFileInvalid");

    private async Task PromptUninstallForTargetAsync(Func<string?> resolveTarget, string invalidTargetMessage)
    {
        if (IsInstallationRecording || IsUnregisteredBusy || IsBatchRunning || IsCleaningResiduals || NeedsQueueReview || HasPendingQueueReview || UninstallService.HasPendingOperation)
        { SetWebNotice(UiStrings.Get(LanguageCode, "unregisteredBusy"), true); return; }

        if (!HasConfirmedInventory) await LoadInventoryAsync();
        RequireMaintenanceIdle();
        if (!HasConfirmedInventory)
        {
            SetWebNotice(UiStrings.Get(LanguageCode, "shellUninstallInventoryFailed"), true);
            return;
        }

        var target = resolveTarget();
        if (target is null)
        {
            SetWebNotice(UiStrings.Get(LanguageCode, invalidTargetMessage), true);
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
            SetWebNotice(UiStrings.Get(LanguageCode, "shellUninstallAmbiguous"), true);
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

    internal Task ConfirmAndExecuteUninstallAsync(bool protect, bool review)
    {
        var app = ConfirmApp;
        if (app is null || IsBatchRunning) return Task.CompletedTask;
        CancelConfirm();
        return RunQueueAsync([app], false, review, protect: protect, deep: false);
    }
    [RelayCommand]
    public void CancelProgress()
    {
        if (CanCancelCurrent) { _uninstallCts?.Cancel(); StopBatch(); }
    }
}
