using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using WinUninstaller.Models;
using WinUninstaller.Services;
using WinUninstaller.ViewModels;

namespace WinUninstaller.Views;

public partial class WebMainWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly MainViewModel _main;
    private readonly AppListViewModel _apps;
    private readonly HistoryViewModel _history;
    private sealed record EncodedIcon(string Value);
    private readonly ConditionalWeakTable<ImageSource, EncodedIcon> _icons = new();
    private readonly HashSet<INotifyCollectionChanged> _attachedCollections = [];
    private readonly HashSet<INotifyPropertyChanged> _attachedItems = [];
    private bool _closed;
    private bool _recoveringBrowser;
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private int _recoveryAttempts;
    private string? _workspacePage;
    private IReadOnlyList<BrowserExtensionInventoryService.Extension> _extensions = Array.Empty<BrowserExtensionInventoryService.Extension>();
    private IReadOnlyList<string> _detectedBrowsers = Array.Empty<string>();
    private bool _extensionLoading;
    private string? _extensionScanError;
    private bool _extensionsScanned;
    private int _extensionDiagnosticCount;
    private long _stateRevision;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Opt the WPF host into DWM dark mode.
        var handle = new WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x00121316, text = 0x00EEEEEE;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    public WebMainWindow(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        _apps = main.AppList;
        _history = main.History;
        StartupErrorTitle.Text = UiStrings.Get(_main.LanguageCode, "webUiStartFailed");
        OpenClassicButton.Content = UiStrings.Get(_main.LanguageCode, "openClassicUi");
        _apps.UseWebUi = true;
        _history.UseWebUi = true;
        _main.UseWebUi = true;
        _main.PropertyChanged += OnAnyPropertyChanged;
        _apps.PropertyChanged += OnAppsPropertyChanged;
        _history.PropertyChanged += OnHistoryPropertyChanged;
        AttachCollections();
        _stateTimer.Tick += (_, _) => { _stateTimer.Stop(); PushState(); };
        if (Environment.GetCommandLineArgs().Contains("--ui-preview-extensions"))
        {
            _workspacePage = "BrowserExtensions";
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await Dispatcher.InvokeAsync(() => ScanExtensionsAsync());
            });
        }
        Loaded += async (_, _) => await InitializeBrowserAsync();
    }

    private void ScheduleState()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.InvokeAsync(ScheduleState, DispatcherPriority.DataBind);
            return;
        }

        if (!_stateTimer.IsEnabled) _stateTimer.Start();
    }

    private async Task InitializeBrowserAsync()
    {
        WriteWebLog($"WebView2 initialization started; PID={Environment.ProcessId}; ProcessPath={Environment.ProcessPath}; " +
            $"CurrentDirectory={Environment.CurrentDirectory}; BaseDirectory={AppContext.BaseDirectory}; " +
            $"Runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; " +
            $"Architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        try
        {
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "WebView2");
            if (_measurePerformance)
                userData = Path.Combine(Path.GetTempPath(), "Uninstaller77-VisualQA", Environment.ProcessId.ToString());
            if (Environment.GetCommandLineArgs().Contains("--ui-phase13-test"))
                userData = Path.Combine(Path.GetTempPath(), "Uninstaller77-Phase13-VisualQA", Environment.ProcessId.ToString());
            Directory.CreateDirectory(userData);
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            await Browser.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            var core = Browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            var indexPath = Path.Combine(wwwroot, "index.html");
            if (!File.Exists(indexPath))
                throw new FileNotFoundException("The WebView2 entry document is missing.", indexPath);
            core.SetVirtualHostNameToFolderMapping("appassets.winuninstaller", wwwroot, CoreWebView2HostResourceAccessKind.Deny);
            StartCpuSampling(core.Environment);

            // Diagnose the local bundle without recording application names or page text.
            core.WebResourceResponseReceived += (_, e) =>
            {
                try
                {
                    if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) &&
                        uri.Host.Equals("appassets.winuninstaller", StringComparison.OrdinalIgnoreCase))
                        WriteWebLog($"Resource response; status={e.Response?.StatusCode}; path={uri.AbsolutePath}");
                }
                catch (Exception ex) { WriteWebLog("Resource response diagnostic failed: " + ex.GetType().Name); }
            };
            try
            {
                var exceptions = core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown");
                exceptions.DevToolsProtocolEventReceived += (_, e) => WriteWebLog("JavaScript exception: " + e.ParameterObjectAsJson);
                await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");

                var console = core.GetDevToolsProtocolEventReceiver("Log.entryAdded");
                console.DevToolsProtocolEventReceived += (_, e) => WriteWebLog("JavaScript console: " + e.ParameterObjectAsJson);
                await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
            }
            catch (Exception ex)
            {
                // Diagnostics must never prevent the secured local UI from opening.
                WriteWebLog("JavaScript diagnostics unavailable: " + ex.GetType().Name + ": " + ex.Message);
            }
            await core.AddScriptToExecuteOnDocumentCreatedAsync("""
                (() => {
                  const diagnostics = window.__winUninstallerDiagnostics = { errors: [] };
                  const record = (kind, message) => {
                    if (diagnostics.errors.length < 20)
                      diagnostics.errors.push({ kind, message: String(message || '').slice(0, 300) });
                  };
                  window.addEventListener('error', event => record('error', event.message), true);
                  window.addEventListener('unhandledrejection', event => record('unhandledrejection', event.reason), true);
                })();
                """);

            core.ProcessFailed += OnBrowserProcessFailed;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += (_, e) =>
            {
                if (!WebUiProtocol.IsTrustedSource(e.Uri)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += async (_, e) =>
            {
                WriteWebLog($"WebView2 navigation completed; success={e.IsSuccess}; errorStatus={e.WebErrorStatus}; source={Browser.Source}");
                if (e.IsSuccess)
                {
                    try
                    {
                        var probe = await core.ExecuteScriptAsync("JSON.stringify({href:location.href,title:document.title,readyState:document.readyState,rootExists:!!document.getElementById('root'),rootChildren:document.getElementById('root')?.childElementCount||0,bodyTextLength:document.body?.innerText?.length||0,scriptCount:document.scripts.length,styleSheetCount:document.styleSheets.length,csp:document.querySelector('meta[http-equiv=\\\"Content-Security-Policy\\\"]')?.content||'',errors:window.__winUninstallerDiagnostics?.errors||[]})");
                        WriteWebLog("Document probe: " + probe);
                    }
                    catch (Exception ex) { WriteWebLog("Document probe failed: " + ex.GetType().Name + ": " + ex.Message); }
                }
                if (!e.IsSuccess)
                {
                    BrowserContainer.Visibility = Visibility.Collapsed;
                    StartupErrorPanel.Visibility = Visibility.Visible;
                    StartupErrorText.Text = UiStrings.Get(_main.LanguageCode, "webUiLoadFailed") + " " + e.WebErrorStatus;
                }
                else
                {
                    StartupErrorPanel.Visibility = Visibility.Collapsed;
                    BrowserContainer.Visibility = Visibility.Visible;
                    ScheduleState();
                    _ = RunVisualQaAsync();
                }
            };
            Browser.Source = new Uri("https://appassets.winuninstaller/index.html");
            WriteWebLog($"WebView2 initialized; browserVersion={core.Environment.BrowserVersionString}; wwwroot={wwwroot}");
        }
        catch (Exception ex)
        {
            if (_closed) return;
            WriteWebLog("WebView2 initialization failed: " + ex);
            BrowserContainer.Visibility = Visibility.Collapsed;
            StartupErrorPanel.Visibility = Visibility.Visible;
            StartupErrorTitle.Text = UiStrings.Get(_main.LanguageCode, "webUiStartFailed");
            StartupErrorText.Text = UiStrings.Get(_main.LanguageCode, "webUiInitializeFailed") + " " + ex.Message;
        }
    }

    private async void OnBrowserProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_closed || _recoveringBrowser) return;
        if (e.ProcessFailedKind is not
            (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;

        if (_recoveryAttempts >= 2)
        {
            BrowserContainer.Visibility = Visibility.Collapsed;
            StartupErrorPanel.Visibility = Visibility.Visible;
            StartupErrorTitle.Text = UiStrings.Get(_main.LanguageCode, "webUiStartFailed");
            StartupErrorText.Text = UiStrings.Get(_main.LanguageCode, "webUiLoadFailed");
            return;
        }

        _recoveryAttempts++;
        _recoveringBrowser = true;
        StartupErrorPanel.Visibility = Visibility.Visible;
        StartupErrorTitle.Text = UiStrings.Get(_main.LanguageCode, "webUiRecovering");
        StartupErrorText.Text = UiStrings.Get(_main.LanguageCode, "webUiRecoveryHint");

        try
        {
            Browser.Dispose();
            BrowserContainer.Children.Clear();
            Browser = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml("#121316")
            };
            BrowserContainer.Children.Add(Browser);
            await InitializeBrowserAsync();
        }
        catch
        {
            if (_closed) return;
            BrowserContainer.Visibility = Visibility.Collapsed;
            StartupErrorPanel.Visibility = Visibility.Visible;
            StartupErrorTitle.Text = UiStrings.Get(_main.LanguageCode, "webUiStartFailed");
            StartupErrorText.Text = UiStrings.Get(_main.LanguageCode, "webUiInitializeFailed");
        }
        finally { _recoveringBrowser = false; }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
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

    private async Task HandleWebMessageReceivedAsync(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closed) return;
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
            if (!_closed) PostUiMessage(new { type = "response", id = request.Id, ok = true, state = BuildState() });
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
        if ((_apps.IsUnregisteredBusy || _apps.IsCleaningResiduals || _dialogPending) && request.Operation is not ("getState" or "inventoryRendered" or "performanceMark"))
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
                if (target is "SoftwareHealth" or "InstallMonitor" or "Logged" or "LoggedApps") target = "AllApps";
                if (target is "BrowserExtensions" or "ControlCenter")
                {
                    _workspacePage = target;
                }
                else
                {
                    _workspacePage = null;
                    _main.NavigateTo(target);
                }

                if (target == "BrowserExtensions" && !_extensionsScanned) await ScanExtensionsAsync();
                if (target == "ControlCenter") _history.LoadHistory();
                break;
            case "refreshControlCenter":
                RequireOnly(p);
                _history.LoadHistory();
                await _apps.LoadAppsAsync();
                break;
            case "openControlFolder":
                OpenControlFolder(ControlCenterService.ReadFolderRequest(p));
                break;
            case "openWindowsAppsSettings":
                RequireOnly(p);
                LaunchControlTarget(new System.Diagnostics.ProcessStartInfo(ControlCenterService.WindowsAppsSettingsUri) { UseShellExecute = true });
                break;
            case "exportSupportReport": RequireOnly(p); QueueDesktopDialog(() => { ExportSupportReport(); return Task.CompletedTask; }); break;
            case "scanExtensions": RequireOnly(p); await ScanExtensionsAsync(); break;
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
                FindApp(ReadString(p, "appId", 512)).IsBatchSelected = ReadBool(p, "selected");
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
                RequireOnly(p, "quiet", "scanResiduals");
                _apps.IsQuietMode = ReadBool(p, "quiet");
                _apps.AutoScanResiduals = ReadBool(p, "scanResiduals");
                break;
            case "confirmUninstall":
                RequireOnly(p, "appId");
                if (_apps.ConfirmApp == null || !string.Equals(_apps.ConfirmApp.Id, ReadString(p, "appId", 512), StringComparison.Ordinal))
                    throw new InvalidDataException("The application confirmation is stale or does not match.");
                _ = ExecuteUiOperationAsync(_apps.ConfirmAndExecuteUninstallAsync);
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
                RequireOnly(p, "appIds");
                if (_apps.SelectedCount == 0 || _apps.IsBatchRunning) throw new InvalidOperationException("Select one or more visible applications before starting a queue.");
                var ids = p.GetProperty("appIds");
                if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 1024 ||
                    ids.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException("The confirmation identity list is invalid.");
                var expectedIds = _apps.Apps.Where(app => app.IsBatchSelected).Select(app => app.Id).ToHashSet(StringComparer.Ordinal);
                var confirmedIds = ids.EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (confirmedIds.Length != expectedIds.Count || !expectedIds.SetEquals(confirmedIds))
                    throw new InvalidDataException("The selection changed. Review the confirmation again.");
                _ = ExecuteUiOperationAsync(_apps.UninstallSelectedAsync);
                break;
            case "selectResidual":
                RequireOnly(p, "index", "selected");
                var residualIndex = ReadInt(p, "index", 0, _apps.ResidualItems.Count - 1);
                var residual = _apps.ResidualItems[residualIndex];
                if (!residual.CanClean) throw new InvalidDataException("This residual candidate is informational and cannot be cleaned.");
                residual.IsSelected = ReadBool(p, "selected");
                break;
            case "selectAllResiduals": RequireOnly(p); _apps.SelectAllResiduals(); break;
            case "deselectAllResiduals": RequireOnly(p); _apps.DeselectAllResiduals(); break;
            case "skipResiduals": RequireOnly(p); _apps.SkipResidualCleanup(); break;
            case "cleanResiduals": RequireOnly(p); _ = ExecuteUiOperationAsync(_apps.ExecuteResidualCleanupAsync); break;
            case "restoreQuarantine": RequireOnly(p); _apps.RequireMaintenanceIdle(); QueueDesktopDialog(() => _main.RestoreQuarantineCommand.ExecuteAsync(null)); break;
            case "setLanguage":
                RequireOnly(p, "code");
                var lang = ReadString(p, "code", 8);
                if (lang is not ("en" or "ar")) throw new InvalidDataException("Language is not supported.");
                _main.LanguageCode = lang;
                break;
            case "loadHistory": RequireOnly(p); _history.LoadHistory(); break;
            case "setShowTestRecords": RequireOnly(p, "value"); _history.ShowTestRecords = ReadBool(p, "value"); break;
            case "openHistoryDetails":
                RequireOnly(p, "id");
                var id = ReadInt(p, "id", 1, int.MaxValue);
                _history.OpenDetails(_history.Records.FirstOrDefault(x => x.Record.Id == id) ?? throw new InvalidDataException("History row no longer exists."));
                break;
            case "closeHistoryDetails": RequireOnly(p); _history.CloseDetails(); break;
            case "clearHistory": RequireOnly(p); _history.ClearAllHistory(); break;
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

    private void ExportSupportReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = UiStrings.Get(_main.LanguageCode, "controlReportTitle"),
            Filter = "JSON (*.json)|*.json", AddExtension = true, DefaultExt = ".json",
            FileName = "Uninstaller77-Support-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        _history.LoadHistory();
        var report = ControlCenterService.SerializeSupportReport(ControlRuntime(),
            new(_apps.HasConfirmedInventory, _apps.InventoryState, _apps.TotalCount, ControlSources()),
            new(_apps.IsBatchRunning, _apps.HasPendingQueueReview, _apps.BatchState.ToString(), _apps.BatchTotal,
                _apps.BatchCompleted, _apps.BatchSuccessful, _apps.BatchFailed, _apps.BatchSkipped),
            _history.Records.Count, _history.ShowTestRecords);
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
                var items = BrowserExtensionInventoryService.ScanCurrentUser(diagnostics.Add);
                return (Browsers: BrowserExtensionInventoryService.DetectCurrentUserBrowsers(), Items: items, Diagnostics: diagnostics);
            });
            _detectedBrowsers = inventory.Browsers;
            _extensions = inventory.Items;
            _extensionDiagnosticCount = inventory.Diagnostics.Count;
            foreach (var diagnostic in inventory.Diagnostics)
                WriteWebLog($"Extension inventory incomplete: browser={diagnostic.Browser}; stage={diagnostic.Stage}; failure={diagnostic.Failure}");
            _extensionsScanned = true;
            if (_extensionDiagnosticCount > 0 && _extensions.Count == 0)
            {
                _extensionsScanned = false;
                _extensionScanError = "Incomplete";
            }
        }
        catch (Exception ex)
        {
            _extensions = Array.Empty<BrowserExtensionInventoryService.Extension>();
            _detectedBrowsers = Array.Empty<string>();
            _extensionsScanned = false;
            _extensionScanError = "Failed";
            WriteWebLog($"Extension inventory failed: {ex}");
        }
        finally { _extensionLoading = false; ScheduleState(); }
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
        if (result.Length > maxLength || result.IndexOf('\0') >= 0) throw new InvalidDataException("A string parameter is invalid or too long.");
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

    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleState();
    private void OnHistoryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.InvokeAsync(() => OnHistoryPropertyChanged(sender, e), DispatcherPriority.DataBind);
            return;
        }
        if (e.PropertyName == nameof(HistoryViewModel.Records)) AttachCollections();
        ScheduleState();
    }
    private void OnAppsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.InvokeAsync(() => OnAppsPropertyChanged(sender, e), DispatcherPriority.DataBind);
            return;
        }
        if (e.PropertyName is nameof(AppListViewModel.Apps) or nameof(AppListViewModel.ProgressLogs) or nameof(AppListViewModel.BatchResults) or nameof(AppListViewModel.ResidualItems))
            AttachCollections();
        ScheduleState();
    }
    private void AttachCollections()
    {
        DetachCollections();
        Attach(_apps.Apps);
        Attach(_apps.ProgressLogs);
        Attach(_apps.BatchResults);
        Attach(_apps.ResidualItems);
        Attach(_history.Records);
    }
    private void Attach<T>(ObservableCollection<T> collection) where T : class
    {
        _attachedCollections.Add(collection);
        collection.CollectionChanged -= OnCollectionChanged;
        collection.CollectionChanged += OnCollectionChanged;
        foreach (var item in collection)
            if (item is INotifyPropertyChanged notify)
            {
                _attachedItems.Add(notify);
                notify.PropertyChanged -= OnItemPropertyChanged;
                notify.PropertyChanged += OnItemPropertyChanged;
            }
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.InvokeAsync(() => OnCollectionChanged(sender, e), DispatcherPriority.DataBind);
            return;
        }
        if (e.Action == NotifyCollectionChangedAction.Reset) { AttachCollections(); ScheduleState(); return; }
        if (e.OldItems != null)
            foreach (var item in e.OldItems)
                if (item is INotifyPropertyChanged removed && _attachedItems.Remove(removed))
                    removed.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems != null)
            foreach (var item in e.NewItems)
                if (item is INotifyPropertyChanged notify)
                {
                    _attachedItems.Add(notify);
                    notify.PropertyChanged -= OnItemPropertyChanged;
                    notify.PropertyChanged += OnItemPropertyChanged;
                }
        ScheduleState();
    }
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleState();

    private void PushState()
    {
        if (_closed || Browser?.CoreWebView2 == null) return;
        try { Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "state", state = BuildState() }, JsonOptions)); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException ex) { WriteWebLog("State publish failed: " + ex); }
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

    private object BuildState() => new
    {
        revision = ++_stateRevision,
        measurePerformance = _measurePerformance,
        language = _main.LanguageCode,
        notice = _apps.WebNotice,
        noticeIsError = _apps.WebNoticeIsError,
        direction = _main.LayoutDirection == FlowDirection.RightToLeft ? "rtl" : "ltr",
        reduceMotion = _main.ReduceMotion,
        settingsOpen = _main.IsSettingsOpen,
        page = _workspacePage switch
        {
            "BrowserExtensions" => "extensions",
            "ControlCenter" => "control",
            _ => _main.CurrentViewModel == _history ? "history" : "apps"
        },
        activeSidebarItem = _workspacePage ?? _main.ActiveSidebarItem,
        controlPage = new
        {
            historyCount = _history.Records.Count, historyIncludesTests = _history.ShowTestRecords
        },
        extensionsPage = new
        {
            loading = _extensionLoading,
            scanned = _extensionsScanned,
            error = _extensionScanError is null ? null : UiStrings.Get(_main.LanguageCode, _extensionScanError == "Incomplete" ? "extensionScanIncomplete" : "extensionScanFailed"),
            incomplete = _extensionDiagnosticCount > 0,
            diagnosticCount = _extensionDiagnosticCount,
            browsers = _detectedBrowsers,
            items = _extensions.Select(e => new
            {
                id = e.Id,
                browser = e.Browser,
                profile = e.Profile,
                name = e.Name,
                desc = e.Description,
                version = e.Version,
                icon = e.Icon,
                isPolicyManaged = e.IsPolicyManaged,
                installationLocation = e.InstallationLocation,
                permissions = e.Permissions,
                optionalPermissions = e.OptionalPermissions,
                hostPermissions = e.HostPermissions,
                optionalHostPermissions = e.OptionalHostPermissions,
                contentScriptHosts = e.ContentScriptHosts,
                enabledState = e.EnabledState,
                installationSource = e.InstallationSource,
                managementCapability = e.ManagementCapability
            }).ToList()
        },
        navigation = new
        {
            appSection = _main.AppSectionTitle, allApps = _main.AllAppsLabel, recentlyInstalled = _main.RecentAppsLabel,
            largeApps = _main.LargeAppsLabel, storeApps = _main.StoreAppsLabel, auditLogs = _main.AuditLogsLabel,
            history = _main.HistoryLabel, restore = _main.RestoreLabel, settings = _main.SettingsLabel,
            reviewBeforeCleanup = _main.ReviewBeforeCleanupLabel, potentialLeftoversHint = _main.PotentialLeftoversHint
        },
        appsPage = new
        {
            title = _apps.PageTitle,
            total = _apps.TotalCount, hasPendingQueueReview = _apps.HasPendingQueueReview,
            filtered = _apps.FilteredCount,
            recent = _apps.RecentCount,
            large = _apps.LargeCount,
            store = _apps.StoreCount,
            bundleware = (int?)null, bundlewareAvailable = false,
            logged = _apps.LoggedCount,
            infrequent = (int?)null, usageAvailable = false,
            windowsApps = _apps.WindowsAppsCount,
            thirdPartyApps = _apps.ThirdPartyAppsCount,
            selected = _apps.SelectedCount,
            isLoading = _apps.IsLoading,
            inventoryState = _apps.InventoryState, inventoryStage = _apps.InventoryStage,
            inventoryError = _apps.InventoryError, inventoryRevision = _apps.InventoryRevision,
            hasConfirmedInventory = _apps.HasConfirmedInventory,
            discoveryDiagnostics = _apps.DiscoveryDiagnostics,
            status = _apps.StatusText,
            category = _apps.CurrentCategory.ToString(),
            sort = _apps.CurrentSort.ToString(),
            search = _apps.SearchText,
            labels = new { _apps.ShownLabel, _apps.RegisteredAppsLabel, _apps.RefreshLabel, _apps.SearchPlaceholder,
                _apps.SelectVisibleLabel, _apps.UninstallSelectedLabel, _apps.SortLabel, _apps.ApplicationColumnLabel,
                _apps.VersionColumnLabel, _apps.SizeColumnLabel, _apps.InstalledColumnLabel, _apps.ActionsColumnLabel,
                _apps.DetailsActionLabel, _apps.UninstallActionLabel, _apps.SortNameLabel,
                _apps.SortSizeLabel, _apps.SortDateLabel, _apps.SortPublisherLabel, _apps.NoMatchesLabel,
                _apps.SearchHintLabel, _apps.ScanAppsLabel, _apps.ScanRegistrationsLabel, _apps.DetailsVersionLabel,
                _apps.ArchitectureLabel, _apps.InstallerEngineLabel, _apps.EstimatedSizeLabel, _apps.InstallDateLabel,
                _apps.InstallLocationLabel, _apps.RegistryKeyLabel, _apps.UninstallCommandLabel,
                _apps.QuietUninstallCommandLabel, _apps.ConfirmUninstallLabel, _apps.UninstallModeLabel,
                _apps.StandardModeLabel, _apps.StandardModeHintLabel, _apps.QuietModeLabel, _apps.QuietModeHintLabel,
                _apps.ReviewLeftoversLabel, _apps.ReviewLeftoversHintLabel, _apps.CancelLabel, _apps.UninstallNowLabel,
                _apps.ResidualTitleLabel, _apps.ResidualSizeLabel, _apps.CheckedLocationsLabel,
                _apps.SelectAllLabel, _apps.DeselectAllLabel, _apps.QuarantineNoticeLabel,
                _apps.SkipResidualLabel, _apps.MoveToQuarantineLabel, _apps.CurrentApplicationLabel,
                _apps.CurrentStageLabel, _apps.ElapsedLabel, _apps.ResultsLabel, _apps.StopAfterCurrentLabel,
                _apps.CloseLabel, _apps.CancelOperationLabel },
            items = _apps.Apps.Select(AppDto).ToList(),
            details = _apps.DetailsApp is null ? null : AppDto(_apps.DetailsApp),
            confirm = _apps.IsConfirmOpen && _apps.ConfirmApp is not null ? AppDto(_apps.ConfirmApp) : null,
            unregistered = new
            {
                open = _apps.IsUnregisteredOpen, busy = _apps.IsUnregisteredBusy, state = _apps.UnregisteredState,
                error = _apps.UnregisteredError, planId = _apps.UnregisteredPlan?.Id,
                deletedFiles = _apps.UnregisteredDeletedFiles, deletionTotal = _apps.UnregisteredPlan?.Files.Count,
                remainingFiles = _apps.UnregisteredDeletion?.RemainingFiles,
                unverifiedMissingFiles = _apps.UnregisteredDeletion?.UnverifiedMissingFiles,
                pendingPath = _apps.UnregisteredDeletion?.PendingPath, canRetryDelete = _apps.UnregisteredDeletion?.CanRetry == true,
                fromBackup = _apps.UnregisteredPlan?.StoredReceipt is not null,
                pendingFolder = _apps.UnregisteredPlan?.StoredReceipt is null ? "PendingRemoval" : "Quarantine",
                app = _apps.UnregisteredIdentity is null ? null : AppDto(_apps.UnregisteredIdentity),
                root = _apps.UnregisteredPlan?.Root, target = _apps.UnregisteredPlan?.Target, kind = _apps.UnregisteredPlan?.Kind,
                bytes = _apps.UnregisteredPlan?.Bytes, count = _apps.UnregisteredPlan?.Files.Count,
                files = _apps.UnregisteredPlan?.Files.Select(file => new { path = file.RelativePath, bytes = file.Bytes }).Take(200).ToArray()
            },
            quiet = _apps.IsQuietMode, scanResiduals = _apps.AutoScanResiduals,
            operation = new
            {
                open = _apps.IsProgressOpen, isBatch = _apps.IsBatchQueue,
                preview = _apps.IsQueuePreview,
                isRunning = _apps.IsBatchQueue ? _apps.IsBatchRunning : _apps.IsProgressOpen,
                title = _apps.ProgressTitle, status = _apps.ProgressStatus, stage = _apps.CurrentStage,
                app = _apps.ProgressApp is null ? null : AppDto(_apps.ProgressApp),
                total = _apps.BatchTotal, processed = _apps.BatchCompleted, percent = _apps.BatchProgressPercent,
                successful = _apps.BatchSuccessful, failed = _apps.BatchFailed, skipped = _apps.BatchSkipped,
                elapsed = _apps.ElapsedTime, state = _apps.BatchState.ToString(), stateLabel = _apps.BatchStateLabel,
                operationState = _apps.OperationState.ToString(), stopRequested = _apps.StopBatchAfterCurrent,
                needsReview = _apps.NeedsQueueReview, canCancelCurrent = _apps.CanCancelCurrent,
                errorDetails = _apps.AttentionDetails, failureCode = _apps.AttentionFailureCode, exitCode = _apps.AttentionExitCode,
                retrying = _apps.VerificationRetrying, remaining = _apps.QueueRemaining,
                queueId = _apps.ReviewQueueId,
                review = new
                {
                    queueId = _apps.ReviewQueueId, recovered = !_apps.IsBatchRunning && _apps.NeedsQueueReview,
                    canAcknowledge = _apps.CanAcknowledgeReview,
                    detectedAt = _apps.ReviewSnapshot?.InterruptionDetectedAt,
                    createdAt = _apps.ReviewSnapshot?.CreatedAt,
                    observation = _apps.ReviewObservation,
                    registrationState = _apps.ReviewRegistrationState,
                    reason = UiStrings.Get(_main.LanguageCode, "queueReviewUncertain"),
                    items = _apps.QueueItems.Select(item => new { item.OperationId, appId = item.App.Id,
                        name = item.App.Name, source = item.App.Source, registryKey = item.App.RegistryKeyPath,
                        registeredCommand = item.App.SafeUninstallCommand,
                        lastState = item.State.ToString(), item.StartedAt, item.FinishedAt, evidence = item.Message, item.ExitCode }).ToArray()
                },
                queueItems = _apps.QueueItems.Select(item => new { item.OperationId, app = AppDto(item.App),
                    state = item.State.ToString(), item.StartedAt, item.FinishedAt, item.Message, item.ExitCode, item.FailureCode }).ToArray(),
                logs = _apps.ProgressLogs.ToArray(), results = _apps.BatchResults.Select(r => new
                { app = AppDto(r.App), state = r.State.ToString(), message = r.Message, label = r.StateLabel }).ToArray()
            },
            residual = new
            {
                open = _apps.IsResidualOpen, cleaning = _apps.IsCleaningResiduals,
                message = _apps.ResidualStatusMessage, stage = _apps.ResidualScanStage, scanState = _apps.ResidualState.ToString(),
                scannedLocations = _apps.ResidualScannedLocations, size = _apps.TotalResidualSizeDisplay,
                items = _apps.ResidualItems.Select((item, index) => new
                {
                    index, title = item.Title, path = item.Path, size = item.SizeDisplay,
                    type = item.Type.ToString(), confidence = item.Confidence.ToString(),
                    reason = item.Reason, canClean = item.CanClean, selected = item.IsSelected
                }).ToArray()
            }
        },
        historyPage = new
        {
            title = _history.HistoryTitle, description = _history.HistoryDescription,
            showTestRecords = _history.ShowTestRecords, empty = _history.IsEmpty,
            labels = new { _history.ShowTestRecordsLabel, _history.RefreshLabel, _history.MoreActionsLabel,
                _history.ClearHistoryLabel, _history.ApplicationLabel, _history.InstallerTypeLabel,
                _history.ResultLabel, _history.ResidualStatusLabel, _history.DateTimeLabel,
                _history.ActionsLabel, _history.NoHistoryLabel, _history.HistoryHintLabel,
                _history.DetailsActionLabel, _history.UninstallDateLabel, _history.ExitCodeLabel,
                _history.ResidualCleanupLabel, _history.RetainedIconLabel, _history.ExecutionNotesLabel,
                _history.CloseLabel },
            records = _history.Records.Select(HistoryDto).ToArray(),
            details = _history.SelectedItem is null ? null : HistoryDto(_history.SelectedItem)
        }
    };

    private object AppDto(InstalledApp app) => new
    {
        id = app.Id, name = app.Name, publisher = app.Publisher, version = app.Version,
        size = app.SizeDisplay, sizeKb = app.EstimatedSizeKB, installDate = app.DisplayInstallDate,
        architecture = app.Architecture, installerType = app.TypeDisplay, source = app.Source,
        icon = ToDataUri(app.IconImage), selected = app.IsBatchSelected,
        installLocation = app.InstallLocation, registryKey = app.RegistryKeyPath,
        uninstallCommand = app.SafeUninstallCommand, quietUninstallCommand = app.SafeQuietUninstallCommand,
        isBuiltIn = app.IsBuiltInWindowsApp, isThirdParty = app.IsThirdPartyWindowsApp,
        isBundleware = app.IsBundleware, isLogged = app.IsLogged,
        description = app.Description
    };

    private object HistoryDto(HistoryItemViewModel item) => new
    {
        id = item.Record.Id, name = item.Name, publisher = item.Publisher, version = item.Version,
        date = item.Date, installerType = item.InstallerType, success = item.Success, outcome = item.OutcomeState,
        status = item.StatusText, residualStatus = item.Record.ResidualStatus, exitCode = item.Record.ObservedExitCode,
        icon = ToDataUri(item.Icon), notes = item.Notes, test = item.IsLegacyTestRecord,
        cleanedItemsCount = item.Record.CleanedItemsCount, cleanedSizeBytes = item.Record.CleanedSizeBytes
    };

    private string? ToDataUri(ImageSource? source)
    {
        if (source is not BitmapSource bitmap) return null;
        if (_icons.TryGetValue(source, out var cached)) return cached.Value;
        try
        {
            var result = "data:image/png;base64," + Convert.ToBase64String(BitmapEncodingService.EncodePng(bitmap));
            _icons.Add(source, new(result));
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or NotSupportedException)
        {
            // An unavailable icon must not prevent every language/page/state update.
            WriteWebLog("Icon encoding unavailable: " + ex);
            _icons.Add(source, new(""));
            return null;
        }
    }

    private void OpenLegacy_Click(object sender, RoutedEventArgs e)
    {
        _apps.UseWebUi = false; _history.UseWebUi = false; _main.UseWebUi = false;
        var window = new MainWindow { DataContext = _main };
        App.ConfigureWindowLifetime(window, _apps);
        Application.Current.MainWindow = window;
        window.Show();
        Close();
    }

    private void PostUiMessage(object message)
    {
        if (_closed) return;
        try { Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions)); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        { WriteWebLog("Response could not be delivered after browser teardown: " + ex.GetType().Name); }
    }

    private void DetachCollections()
    {
        foreach (var collection in _attachedCollections) collection.CollectionChanged -= OnCollectionChanged;
        foreach (var item in _attachedItems) item.PropertyChanged -= OnItemPropertyChanged;
        _attachedCollections.Clear(); _attachedItems.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _stateTimer.Stop(); _cpuTimer?.Stop();
        _main.PropertyChanged -= OnAnyPropertyChanged;
        _apps.PropertyChanged -= OnAppsPropertyChanged;
        _history.PropertyChanged -= OnHistoryPropertyChanged;
        DetachCollections();
        Browser.Dispose();
        base.OnClosed(e);
    }
}
