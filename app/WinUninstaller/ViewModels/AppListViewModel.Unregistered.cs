using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class AppListViewModel
{
    private readonly UnregisteredAppService _unregistered;
    public UnregisteredAppService.Plan? UnregisteredPlan { get; private set; }
    public InstalledApp? UnregisteredIdentity { get; private set; }
    public UnregisteredAppService.DeletionResult? UnregisteredDeletion { get; private set; }
    [ObservableProperty] private int _unregisteredDeletedFiles;
    [ObservableProperty] private bool _isUnregisteredOpen;
    [ObservableProperty] private bool _isUnregisteredBusy;
    [ObservableProperty] private string _unregisteredState = "Review";
    [ObservableProperty] private string _unregisteredError = "";

    private void RequireUnregisteredIdle()
    {
        if (IsUnregisteredBusy || IsBatchRunning || IsCleaningResiduals || NeedsQueueReview || HasPendingQueueReview || UninstallService.HasPendingOperation)
            throw new InvalidOperationException("UnregisteredApp:OperationBusy");
    }

    public void RequireMaintenanceIdle() => RequireUnregisteredIdle();

    public async Task RestoreResidualBackupAsync(string manifest)
    {
        RequireUnregisteredIdle();
        IsCleaningResiduals = true;
        try
        {
            using var lease = _journal.AcquireExecutionLease();
            if (QueueJournal.NeedsReview(_journal.Read())) throw new InvalidOperationException("UnregisteredApp:OperationBusy");
            var result = await _residualScanner.RestoreQuarantinedAsync(manifest);
            SetWebNotice(result.Message, !result.Success);
        }
        finally { IsCleaningResiduals = false; }
    }

    public async Task ReviewUnregisteredAsync(string target)
    {
        RequireUnregisteredIdle();
        IsConfirmOpen = false; ConfirmApp = null;
        UnregisteredPlan = null; UnregisteredIdentity = null;
        UnregisteredDeletion = null; UnregisteredDeletedFiles = 0;
        IsUnregisteredOpen = true; IsUnregisteredBusy = true;
        UnregisteredState = "Scanning"; UnregisteredError = "";
        var catalog = _allApps.ToArray();
        try
        {
            var plan = await Task.Run(() => _unregistered.Prepare(target, catalog));
            UnregisteredPlan = plan;
            UnregisteredIdentity = new InstalledApp { Id = plan.Id, Name = plan.Name, Source = "Unregistered", DisplayIcon = plan.Target, InstallLocation = plan.Root };
            UnregisteredIdentity.IconImage = _iconCache.GetIcon(UnregisteredIdentity);
            UnregisteredIdentity.CachedIconPath = _iconCache.PersistIcon(plan.Id, UnregisteredIdentity.IconImage, plan.Name);
            UnregisteredState = "Review";
        }
        catch (Exception ex) { UnregisteredState = "Unsupported"; UnregisteredError = ex.Message; }
        finally { IsUnregisteredBusy = false; OnPropertyChanged(nameof(UnregisteredPlan)); }
    }

    public void CloseUnregisteredReview()
    { if (!IsUnregisteredBusy) { IsUnregisteredOpen = false; UnregisteredPlan = null; UnregisteredIdentity = null; UnregisteredDeletion = null; } }

    public async Task ConfirmUnregisteredAsync(string expectedPlan, bool acknowledged)
    {
        RequireUnregisteredIdle();
        var plan = UnregisteredPlan;
        if (!acknowledged || !IsUnregisteredOpen || UnregisteredState != "Review" || plan?.Id != expectedPlan)
            throw new InvalidDataException("UnregisteredApp:InvalidReview");
        IsUnregisteredBusy = true; UnregisteredError = "";
        try
        {
            using var lease = _journal.AcquireExecutionLease();
            if (QueueJournal.NeedsReview(_journal.Read())) throw new InvalidOperationException("UnregisteredApp:OperationBusy");
            // Never rely solely on the catalog captured when the review opened.
            var current = await Task.Run(() => _registry.DiscoverApps(includeMsix: false));
            if (_registry.LastDiagnostics.Any(source => source.Errors != 0 || source.SourceErrors != 0 || source.TimedOut || source.Cancelled))
                throw new IOException("UnregisteredApp:InventoryUnavailable");
            UnregisteredState = "Deleting";
            var progress = new DispatcherProgress<UnregisteredAppService.DeletionProgress>(_dispatcher, value => UnregisteredDeletedFiles = value.DeletedFiles);
            UnregisteredDeletion = await Task.Run(() => _unregistered.DeletePermanently(plan, current, progress));
            FinishUnregisteredDeletion(plan);
        }
        catch (Exception ex)
        {
            UnregisteredError = ex.Message;
            UnregisteredState = Directory.Exists(plan.Root) ? "Review" : "NeedsReview";
        }
        finally { IsUnregisteredBusy = false; }
    }

    public async Task RetryUnregisteredDeletionAsync(string expectedPlan)
    {
        RequireUnregisteredIdle();
        if (!IsUnregisteredOpen || UnregisteredPlan?.Id != expectedPlan || UnregisteredState != "DeleteIncomplete" || UnregisteredDeletion?.CanRetry != true)
            throw new InvalidDataException("UnregisteredApp:InvalidReview");
        IsUnregisteredBusy = true;
        try
        {
            using var lease = _journal.AcquireExecutionLease();
            if (QueueJournal.NeedsReview(_journal.Read())) throw new InvalidOperationException("UnregisteredApp:OperationBusy");
            var knownDeleted = UnregisteredDeletion.DeletedPaths;
            UnregisteredState = "Deleting"; UnregisteredError = "";
            var progress = new DispatcherProgress<UnregisteredAppService.DeletionProgress>(_dispatcher, value => UnregisteredDeletedFiles = value.DeletedFiles);
            UnregisteredDeletion = await Task.Run(() => _unregistered.RetryPermanentDeletion(UnregisteredPlan!, knownDeleted, progress));
            FinishUnregisteredDeletion(UnregisteredPlan);
        }
        catch (Exception ex) { UnregisteredState = "DeleteIncomplete"; UnregisteredError = ex.Message; }
        finally { IsUnregisteredBusy = false; }
    }

    private void FinishUnregisteredDeletion(UnregisteredAppService.Plan plan)
    {
        var result = UnregisteredDeletion!;
        UnregisteredDeletedFiles = result.DeletedFiles;
        UnregisteredState = result.Complete ? "Deleted" : "DeleteIncomplete";
        UnregisteredError = string.Join("\n", result.Errors);
        RecordUnregisteredAction(plan.Name, plan.Id, result.Complete ? "FilesDeleted" : "FileDeletionIncomplete",
            $"Permanent application-file deletion. Verified deleted files: {result.DeletedFiles}/{plan.Files.Count}. Logical deleted bytes: {result.DeletedBytes}. No registered uninstaller was launched. " + UnregisteredError);
    }

    public bool IsUnregisteredBackup(string path) => _unregistered.IsManifest(path);

    public async Task RestoreUnregisteredAsync(string path)
    {
        RequireUnregisteredIdle(); IsUnregisteredBusy = true; UnregisteredError = "";
        var previousState = UnregisteredState;
        IsUnregisteredOpen = true; UnregisteredState = "Restoring";
        try
        {
            using var lease = _journal.AcquireExecutionLease();
            if (QueueJournal.NeedsReview(_journal.Read())) throw new InvalidOperationException("UnregisteredApp:OperationBusy");
            var result = await Task.Run(() => _unregistered.Restore(path));
            UnregisteredPlan = new(result.Id, result.Name, result.Target, result.OriginalPath, result.Kind,
                result.Files.Sum(file => file.Bytes), result.Fingerprint, Array.AsReadOnly(result.Files));
            UnregisteredIdentity = new InstalledApp { Id = result.Id, Name = result.Name, Source = "Unregistered", DisplayIcon = result.Target, InstallLocation = result.OriginalPath };
            UnregisteredIdentity.IconImage = _iconCache.GetIcon(UnregisteredIdentity);
            UnregisteredIdentity.CachedIconPath = _iconCache.PersistIcon(result.Id, UnregisteredIdentity.IconImage, result.Name);
            UnregisteredState = "Restored";
            RecordUnregisteredAction(result.Name, result.Id, "FilesRestored", "Deployment files restored from authenticated quarantine; no uninstaller was launched.");
            SetWebNotice(UiStrings.Get(LanguageCode, "unregisteredRestored"));
        }
        catch (Exception ex) { UnregisteredState = UnregisteredPlan is null ? "Unsupported" : previousState; UnregisteredError = ex.Message; SetWebNotice(ex.Message, true); }
        finally { IsUnregisteredBusy = false; }
    }

    private void RecordUnregisteredAction(string name, string id, string outcome, string notes)
    {
        try
        {
            _db.AddRecord(new UninstallRecord { AppId = id, AppName = name, InstallerType = "Unregistered files",
                CachedIconPath = UnregisteredIdentity?.Id == id ? UnregisteredIdentity.CachedIconPath : "",
                Success = false, Outcome = outcome, ExitCode = 0, UninstallDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ResidualStatus = "ResidualScan:NotScanned", Notes = notes });
            OnPropertyChanged(nameof(HistoryCount));
        }
        catch { UnregisteredError = "UnregisteredApp:HistoryUnavailable"; }
    }
}
