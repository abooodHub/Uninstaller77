using System.Windows;
using CommunityToolkit.Mvvm.Input;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class AppListViewModel
{
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
        if (ResidualReviewId is not null)
        {
            CloseQueueResidualReview();
            return;
        }
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
            if (ResidualReviewId is not null) ResidualSelectionVersion++;
            OnPropertyChanged(nameof(TotalResidualSizeBytes));
            OnPropertyChanged(nameof(TotalResidualSizeDisplay));
        }
    }
}
