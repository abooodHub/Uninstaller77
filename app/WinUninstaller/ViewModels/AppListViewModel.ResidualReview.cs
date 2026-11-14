using System.IO;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class AppListViewModel
{
    internal sealed record ResidualReviewGroup(InstalledApp App, int RecordId, ResidualScanResult Scan,
        int Offset, IReadOnlyList<ResidualItem> Items);

    internal IReadOnlyList<ResidualReviewGroup> ResidualReviewGroups { get; private set; } = [];
    internal Dictionary<string, int> ResidualDeletedByApp { get; } = new(StringComparer.Ordinal);
    private CancellationTokenSource? _residualDeleteStop;
    internal string? ResidualReviewId { get; private set; }
    internal int ResidualSelectionVersion { get; private set; }
    private IReadOnlyList<InstalledApp> _residualReviewQueue = [];

    internal async Task RescanHistoryResidualsAsync(int recordId, bool deep)
    {
        RequireMaintenanceIdle();
        if (IsResidualOpen || recordId <= 0) throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualSelectionChanged"));
        using var lease = _journal.AcquireExecutionLease();
        if (QueueJournal.NeedsReview(_journal.Read())) throw new InvalidOperationException("UnregisteredApp:OperationBusy");
        var record = _db.GetHistory().FirstOrDefault(entry => entry.Id == recordId);
        if (record is null || !ResidualHistoryIdentity.TryRestore(record, out var app) || app is null)
            throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualHistoryRescanUnavailable"));
        if (!await IsHistoryAppStillRemovedAsync(app))
            throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualHistoryRescanChanged"));
        IsCleaningResiduals = true;
        try
        {
            ResidualState = ResidualScanState.Scanning;
            ResidualScannedLocations = 0;
            SaveResidualHistory(recordId, "ResidualScan:Scanning", 0, 0, "");
            var scan = await _residualScanner.ScanResidualsAsync(app,
                new DispatcherProgress<ResidualScanService.ScanProgress>(_dispatcher, value =>
                { ResidualScanStage = LocalizeResidualStage(value.Stage); ResidualScannedLocations = value.ScannedLocations; }),
                deep, CancellationToken.None);
            // The identity can change while the scan is running. A reinstalled app is never a cleanup target.
            if (!await IsHistoryAppStillRemovedAsync(app))
                throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualHistoryRescanChanged"));
            SaveResidualHistory(recordId, scan.StorageStatus, 0, 0, scan.Error);
            var items = ResidualScanService.ToPermanentReview(app, scan, [app]);
            ResidualReviewGroups = [new ResidualReviewGroup(app, recordId, scan, 0, items)];
            ResidualDeletedByApp.Clear();
            _residualReviewQueue = [app];
            ResidualDeletionAttempted = false;
            LastResidualDeletedCount = 0;
            LastResidualKeptCount = 0;
            LastResidualSkippedCount = 0;
            LastResidualFailedCount = 0;
            LastResidualDeletedBytes = 0;
            LastResidualDeletedRegistryCount = 0;
            LastResidualDeletedFileBytes = 0;
            ResidualItems = new(items);
            foreach (var item in ResidualItems) { item.IsSelected = false; item.PropertyChanged += ResidualItemChanged; }
            ResidualState = scan.State;
            ResidualScannedLocations = scan.ScannedLocations;
            ResidualReviewId = Guid.NewGuid().ToString("N");
            ResidualSelectionVersion = 0;
            IsResidualOpen = true;
            OnPropertyChanged(nameof(TotalResidualSizeBytes));
            OnPropertyChanged(nameof(TotalResidualSizeDisplay));
        }
        finally { IsCleaningResiduals = false; }
    }

    private static async Task<bool> IsHistoryAppStillRemovedAsync(InstalledApp app) => app.Source == "MSIX"
        ? (await MsixDeploymentService.QueryAsync(app.Id)).Verification == VerificationStatus.VerifiedRemoved
        : UninstallRegistration.Read(app.RegistryKeyPath).State == UninstallRegistration.Presence.Absent;

    internal void SelectResidualGroup(string reviewId, string appId, bool selected, string? kind = null)
    {
        RequireOpenResidualReview(reviewId);
        var group = ResidualReviewGroups.SingleOrDefault(group => group.App.Id == appId)
            ?? throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualSelectionChanged"));
        foreach (var item in group.Items.Where(item => item.CanClean && (kind switch
        {
            "files" => item.Type is ResidualType.File or ResidualType.Directory,
            "registry" => item.Type is ResidualType.RegistryKey or ResidualType.RegistryValue,
            "system" => item.Type is not (ResidualType.File or ResidualType.Directory or ResidualType.RegistryKey or ResidualType.RegistryValue),
            null => true,
            _ => false
        }))) item.IsSelected = selected;
    }

#if DEBUG
    public void ShowSyntheticResidualReviewPreview()
    {
        var apps = new[]
        {
            new InstalledApp { Id = "ResidualPreview_Dropbox", Name = "Dropbox", IconImage = IconExtractionService.GenerateAppAvatar("Dropbox", 64) },
            new InstalledApp { Id = "ResidualPreview_Editor", Name = "Preview Editor", IconImage = IconExtractionService.GenerateAppAvatar("Preview Editor", 64) },
            new InstalledApp { Id = "ResidualPreview_Notes", Name = "Preview Notes", IconImage = IconExtractionService.GenerateAppAvatar("Preview Notes", 64) }
        };
        var counts = new[] { 64, 7, 5 };
        var groups = new List<ResidualReviewGroup>();
        var offset = 0;
        for (var groupIndex = 0; groupIndex < apps.Length; groupIndex++)
        {
            var app = apps[groupIndex];
            var groupItems = Enumerable.Range(0, counts[groupIndex]).Select(i => new ResidualItem
            {
                Path = $@"C:\Users\Preview\AppData\Local\{app.Name}\Data\cache\very-long-folder-name\residual-file-{i + 1:000}.bin",
                SizeBytes = (i + 1) * 4096L,
                Type = ResidualType.File,
                CanClean = i % 9 != 0,
                IsSelected = false
            }).ToArray();
            var scan = new ResidualScanResult(ResidualScanState.CompletedWithFindings, groupItems);
            groups.Add(new ResidualReviewGroup(app, -1, scan, offset, groupItems));
            offset += groupItems.Length;
        }
        ResidualReviewGroups = groups;
        ResidualItems = new(groups.SelectMany(group => group.Items));
        ResidualReviewId = Guid.NewGuid().ToString("N");
        ResidualSelectionVersion = 0;
        IsResidualOpen = true;
        IsBatchRunning = false;
        OnPropertyChanged(nameof(TotalResidualSizeBytes));
        OnPropertyChanged(nameof(TotalResidualSizeDisplay));
    }
#endif

    private async Task ReviewQueueResidualsAsync(IReadOnlyList<InstalledApp> queue, List<(InstalledApp App, int RecordId)> verified,
        Dictionary<string, DesktopShortcutCleanupService.Snapshot> shortcutSnapshots,
        Dictionary<string, RegistryResidualEvidence> registryEvidence, bool deep)
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
            foreach (var app in queue)
            {
                var verifiedEntry = verified.FirstOrDefault(entry => entry.App.Id == app.Id);
                if (verifiedEntry.App is null)
                    continue;
                var recordId = verifiedEntry.RecordId;
                ProgressApp = app;
                SaveResidualHistory(recordId, "ResidualScan:Scanning", 0, 0, "");
                var previousLocations = locations;
                var scan = await _residualScanner.ScanResidualsAsync(app,
                    new DispatcherProgress<ResidualScanService.ScanProgress>(_dispatcher, value =>
                    {
                        if (ResidualState != ResidualScanState.Scanning || !ReferenceEquals(ProgressApp, app)) return;
                        ResidualScanStage = LocalizeResidualStage(value.Stage);
                        ResidualScannedLocations = previousLocations + value.ScannedLocations;
                    }), deep, CancellationToken.None);
                locations += scan.ScannedLocations;
                SaveResidualHistory(recordId, scan.StorageStatus, 0, 0, scan.Error);
                shortcutSnapshots.TryGetValue(app.Id, out var shortcutSnapshot);
                registryEvidence.TryGetValue(app.Id, out var registrySnapshot);
                var items = ResidualScanService.ToPermanentReview(app, scan, queue, shortcutSnapshot, registrySnapshot);
                groups.Add(new(app, recordId, scan, offset, items));
                offset += items.Count;
            }

            ResidualReviewGroups = groups;
            ResidualDeletedByApp.Clear();
            ResidualScannedLocations = locations;
            ResidualState = offset > 0 ? ResidualScanState.CompletedWithFindings
                : groups.Any(group => group.Scan.State == ResidualScanState.Failed) ? ResidualScanState.Failed
                : groups.Any(group => group.Scan.Completed) ? ResidualScanState.CompletedNoFindings : ResidualScanState.NotScanned;
            // Review verified removals even when their scan has no findings.
            // Programs still installed belong in the queue results, not residual review.
            ResidualReviewId = groups.Count > 0 ? Guid.NewGuid().ToString("N") : null;
            ResidualSelectionVersion = 0;
            _residualReviewQueue = queue;
            ResidualItems = new(groups.SelectMany(group => group.Items));
            foreach (var item in ResidualItems) { item.IsSelected = false; item.PropertyChanged += ResidualItemChanged; }
            IsResidualOpen = groups.Count > 0;
        }
        finally { ProgressApp = terminalApp; OperationState = terminalState; }
    }

    internal void RequireOpenResidualReview(string reviewId)
    {
        if (!IsResidualOpen || IsBatchRunning || IsCleaningResiduals || ResidualReviewId != reviewId)
            throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualSelectionChanged"));
    }

    internal void OpenQueueResidualReview()
    {
        if (IsBatchRunning || IsCleaningResiduals || IsResidualOpen ||
            ResidualReviewGroups.Count == 0)
            throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualSelectionChanged"));
        ResidualReviewId = Guid.NewGuid().ToString("N");
        ResidualSelectionVersion = 0;
        ResidualItems = new(ResidualReviewGroups.SelectMany(group => group.Items));
        foreach (var item in ResidualItems)
        {
            item.IsSelected = false;
            item.PropertyChanged += ResidualItemChanged;
        }
        IsResidualOpen = true;
        OnPropertyChanged(nameof(TotalResidualSizeBytes));
        OnPropertyChanged(nameof(TotalResidualSizeDisplay));
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
        ResidualDeleteTotalCount = ResidualItems.Count(item => item.CanClean && item.IsSelected);
        ResidualDeleteProcessedCount = 0;
        LastResidualSkippedCount = 0;
        LastResidualFailedCount = 0;
        LastResidualDeletedRegistryCount = 0;
        LastResidualDeletedFileBytes = 0;
        ResidualDeletionAttempted = true;
        ResidualDeleteStopRequested = false;
        using var stop = new CancellationTokenSource();
        _residualDeleteStop = stop;
        IsCleaningResiduals = true;
        try
        {
            var deleted = 0;
            var kept = 0;
            long deletedBytes = 0;
            var deletedItems = new HashSet<ResidualItem>();
            foreach (var group in ResidualReviewGroups)
            {
                if (stop.IsCancellationRequested) break;
                var selected = group.Items.Where(item => item.CanClean && item.IsSelected).ToArray();
                if (selected.Length == 0) continue;
                ResidualDeleteCurrentApp = group.App.Name;
                var previouslyProcessed = ResidualDeleteProcessedCount;
                var progress = new DispatcherProgress<(int Processed, string Path)>(_dispatcher, update =>
                {
                    if (_residualDeleteStop != stop || ResidualDeleteProcessedCount > previouslyProcessed + update.Processed) return;
                    ResidualDeleteProcessedCount = previouslyProcessed + update.Processed;
                    ResidualDeleteCurrentPath = update.Path;
                });
                var result = await ResidualScanService.DeleteReviewedFilesAsync(group.App, _residualReviewQueue, selected, progress, stop.Token);
                deleted += result.Deleted;
                ResidualDeletedByApp[group.App.Id] = ResidualDeletedByApp.GetValueOrDefault(group.App.Id) + result.Deleted;
                kept += result.Kept.Count;
                deletedBytes += result.Bytes;
                LastResidualSkippedCount += result.Attempts.Count(attempt => attempt.Disposition is
                    ResidualScanService.PermanentDisposition.Retained or ResidualScanService.PermanentDisposition.Changed);
                LastResidualFailedCount += result.Attempts.Count(attempt => attempt.Disposition ==
                    ResidualScanService.PermanentDisposition.Inaccessible);
                ResidualDeleteProcessedCount = previouslyProcessed + result.Attempts.Count;
                foreach (var attempt in result.Attempts.Where(attempt => attempt.Disposition == ResidualScanService.PermanentDisposition.Deleted))
                {
                    deletedItems.Add(attempt.Item);
                    if (attempt.Item.Type is ResidualType.RegistryKey or ResidualType.RegistryValue)
                        LastResidualDeletedRegistryCount++;
                    else if (attempt.Item.Type is ResidualType.File or ResidualType.Shortcut)
                        LastResidualDeletedFileBytes += attempt.Item.SizeBytes;
                }
                SaveResidualHistory(group.RecordId, "ResidualPermanent:" + group.Scan.State, result.Deleted, result.Bytes,
                    $"Permanent residual deletion: {result.Deleted} deleted, {result.Kept.Count} kept.\n" +
                    string.Join("\n", result.Attempts.Select(attempt =>
                        $"{attempt.Disposition}: {attempt.Item.Path}" + (attempt.BackupPath is null ? "" : $"; backup: {attempt.BackupPath}"))));
            }
            LastResidualDeletedCount = deleted;
            LastResidualKeptCount = kept;
            LastResidualDeletedBytes = deletedBytes;
            LastResidualSkippedCount += ResidualDeleteTotalCount - ResidualDeleteProcessedCount;
            CloseQueueResidualReview();
            var offset = 0;
            ResidualReviewGroups = ResidualReviewGroups.Select(group =>
            {
                var remaining = group.Items.Where(item => !deletedItems.Contains(item)).ToArray();
                var updated = group with { Offset = offset, Items = remaining };
                offset += remaining.Length;
                return updated;
            }).ToArray();
            SetWebNotice(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                UiStrings.Get(LanguageCode, "residualPermanentResult"), deleted, kept,
                LastResidualSkippedCount, LastResidualFailedCount), kept > 0);
        }
        finally
        {
            _residualDeleteStop = null;
            IsCleaningResiduals = false;
            ResidualDeleteCurrentApp = "";
            ResidualDeleteCurrentPath = "";
            ResidualDeleteStopRequested = false;
        }
    }

    internal void StopResidualCleanup(string reviewId)
    {
        if (_residualDeleteStop is not { } stop || !IsCleaningResiduals || ResidualReviewId != reviewId)
            throw new InvalidDataException(UiStrings.Get(LanguageCode, "residualSelectionChanged"));
        stop.Cancel();
        ResidualDeleteStopRequested = true;
    }

    private void SaveResidualHistory(int recordId, string status, int count, long bytes, string detail)
    {
        try
        {
            if (status.StartsWith("ResidualPermanent:", StringComparison.Ordinal))
                _db.AddResidualCleanupResult(recordId, status, count, bytes);
            else _db.UpdateResidualScanStatus(recordId, status);
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
    }
}
