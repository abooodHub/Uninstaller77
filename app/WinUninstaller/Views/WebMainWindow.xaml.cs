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
    private readonly string? _webViewDataFolder;
    private sealed record EncodedIcon(string Value);
    private readonly ConditionalWeakTable<ImageSource, EncodedIcon> _icons = new();
    private readonly HashSet<INotifyCollectionChanged> _attachedCollections = [];
    private readonly HashSet<INotifyPropertyChanged> _attachedItems = [];
    private bool _closed;
    private bool _recoveringBrowser;
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _uiReadyTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _uiReady;
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

    public WebMainWindow(MainViewModel main, string? webViewDataFolder = null)
    {
        InitializeComponent();
        _main = main;
        _apps = main.AppList;
        _history = main.History;
        _webViewDataFolder = string.IsNullOrWhiteSpace(webViewDataFolder) ? null : Path.GetFullPath(webViewDataFolder);
        StartupErrorTitle.Text = UiStrings.Get(_main.LanguageCode, "webUiStartFailed");
        OpenClassicButton.Content = UiStrings.Get(_main.LanguageCode, "openClassicUi");
        _apps.UseWebUi = true;
        _main.UseWebUi = true;
        _main.PropertyChanged += OnAnyPropertyChanged;
        _apps.PropertyChanged += OnAppsPropertyChanged;
        _history.PropertyChanged += OnHistoryPropertyChanged;
        AttachCollections();
        _stateTimer.Tick += (_, _) => { _stateTimer.Stop(); PushState(); };
        _uiReadyTimer.Tick += (_, _) =>
        {
            _uiReadyTimer.Stop();
            WriteWebLog("Local document did not complete its initial state request.");
            ShowWebStartupError(UiStrings.Get(_main.LanguageCode, "webUiReadyTimeout"));
        };
#if DEBUG
        if (Environment.GetCommandLineArgs().Contains("--ui-preview-extensions"))
        {
            _workspacePage = "BrowserExtensions";
            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                await Dispatcher.InvokeAsync(() => ScanExtensionsAsync());
            });
        }
#endif
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
            var userData = _webViewDataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "WebView2");
            if (_measurePerformance)
                userData = Path.Combine(Path.GetTempPath(), "Uninstaller77-VisualQA", Environment.ProcessId.ToString());
#if DEBUG
            if (Environment.GetCommandLineArgs().Contains("--ui-phase13-test"))
                userData = Path.Combine(Path.GetTempPath(), "Uninstaller77-Phase13-VisualQA", Environment.ProcessId.ToString());
#endif
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
                if (!WebUiProtocol.IsTrustedSource(e.Uri)) { e.Cancel = true; return; }
                _uiReady = false;
                _uiReadyTimer.Stop();
                _uiReadyTimer.Start();
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
                if (_closed || !ReferenceEquals(core, Browser.CoreWebView2)) return;
                if (!e.IsSuccess)
                {
                    _uiReadyTimer.Stop();
                    ShowWebStartupError(UiStrings.Get(_main.LanguageCode, "webUiLoadFailed") + " " + e.WebErrorStatus);
                }
                else ScheduleState();
            };
            Browser.Source = new Uri("https://appassets.winuninstaller/index.html");
            WriteWebLog($"WebView2 initialized; browserVersion={core.Environment.BrowserVersionString}; wwwroot={wwwroot}");
        }
        catch (Exception ex)
        {
            if (_closed) return;
            WriteWebLog("WebView2 initialization failed: " + ex);
            _uiReadyTimer.Stop();
            ShowWebStartupError(UiStrings.Get(_main.LanguageCode, "webUiInitializeFailed") + " " + ex.Message);
        }
    }

    private void ShowWebStartupError(string message)
    {
        if (_closed) return;
        BrowserContainer.Visibility = Visibility.Collapsed;
        StartupErrorPanel.Visibility = Visibility.Visible;
        StartupErrorTitle.Text = UiStrings.Get(_main.LanguageCode, "webUiStartFailed");
        StartupErrorText.Text = message;
    }

    private async void OnBrowserProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_closed || _recoveringBrowser) return;
        if (e.ProcessFailedKind is not
            (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
        _uiReadyTimer.Stop();

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
            BrowserContainer.Visibility = Visibility.Visible;
            await InitializeBrowserAsync();
        }
        catch (Exception ex)
        {
            WriteWebLog("WebView2 recovery failed: " + ex);
            if (_closed) return;
            BrowserContainer.Visibility = Visibility.Collapsed;
            StartupErrorPanel.Visibility = Visibility.Visible;
            StartupErrorTitle.Text = UiStrings.Get(_main.LanguageCode, "webUiStartFailed");
            StartupErrorText.Text = UiStrings.Get(_main.LanguageCode, "webUiInitializeFailed");
        }
        finally { _recoveringBrowser = false; }
    }


    private string CurrentWebPage => _workspacePage switch
    {
        "BrowserExtensions" => "extensions",
        "ControlCenter" => "control",
        _ => _main.CurrentViewModel == _history ? "history" : "apps"
    };

    private void OpenLegacy_Click(object sender, RoutedEventArgs e)
    {
        _apps.UseWebUi = false; _main.UseWebUi = false;
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
        _stateTimer.Stop(); _uiReadyTimer.Stop(); _cpuTimer?.Stop();
        _main.PropertyChanged -= OnAnyPropertyChanged;
        _apps.PropertyChanged -= OnAppsPropertyChanged;
        _history.PropertyChanged -= OnHistoryPropertyChanged;
        DetachCollections();
        Browser.Dispose();
        base.OnClosed(e);
    }
}
