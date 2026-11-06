using System.IO;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class AppListViewModel
{
    internal sealed record ResidualReviewGroup(InstalledApp App, int RecordId, ResidualScanResult Scan,
        int Offset, IReadOnlyList<ResidualItem> Items);

    internal IReadOnlyList<ResidualReviewGroup> ResidualReviewGroups { get; private set; } = [];
    internal string? ResidualReviewId { get; private set; }
    internal int ResidualSelectionVersion { get; private set; }
    private IReadOnlyList<InstalledApp> _residualReviewQueue = [];

    private async Task ReviewQueueResidualsAsync(IReadOnlyList<InstalledApp> queue, List<(InstalledApp App, int RecordId)> verified)
    {
        var terminalState = OperationState;
        var terminalApp = ProgressApp;
        OperationState = UninstallOperationState.ScanningResiduals;
        ResidualState = ResidualScanState.Scanning;
        ResidualScannedLocations = 0;
        ResidualScanStage = "";
        try
        {
            var groups = new List<ResidualReviewGroup>();
            var offset = 0;
            var locations = 0;
            foreach (var (app, recordId) in verified)
            {
                ProgressApp = app;
                SaveResidualHistory(recordId, "ResidualScan:Scanning", 0, 0, "");
                var previousLocations = locations;
                var scan = await _residualScanner.ScanResidualsAsync(app, CancellationToken.None,
                    new DispatcherProgress<ResidualScanService.ScanProgress>(_dispatcher, value =>
                    {
                        if (ResidualState != ResidualScanState.Scanning || !ReferenceEquals(ProgressApp, app)) return;
                        ResidualScanStage = LocalizeResidualStage(value.Stage);
                        ResidualScannedLocations = previousLocations + value.ScannedLocations;
                    }));
                locations += scan.ScannedLocations;
                SaveResidualHistory(recordId, scan.StorageStatus, 0, 0, scan.Error);
                var items = ResidualScanService.ToPermanentReview(app, scan, queue);
                groups.Add(new(app, recordId, scan, offset, items));
                offset += items.Count;
            }

            ResidualReviewGroups = groups;
            ResidualScannedLocations = locations;
            ResidualState = offset > 0 ? ResidualScanState.CompletedWithFindings
                : groups.Any(group => group.Scan.State == ResidualScanState.Failed) ? ResidualScanState.Failed
                : groups.Any(group => group.Scan.Completed) ? ResidualScanState.CompletedNoFindings : ResidualScanState.NotScanned;
            if (groups.Any(group => group.Items.Any(item => item.CanClean)))
            {
                ResidualReviewId = Guid.NewGuid().ToString("N");
                ResidualSelectionVersion = 0;
                _residualReviewQueue = queue;
                ResidualItems = new(groups.SelectMany(group => group.Items));
                foreach (var item in ResidualItems) item.PropertyChanged += ResidualItemChanged;
                IsResidualOpen = true;
            }
        }
        finally { ProgressApp = terminalApp; OperationState = terminalState; }
    }

    internal void RequireOpenResidualReview(string reviewId)
    {
        if (!IsResidualOpen || IsBatchRunning || IsCleaningResiduals || ResidualReviewId != reviewId)
            throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualSelectionChanged"));
    }

    internal void RequireCurrentResidualReview(string reviewId, int selectionVersion, bool confirmed)
    {
        RequireOpenResidualReview(reviewId);
        if (!confirmed || ResidualSelectionVersion != selectionVersion || !ResidualItems.Any(item => item.CanClean && item.IsSelected))
            throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualSelectionChanged"));
    }

    internal async Task DeleteQueueResidualsAsync(string reviewId, int selectionVersion)
    {
        RequireCurrentResidualReview(reviewId, selectionVersion, true);
        RequireUnregisteredIdle();
        using var lease = _journal.AcquireExecutionLease();
        if (QueueJournal.NeedsReview(_journal.Read())) throw new InvalidOperationException("UnregisteredApp:OperationBusy");
        IsCleaningResiduals = true;
        try
        {
            var deleted = 0;
            var kept = 0;
            foreach (var group in ResidualReviewGroups)
            {
                var selected = group.Items.Where(item => item.CanClean && item.IsSelected).ToArray();
                if (selected.Length == 0) continue;
                var result = await ResidualScanService.DeleteReviewedFilesAsync(group.App, _residualReviewQueue, selected);
                deleted += result.Deleted;
                kept += result.Kept.Count;
                SaveResidualHistory(group.RecordId, "ResidualPermanent:" + group.Scan.State, result.Deleted, result.Bytes,
                    $"Permanent residual deletion: {result.Deleted} deleted, {result.Kept.Count} kept.\n" +
                    string.Join("\n", result.Attempts.Where(attempt => attempt.Disposition != ResidualScanService.PermanentDisposition.Deleted)
                        .Select(attempt => $"{attempt.Disposition}: {attempt.Item.Path}")));
            }
            CloseQueueResidualReview();
            SetWebNotice(string.Format(UiStrings.Get(LanguageCode, "residualPermanentResult"), deleted, kept), kept > 0);
        }
        finally { IsCleaningResiduals = false; }
    }

    private void SaveResidualHistory(int recordId, string status, int count, long bytes, string detail)
    {
        try
        {
            _db.UpdateResidualInfo(recordId, status, count, bytes);
            if (detail.Length > 0) _db.AppendRecordNote(recordId, detail);
        }
        catch (Exception ex)
        {
            ProgressLogs.Add(ex.Message);
            SetWebNotice(UiStrings.Get(LanguageCode, "residualHistoryFailed"), true);
        }
    }

    private void CloseQueueResidualReview()
    {
        IsResidualOpen = false;
        foreach (var item in ResidualItems) item.PropertyChanged -= ResidualItemChanged;
        ResidualItems.Clear();
        ResidualReviewId = null;
        _residualReviewQueue = [];
    }
}
