using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WebView control is owned by WPF and is explicitly disposed in WebMainWindow.OnClosed.")]
public partial class WebMainWindow
{
    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // CoreWebView2 callbacks are normally raised on the WPF dispatcher, but
            // process recovery and async navigation can deliver a callback without
            // the owning dispatcher context. Every payload handler reads WPF-bound
            // collections and posts back through CoreWebView2, so normalize the
            // callback onto the window's dispatcher before touching either object.
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => HandleWebMessageReceivedAsync(sender, e)).Task.Unwrap();
                return;
            }

            await HandleWebMessageReceivedAsync(sender, e);
        }
        catch (Exception ex)
        {
            // Event handlers are async void by framework contract. Dispatcher
            // shutdown or browser teardown must be diagnosed instead of escaping
            // through WPF as an unhandled process-level exception.
            WriteWebLog("Web message callback terminated during browser teardown: " + ex);
        }
    }

    private async Task HandleWebMessageReceivedAsync(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed || !ReferenceEquals(sender, Browser.CoreWebView2)) return;
        if (!WebUiProtocol.IsTrustedSource(e.Source)) return;
        string id = "";
        string operation = "<unparsed>";
        try
        {
            string json;
            try { json = e.WebMessageAsJson; }
            catch (Exception ex)
            {
                PostUiMessage(new { type = "error", id = "", error = "Malformed envelope: " + ex.Message });
                return;
            }

            if (!WebUiProtocol.TryParse(json, out var request, out var error))
            {
                PostUiMessage(new { type = "error", id = "", error });
                return;
            }

            id = request!.Id;
            operation = request.Operation;
            WriteWebLog("UI request received: op=" + operation);
            await DispatchAsync(request);
            if (!_closed)
            {
                if (operation is "inventoryRendered" or "performanceMark")
                    PostUiMessage(new { type = "response", id = request.Id, ok = true });
                else
                    PostUiMessage(new { type = "response", id = request.Id, ok = true, state = BuildState() });
            }
            if (!_closed && !_uiReady && operation == "getState")
            {
                _uiReady = true;
                _uiReadyTimer.Stop();
                StartupErrorPanel.Visibility = Visibility.Collapsed;
                BrowserContainer.Visibility = Visibility.Visible;
                WriteWebLog("React desktop bridge ready.");
#if DEBUG
                _ = RunVisualQaAsync();
#endif
            }
        }
        catch (Exception ex)
        {
            WriteWebLog($"Message handler error; operation={operation}; id={id}: {ex}");
            var error = UiStrings.IsDispatcherAccessError(ex)
                ? UiStrings.Get(_apps.LanguageCode, "uiThreadAccessError")
                : ex.Message;
            PostUiMessage(new { type = "response", id, ok = false, error });
        }
    }

    private async Task DispatchAsync(WebUiRequest request)
    {
        var p = request.Payload;
        if (!WebUiProtocol.CanDispatch(request.Operation, _apps.IsUnregisteredBusy, _apps.IsCleaningResiduals, _dialogPending))
            throw new InvalidOperationException("UnregisteredApp:OperationBusy");
        switch (request.Operation)
        {
            case "closeUnregistered": RequireOnly(p); _apps.CloseUnregisteredReview(); break;
            case "confirmUnregistered":
                var planId = UnregisteredAppService.ReadAction(p, confirmation: true);
                _ = ExecuteUiOperationAsync(() => _apps.ConfirmUnregisteredAsync(planId, true));
                break;
            case "retryUnregisteredDelete":
                var retryPlan = UnregisteredAppService.ReadAction(p, confirmation: true);
                _ = ExecuteUiOperationAsync(() => _apps.RetryUnregisteredDeletionAsync(retryPlan));
                break;
            case "getState": RequireOnly(p); break;
            case "performanceMark":
                RequireOnly(p, "action");
                var performanceAction = ReadString(p, "action", 24);
                if (performanceAction is not ("Idle" or "Scroll" or "Hover" or "Sort" or "Search")) throw new InvalidDataException("Unknown performance action.");
                if (_measurePerformance) _performanceAction = performanceAction;
                break;
            case "navigate":
                RequireOnly(p, "target");
                var target = ReadString(p, "target", 32);
                if (target is "Bundleware" or "Infrequent" or "InfrequentApps")
                { _apps.SetWebNotice(UiStrings.Get(_main.LanguageCode, "classificationUnavailable")); break; }
                if (target is "SoftwareHealth" or "Logged" or "LoggedApps") target = "AllApps";
                if (target is "BrowserExtensions" or "StartupApps" or "InstallMonitor")
                {
                    _workspacePage = target;
                }
                else
                {
                    _workspacePage = null;
                    _main.NavigateTo(target);
                }

                if (target == "BrowserExtensions" && !_extensionsScanned) await ScanExtensionsAsync();
                if (target == "StartupApps" && _startupInventory is null) _ = ScanStartupAppsAsync();
                if (target == "InstallMonitor") LoadInstallationLogs();
                break;
            case "startInstallationRecording":
                RequireOnly(p, "registryScope");
                if (CurrentWebPage != "install-monitor" || _installationFinish is not null) throw new InvalidOperationException("An installation recording is already active or the monitor is closed.");
                _apps.RequireMaintenanceIdle();
                var registryScope = ReadString(p, "registryScope", 192).Trim();
                if (registryScope.Length > 0 && !InstallationScope.Valid(registryScope))
                    throw new InvalidDataException(UiStrings.Get(_main.LanguageCode, "monitorInvalidScope"));
                QueueDesktopDialog(() => ChooseInstallationScopeAsync(registryScope));
                break;
            case "finishInstallationRecording":
                RequireOnly(p, "id");
                if (CurrentWebPage != "install-monitor" || _installationFinish is null || _installationCancel?.IsCancellationRequested == true ||
                    _activeInstallation is not { State: "Monitoring" } active || active.Id != ReadString(p, "id", 32))
                    throw new InvalidOperationException("The current installation recording is not ready to finish.");
                _installationFinish.Cancel();
                ScheduleState();
                break;
            case "cancelInstallationRecording":
                RequireOnly(p);
                if (CurrentWebPage != "install-monitor" || _installationFinish is null || _installationFinish.IsCancellationRequested || _installationCancel is null)
                    throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "monitorNoActiveRecording"));
                _installationCancel.Cancel();
                break;
            case "exportInstallationRecording":
                RequireOnly(p, "id");
                var reportId = ReadString(p, "id", 32);
                var installationReport = _activeInstallation?.Id == reportId ? _activeInstallation : _installationLogs.FirstOrDefault(record => record.Id == reportId);
                if (CurrentWebPage != "install-monitor" || _installationFinish is not null || installationReport is null || installationReport.State is "Capturing" or "Monitoring")
                    throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "monitorReportUnavailable"));
                QueueDesktopDialog(() => ExportInstallationReportAsync(installationReport));
                break;
            case "refreshControlCenter":
                RequireOnly(p);
                _history.RefreshCount();
                await _apps.LoadAppsAsync();
                break;
            case "openControlFolder":
                OpenControlFolder(ControlCenterService.ReadFolderRequest(p));
                break;
            case "openWindowsAppsSettings":
                RequireOnly(p);
                LaunchControlTarget(new System.Diagnostics.ProcessStartInfo(ControlCenterService.WindowsAppsSettingsUri) { UseShellExecute = true });
                break;
            case "reviewProgramFile":
                RequireOnly(p);
                _apps.RequireMaintenanceIdle();
                if (CurrentWebPage != "apps" || _apps.IsLoading || _apps.IsConfirmOpen || _apps.IsDetailsOpen ||
                    _apps.IsUnregisteredOpen || _apps.IsResidualOpen || _apps.IsProgressOpen || _main.IsSettingsOpen)
                    throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "unregisteredBusy"));
                QueueDesktopDialog(ReviewProgramFileAsync);
                break;
            case "exportSupportReport": RequireOnly(p); QueueDesktopDialog(() => { ExportSupportReport(); return Task.CompletedTask; }); break;
            case "scanExtensions": RequireOnly(p); await ScanExtensionsAsync(); break;
            case "openExtensionManager":
                OpenExtensionManager(p);
                break;
            case "scanStartupApps":
                RequireOnly(p);
                if (CurrentWebPage != "startup") throw new InvalidDataException("Startup apps page is not open.");
                if (!_startupLoading) _startupBatchResult = null;
                _ = ScanStartupAppsAsync();
                break;
            case "openStartupSettings":
                RequireOnly(p);
                LaunchControlTarget(new System.Diagnostics.ProcessStartInfo(StartupInventoryService.SettingsUri) { UseShellExecute = true });
                break;
            case "setStartupEnabled":
                RequireOnly(p, "id", "token", "enabled");
                await SetStartupEnabledAsync(ReadString(p, "id", 32768), ReadString(p, "token", 64), ReadBool(p, "enabled"));
                break;
            case "setStartupBatchEnabled":
                StartStartupBatch(p);
                break;
            case "exportApps": RequireOnly(p); QueueDesktopDialog(() => { ExportApps(); return Task.CompletedTask; }); break;
            case "refresh": RequireOnly(p); await _apps.LoadAppsAsync(); break;
            case "search": RequireOnly(p, "value"); _apps.SearchText = ReadString(p, "value", 256); break;
            case "sort":
                RequireOnly(p, "value");
                var sort = ReadString(p, "value", 24);
                if (sort is not ("NameAsc" or "NameDesc" or "SizeAsc" or "SizeDesc" or "DateDesc" or "PublisherAsc")) throw new InvalidDataException("Sort mode is not supported.");
                _apps.SetSort(sort);
                break;
            case "category":
                RequireOnly(p, "value");
                var category = ReadString(p, "value", 24);
                if (category is "Bundleware" or "Infrequent" or "Logged")
                { _apps.SetWebNotice(UiStrings.Get(_main.LanguageCode, "classificationUnavailable")); break; }
                _apps.SetCategory(category);
                break;
            case "selectAll": RequireOnly(p, "selected"); _apps.SelectAllVisible(ReadBool(p, "selected")); break;
            case "selectApp":
                RequireOnly(p, "appId", "selected");
                var selectedApp = FindApp(ReadString(p, "appId", 512));
                selectedApp.IsBatchSelected = ReadBool(p, "selected") && selectedApp.CanUninstall;
                break;
            case "openDetails":
                RequireOnly(p, "appId");
                _apps.OpenDetails(FindApp(ReadString(p, "appId", 512)));
                break;
            case "closeDetails": RequireOnly(p); _apps.CloseDetails(); break;
            case "promptUninstall":
                RequireOnly(p, "appId");
                _apps.PromptUninstall(FindApp(ReadString(p, "appId", 512)));
                break;
            case "cancelConfirm": RequireOnly(p); _apps.CancelConfirm(); break;
            case "setUninstallOptions":
                RequireOnly(p, "quiet", "scanResiduals", "deepResiduals");
                var quiet = ReadBool(p, "quiet");
                var scanResiduals = ReadBool(p, "scanResiduals");
                _apps.IsQuietMode = quiet;
                _apps.AutoScanResiduals = scanResiduals;
                _apps.DeepResidualScan = p.TryGetProperty("deepResiduals", out _) && ReadBool(p, "deepResiduals");
                break;
            case "confirmUninstall":
                var singleOptions = ReadConfirmationOptions(p, "appId");
                if (_apps.ConfirmApp == null || !string.Equals(_apps.ConfirmApp.Id, ReadString(p, "appId", 512), StringComparison.Ordinal))
                    throw new InvalidDataException("The application confirmation is stale or does not match.");
                _ = ExecuteUiOperationAsync(singleOptions is { } single
                    ? () => _apps.ConfirmAndExecuteUninstallAsync(single.Protect, single.Review)
                    : _apps.ConfirmAndExecuteUninstallAsync);
                break;
            case "cancelProgress": RequireOnly(p); _apps.CancelProgress(); break;
            case "closeProgress": RequireOnly(p); _apps.CloseProgress(); break;
            case "stopBatch": RequireOnly(p); _apps.StopBatch(); break;
            case "retryQueueVerification": RequireOnly(p); await _apps.RetryQueueVerificationAsync(); break;
            case "reviewQueue": RequireOnly(p); _apps.MarkQueueForReview(); break;
            case "openQueueReview": RequireOnly(p); _apps.OpenQueueReview(); break;
            case "acknowledgeQueueReview":
                RequireOnly(p, "queueId", "acknowledged");
                if (!ReadBool(p, "acknowledged")) throw new InvalidDataException("Explicit acknowledgment is required.");
                _apps.AcknowledgeUnresolvedQueue(ReadString(p, "queueId", 128));
                break;
            case "inventoryRendered":
                RequireOnly(p, "revision", "durationMs");
                _apps.RecordInventoryRendered(ReadInt(p, "revision", 0, int.MaxValue), ReadInt(p, "durationMs", 0, 60000));
                break;
            case "runBatch":
                var batchOptions = ReadConfirmationOptions(p, "appIds");
                if (_apps.SelectedCount == 0 || _apps.IsBatchRunning) throw new InvalidOperationException("Select one or more visible applications before starting a queue.");
                var ids = p.GetProperty("appIds");
                if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 1024 ||
                    ids.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException("The confirmation identity list is invalid.");
                var expectedIds = _apps.Apps.Where(app => app.IsBatchSelected && app.CanUninstall).Select(app => app.Id).ToHashSet(StringComparer.Ordinal);
                var confirmedIds = ids.EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (confirmedIds.Length != expectedIds.Count || !expectedIds.SetEquals(confirmedIds))
                    throw new InvalidDataException("The selection changed. Review the confirmation again.");
                _ = ExecuteUiOperationAsync(batchOptions is { } batch
                    ? () => _apps.UninstallConfirmedSelectionAsync(batch.Protect, batch.Review)
                    : _apps.UninstallSelectedAsync);
                break;
            case "openResidualReview": RequireOnly(p); _apps.OpenQueueResidualReview(); break;
            case "selectResidual":
                RequireOnly(p, "reviewId", "index", "selected");
                _apps.RequireOpenResidualReview(ReadString(p, "reviewId", 32));
                var residualIndex = ReadInt(p, "index", 0, _apps.ResidualItems.Count - 1);
                var residual = _apps.ResidualItems[residualIndex];
                if (!residual.CanClean) throw new InvalidDataException("This residual candidate is informational and cannot be cleaned.");
                residual.IsSelected = ReadBool(p, "selected");
                break;
            case "selectAllResiduals": RequireOnly(p, "reviewId"); _apps.RequireOpenResidualReview(ReadString(p, "reviewId", 32)); _apps.SelectAllResiduals(); break;
            case "selectResidualGroup":
                var residualKind = p.TryGetProperty("kind", out _) ? ReadString(p, "kind", 16) : null;
                if (residualKind is not null && residualKind is not ("files" or "registry" or "system"))
                    throw new InvalidDataException("Unknown residual category.");
                if (residualKind is null) RequireOnly(p, "reviewId", "appId", "selected");
                else RequireOnly(p, "reviewId", "appId", "selected", "kind");
                _apps.SelectResidualGroup(ReadString(p, "reviewId", 32), ReadString(p, "appId", 512), ReadBool(p, "selected"), residualKind);
                break;
            case "deselectAllResiduals": RequireOnly(p, "reviewId"); _apps.RequireOpenResidualReview(ReadString(p, "reviewId", 32)); _apps.DeselectAllResiduals(); break;
            case "skipResiduals": RequireOnly(p, "reviewId"); _apps.RequireOpenResidualReview(ReadString(p, "reviewId", 32)); _apps.SkipResidualCleanup(); break;
            case "stopResidualCleanup":
                RequireOnly(p, "reviewId"); _apps.StopResidualCleanup(ReadString(p, "reviewId", 32)); break;
            case "deleteReviewedResiduals":
                RequireOnly(p, "reviewId", "selectionVersion", "confirmed");
                var residualReviewId = ReadString(p, "reviewId", 32);
                var selectionVersion = ReadInt(p, "selectionVersion", 0, int.MaxValue);
                _apps.RequireCurrentResidualReview(residualReviewId, selectionVersion, ReadBool(p, "confirmed"));
                _ = ExecuteUiOperationAsync(() => _apps.DeleteQueueResidualsAsync(residualReviewId, selectionVersion));
                break;
            case "restoreQuarantine": RequireOnly(p); _apps.RequireMaintenanceIdle(); QueueDesktopDialog(() => _main.RestoreQuarantineCommand.ExecuteAsync(null)); break;
            case "reviewPendingDeletion": RequireOnly(p); _apps.RequireMaintenanceIdle(); QueueDesktopDialog(_main.ReviewPendingDeletionAsync); break;
            case "setLanguage":
                RequireOnly(p, "code");
                var lang = ReadString(p, "code", 8);
                if (lang is not ("en" or "ar")) throw new InvalidDataException("Language is not supported.");
                _main.LanguageCode = lang;
                break;
            case "setReduceMotion":
                RequireOnly(p, "enabled");
                _main.ReduceMotion = ReadBool(p, "enabled");
                break;
            case "setQueueProtection":
                RequireOnly(p, "enabled");
                _main.ProtectBeforeUninstall = ReadBool(p, "enabled");
                break;
            case "openSystemRestore":
                RequireOnly(p);
                _apps.OpenSystemRestoreCommand.Execute(null);
                break;
            case "loadHistory": RequireOnly(p); _history.LoadHistory(); break;
            case "changeHistoryPage":
                RequireOnly(p, "direction", "page");
                if (CurrentWebPage != "history" || ReadInt(p, "page", 1, int.MaxValue) != _history.PageNumber)
                    throw new InvalidDataException("The history page has changed.");
                switch (ReadString(p, "direction", 5))
                {
                    case "older": _history.OpenOlderPage(); break;
                    case "newer": _history.OpenNewerPage(); break;
                    default: throw new InvalidDataException("Unsupported history page direction.");
                }
                break;
            case "setShowTestRecords": RequireOnly(p, "value"); _history.ShowTestRecords = ReadBool(p, "value"); break;
            case "openHistoryDetails":
                RequireOnly(p, "id");
                var id = ReadInt(p, "id", 1, int.MaxValue);
                _history.OpenDetails(_history.Records.FirstOrDefault(x => x.Record.Id == id) ?? throw new InvalidDataException("History row no longer exists."));
                break;
            case "closeHistoryDetails": RequireOnly(p); _history.CloseDetails(); break;
            case "rescanHistoryResiduals":
                RequireOnly(p, "id", "deep");
                if (CurrentWebPage != "history") throw new InvalidDataException("History page is not open.");
                var rescanId = ReadInt(p, "id", 1, int.MaxValue);
                var rescanRecord = _history.Records.FirstOrDefault(x => x.Record.Id == rescanId)?.Record
                    ?? throw new InvalidDataException("History row no longer exists.");
                if (!ResidualHistoryIdentity.TryRestore(rescanRecord, out _))
                    throw new InvalidDataException(UiStrings.Get(_main.LanguageCode, "residualHistoryRescanUnavailable"));
                var deepRescan = ReadBool(p, "deep");
                _history.CloseDetails();
                _ = ExecuteUiOperationAsync(() => _apps.RescanHistoryResidualsAsync(rescanId, deepRescan));
                break;
            case "clearHistory":
                RequireOnly(p);
                QueueDesktopDialog(() => { _history.ClearAllHistory(); return Task.CompletedTask; });
                break;
            case "openSettings": RequireOnly(p); _main.OpenSettings(); break;
            case "closeSettings": RequireOnly(p); _main.CloseSettings(); break;
            case "openProductWebsite":
                RequireOnly(p);
                LaunchControlTarget(ProductWebsite.CreateLaunchInfo());
                break;
            default: throw new InvalidDataException("Operation is not authorized.");
        }
    }

    private InstalledApp FindApp(string id) => _apps.Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))
        ?? throw new InvalidDataException("Application is not in the current visible list.");

    private ControlCenterService.SourceInfo[] ControlSources() => _apps.DiscoveryDiagnostics.Select(source => new ControlCenterService.SourceInfo(
        source.Source, source.Scanned, source.Accepted, source.Errors, source.SourceErrors, source.TimedOut, source.Cancelled,
        source.ElapsedMs, ControlCenterService.ClassifySource(source.Accepted, source.Errors, source.SourceErrors, source.TimedOut, source.Cancelled))).ToArray();

    private ControlCenterService.RuntimeInfo ControlRuntime() => ControlCenterService.GetRuntimeInfo(Browser.CoreWebView2?.Environment.BrowserVersionString);

    private void OpenControlFolder(string destination)
    {
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller");
        var folder = ControlCenterService.ResolveFolder(destination, dataRoot, AppContext.BaseDirectory);
        Directory.CreateDirectory(folder);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        { UseShellExecute = true };
        start.ArgumentList.Add(folder);
        LaunchControlTarget(start);
    }

    private void LaunchControlTarget(System.Diagnostics.ProcessStartInfo start)
    {
        try { using var process = System.Diagnostics.Process.Start(start); }
        catch (Exception ex)
        {
            WriteWebLog("Control Center launch failed: " + ex);
            throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "controlLaunchFailed"), ex);
        }
    }

    private bool _dialogPending;
    private void QueueDesktopDialog(Func<Task> action)
    {
        _dialogPending = true;
        // Acknowledge before opening a native modal picker. User time spent in
        // that picker must not be reported as a failed WebView request timeout.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(async () =>
        {
            try { if (!_closed) await ExecuteUiOperationAsync(action); }
            finally { _dialogPending = false; }
        }));
    }

    private async Task ReviewProgramFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = UiStrings.Get(_main.LanguageCode, "programFileTitle"),
            Filter = "EXE (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true && !_closed)
            await _apps.PromptUninstallFromExecutableAsync(dialog.FileName);
    }

    private void ExportSupportReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = UiStrings.Get(_main.LanguageCode, "controlReportTitle"),
            Filter = "JSON (*.json)|*.json", AddExtension = true, DefaultExt = ".json",
            FileName = "Uninstaller77-Support-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        _history.RefreshCount();
        var report = ControlCenterService.SerializeSupportReport(ControlRuntime(),
            new(_apps.HasConfirmedInventory, _apps.InventoryState, _apps.TotalCount, ControlSources()),
            new(_apps.IsBatchRunning, _apps.HasPendingQueueReview, _apps.BatchState.ToString(), _apps.BatchTotal,
                _apps.BatchCompleted, _apps.BatchSuccessful, _apps.BatchFailed, _apps.BatchSkipped),
            _history.TotalCount, _history.ShowTestRecords);
        File.WriteAllText(dialog.FileName, report, new UTF8Encoding(false));
        _apps.SetWebNotice(UiStrings.Get(_main.LanguageCode, "controlReportSaved"));
    }

    private async Task ScanExtensionsAsync()
    {
        if (_extensionLoading) return;
        _extensionLoading = true;
        _extensionScanError = null;
        _extensionDiagnosticCount = 0;
        ScheduleState();
        try
        {
            var inventory = await Task.Run(() =>
            {
                var diagnostics = new List<BrowserExtensionInventoryService.ScanDiagnostic>();
                var scanned = BrowserExtensionInventoryService.ScanCurrentUser(diagnostics.Add);
                var installed = scanned.Select(item => item.Browser).Distinct(StringComparer.Ordinal)
                    .Where(browser => BrowserExtensionManager.FindExecutable(browser) is not null).ToHashSet(StringComparer.Ordinal);
                var items = scanned.Select(item => installed.Contains(item.Browser) && BrowserExtensionManager.IsSupportedProfile(item.Browser, item.Profile)
                    ? item with { ManagementCapability = "Browser" } : item).ToArray();
                return (Browsers: BrowserExtensionInventoryService.DetectCurrentUserBrowsers(), Items: items, Diagnostics: diagnostics);
            });
            _detectedBrowsers = inventory.Browsers;
            _extensions = inventory.Items;
            _extensionDiagnosticCount = inventory.Diagnostics.Count;
            foreach (var diagnostic in inventory.Diagnostics)
                WriteWebLog($"Extension inventory incomplete: browser={diagnostic.Browser}; stage={diagnostic.Stage}; failure={diagnostic.Failure}");
            _extensionsScanned = true;
            if (_extensionDiagnosticCount > 0 && _extensions.Length == 0)
            {
                _extensionsScanned = false;
                _extensionScanError = "Incomplete";
            }
        }
        catch (Exception ex)
        {
            _extensions = [];
            _detectedBrowsers = Array.Empty<string>();
            _extensionsScanned = false;
            _extensionScanError = "Failed";
            WriteWebLog($"Extension inventory failed: {ex}");
        }
        finally { _extensionLoading = false; ScheduleState(); }
    }

    private void OpenExtensionManager(JsonElement payload)
    {
        RequireOnly(payload, "browser", "profile", "ids");
        var browser = ReadString(payload, "browser", 40);
        var profile = ReadString(payload, "profile", 40);
        if (CurrentWebPage != "extensions" || _extensionLoading || !_extensionsScanned ||
            !BrowserExtensionManager.IsSupportedProfile(browser, profile) || !payload.TryGetProperty("ids", out var ids) ||
            ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 500)
            throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "extensionManagerFailed"));

        var requested = new HashSet<string>(StringComparer.Ordinal);
        var available = _extensions.Where(item => item.Browser == browser && item.Profile == profile &&
            item.ManagementCapability == "Browser" && !item.IsPolicyManaged).ToLookup(item => item.Id, StringComparer.Ordinal);
        foreach (var value in ids.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } id || !BrowserExtensionManager.IsExtensionId(id) ||
                !requested.Add(id) || !available[id].Any(item => File.Exists(Path.Combine(item.InstallationLocation, "manifest.json"))))
                throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "extensionManagerFailed"));
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(BrowserExtensionManager.CreateLaunch(browser, profile,
                requested.Count == 1 ? requested.Single() : null));
            if (process is null) throw new InvalidOperationException("Browser launch did not return a process.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "extensionManagerFailed"), ex);
        }
    }

    private async Task SetStartupEnabledAsync(string id, string token, bool enabled)
    {
        if (CurrentWebPage != "startup" || _startupLoading || _startupInventory?.Items.FirstOrDefault(item => item.Id == id) is not { } entry)
            throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "startupChangeFailed"));
        _startupBatchResult = null;
        _startupLoading = true;
        ScheduleState();
        try
        {
            await Task.Run(() => StartupInventoryService.SetEnabled(entry, token, enabled));
        }
        catch (Exception ex) when (StartupInventoryService.IsChangeFailure(ex))
        {
            throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "startupChangeFailed"), ex);
        }
        finally
        {
            _startupLoading = false;
            await ScanStartupAppsAsync();
        }
    }

    private void StartStartupBatch(JsonElement payload)
    {
        RequireOnly(payload, "snapshotId", "indices", "enabled");
        var failureMessage = UiStrings.Get(_main.LanguageCode, "startupChangeFailed");
        if (CurrentWebPage != "startup" || _startupLoading || _startupFailed || _startupInventory is not { } inventory ||
            !payload.TryGetProperty("indices", out var selection) || selection.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(failureMessage);

        var indices = new List<int>();
        foreach (var value in selection.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var index))
                throw new InvalidDataException(failureMessage);
            indices.Add(index);
        }

        var enabled = ReadBool(payload, "enabled");
        StartupInventoryService.Entry[] entries;
        try { entries = StartupInventoryService.ResolveBatch(inventory, ReadString(payload, "snapshotId", 32), indices, enabled); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException(failureMessage, ex); }
        _ = ExecuteUiOperationAsync(() => ApplyStartupBatchAsync(entries, enabled));
    }

    private async Task ApplyStartupBatchAsync(StartupInventoryService.Entry[] entries, bool enabled)
    {
        _startupBatchResult = null;
        _startupLoading = true;
        ScheduleState();
        try
        {
            _startupBatchResult = await Task.Run(() => StartupInventoryService.ApplyBatch(entries, enabled,
                StartupInventoryService.SetEnabled, ex => WriteWebLog($"Startup batch item failed: {ex}")));
        }
        finally
        {
            _startupLoading = false;
            await ScanStartupAppsAsync();
        }
    }

    private async Task ScanStartupAppsAsync()
    {
        if (_startupLoading) return;
        _startupLoading = true;
        _startupFailed = false;
        ScheduleState();
        try
        {
            _startupInventory = await Task.Run(StartupInventoryService.Scan);
        }
        catch (Exception ex)
        {
            _startupFailed = true;
            WriteWebLog($"Startup inventory failed: {ex}");
        }
        finally
        {
            _startupLoading = false;
            ScheduleState();
        }
    }

    private void ExportApps()
    {
        if (!_apps.HasConfirmedInventory) throw new InvalidOperationException(UiStrings.Get(_main.LanguageCode, "exportRequiresInventory"));
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = _main.LanguageCode == "ar" ? "تصدير النتائج المفلترة" : "Export filtered results",
            Filter = "CSV (*.csv)|*.csv", FileName = "Uninstaller77-filtered-programs.csv", AddExtension = true
        };
        if (picker.ShowDialog(this) != true) return;
        var rows = CsvExportService.FilteredResults(_apps.Apps);
        File.WriteAllLines(picker.FileName, rows, new UTF8Encoding(true));
        _apps.SetWebNotice(_main.LanguageCode == "ar" ? $"تم تصدير {rows.Count - 1} من النتائج المفلترة." : $"Exported {rows.Count - 1} filtered results.", false);
    }

    private static (bool Protect, bool Review)? ReadConfirmationOptions(JsonElement payload, string identity)
    {
        if (payload.TryGetProperty("protect", out _) || payload.TryGetProperty("review", out _))
        {
            RequireOnly(payload, identity, "protect", "review");
            return (ReadBool(payload, "protect"), ReadBool(payload, "review"));
        }
        RequireOnly(payload, identity);
        return null;
    }

    private static void RequireOnly(JsonElement payload, params string[] fields)
    {
        var actual = payload.EnumerateObject().Select(x => x.Name).ToArray();
        if (actual.Length != fields.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            fields.Any(field => !actual.Contains(field, StringComparer.Ordinal)))
            throw new InvalidDataException("Operation payload has missing or unexpected fields.");
    }
    private static string ReadString(JsonElement payload, string name, int maxLength)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidDataException("A string parameter is required.");
        var result = value.GetString() ?? "";
        if (result.Length > maxLength || result.Contains('\0')) throw new InvalidDataException("A string parameter is invalid or too long.");
        return result;
    }
    private static bool ReadBool(JsonElement payload, string name) => payload.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new InvalidDataException("A boolean parameter is required.");
    private static int ReadInt(JsonElement payload, string name, int min, int max)
    {
        if (!payload.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result) || result < min || result > max)
            throw new InvalidDataException("An integer parameter is outside the allowed range.");
        return result;
    }

    private static void WriteWebLog(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "webview.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private async Task ExecuteUiOperationAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) { _apps.SetWebNotice(ex.Message, true); WriteWebLog(ex.ToString()); }
        finally { ScheduleState(); }
    }
}
