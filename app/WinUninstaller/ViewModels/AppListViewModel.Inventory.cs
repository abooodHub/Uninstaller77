using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using WinUninstaller.Services;

namespace WinUninstaller.ViewModels;

public partial class AppListViewModel
{
    [ObservableProperty] private string _inventoryState = "NotStarted";
    [ObservableProperty] private string _inventoryStage = "Preparing List";
    [ObservableProperty] private string _inventoryError = "";
    [ObservableProperty] private long _inventoryRevision;
    private long _scanGeneration;
    private Stopwatch? _inventoryClock;
    private long _discoveryMs, _iconsMs;
    private IReadOnlyList<RegistryDiscoveryService.DiscoveryDiagnostic> _inventoryDiagnostics = Array.Empty<RegistryDiscoveryService.DiscoveryDiagnostic>();
    public IReadOnlyList<RegistryDiscoveryService.DiscoveryDiagnostic> DiscoveryDiagnostics => _inventoryDiagnostics;
    public bool HasConfirmedInventory { get; private set; }

    private async Task LoadInventoryAsync()
    {
        if (IsBatchRunning) return;
        var generation = Interlocked.Increment(ref _scanGeneration);
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new();
        var token = _loadCts.Token;
        IsLoading = true; InventoryState = "Loading"; InventoryError = "";
        InventoryStage = "Reading Registry";
        _inventoryClock = Stopwatch.StartNew();
        var clock = _inventoryClock;
        var discovery = new RegistryDiscoveryService();
        try
        {
            var progress = new DispatcherProgress<string>(_dispatcher, stage =>
            { if (generation == _scanGeneration) InventoryStage = stage; });
            var apps = await Task.Run(() => discovery.DiscoverApps(true, token, progress), token);
            token.ThrowIfCancellationRequested();
            if (generation != _scanGeneration) return;
            _discoveryMs = clock.ElapsedMilliseconds;
            _inventoryDiagnostics = discovery.LastDiagnostics;
            if (_inventoryDiagnostics.All(source => source.Errors > 0 || source.SourceErrors > 0 || source.Cancelled || source.TimedOut))
                throw new InvalidOperationException("All application discovery sources failed. See discovery diagnostics.");
            apps = InventoryIntegrity.CurrentRegistrations(apps);
            _allApps = apps; ObserveSelection(apps); ApplyFilterAndSort(); UpdateCounts();
            HasConfirmedInventory = true; InventoryRevision = generation;
            InventoryState = apps.Count == 0 ? "Empty" : "Loaded";
            StatusText = string.Format(UiStrings.Get(LanguageCode, "foundAppsStatus"), apps.Count, RecentCount, LargeCount, StoreCount);
            OnPropertyChanged(nameof(HasConfirmedInventory)); OnPropertyChanged(nameof(DiscoveryDiagnostics));
            _ = LoadMeasuredIconsAsync(generation, token);
            WriteInventoryTiming("ListReady", generation, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation != _scanGeneration) return;
            InventoryState = "Failed"; InventoryError = ex.Message;
            StatusText = UiStrings.Get(LanguageCode, "discoveryError") + ": " + ex.Message;
        }
        finally { if (generation == _scanGeneration) IsLoading = false; }
    }

    private async Task LoadMeasuredIconsAsync(long generation, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        await LoadIconsInBackgroundAsync(token);
        if (generation != _scanGeneration || token.IsCancellationRequested) return;
        _iconsMs = clock.ElapsedMilliseconds;
        WriteInventoryTiming("IconsReady", generation, _inventoryClock?.ElapsedMilliseconds ?? 0);
    }

    public void RecordInventoryRendered(long revision, int durationMs)
    {
        if (revision != InventoryRevision || !HasConfirmedInventory) return;
        WriteInventoryTiming("Rendered", revision, _inventoryClock?.ElapsedMilliseconds ?? 0, durationMs);
    }

    private void WriteInventoryTiming(string stage, long generation, long elapsedMs, int? renderMs = null)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Logs");
        try
        {
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "inventory-timing.jsonl"), JsonSerializer.Serialize(new
            { time = DateTimeOffset.Now, generation, stage, elapsedMs, discoveryMs = _discoveryMs, iconsMs = _iconsMs,
                renderMs, sources = _inventoryDiagnostics, pid = Environment.ProcessId }) + Environment.NewLine);
        }
        catch (Exception ex) { Debug.WriteLine("Inventory timing log unavailable: " + ex.Message); }
    }
}
