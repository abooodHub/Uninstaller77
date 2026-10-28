using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using WinUninstaller.Models;
using WinUninstaller.Services;
using WinUninstaller.ViewModels;

namespace WinUninstaller;

public partial class App : Application
{
    private Mutex? _desktopMutex;
    // Every process loading the payload (including shell and test launches) must
    // prevent replacement. Let Windows close this handle on process termination,
    // after WPF/WebView2 and their mapped assemblies have finished tearing down.
    private Mutex? _installerLifetimeMutex;
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    public App()
    {
        DispatcherUnhandledException += (_, args) => LogCrash("DispatcherUnhandledException", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString() ?? "Unknown fatal exception"));
        TaskScheduler.UnobservedTaskException += (_, args) => LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
        Exit += (_, args) => LogRuntime($"Exit code={args.ApplicationExitCode}; Environment.ExitCode={Environment.ExitCode}");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        _installerLifetimeMutex = new Mutex(false, @"Local\Uninstaller77.InUse");
        LogStartup("OnStartup entered");
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && args[1] == UninstallBroker.Switch)
        {
            // Startup owns the WPF dispatcher. Run the headless pipe/job worker
            // off that dispatcher so its awaits cannot deadlock OnStartup.
            try { Shutdown(Task.Run(() => UninstallBroker.RunAsync(args[1..])).GetAwaiter().GetResult()); }
            catch (Exception ex) { LogCrash("UninstallBroker", ex); Shutdown(2); }
            return;
        }
        var shellUninstallShortcut = args.Length == 3 && args[1] == "--shell-uninstall";
        if (args.Contains("--require-vm"))
        {
            using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var manufacturer = bios?.GetValue("SystemManufacturer")?.ToString() ?? "";
            if (!manufacturer.Contains("VMware", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("VM-only tests were not started. / لم تبدأ اختبارات الوهمي: هذه ليست بيئة VMware.", "Uninstaller 77 Tests");
                Shutdown(2); return;
            }
        }
        if (args.Contains("--collect-diagnostics"))
        {
            try
            {
                var result = Task.Run(DiagnosticExportService.CollectAsync).GetAwaiter().GetResult();
                MessageBox.Show("Read-only local diagnostics; no removal was started.\n\n" + result, "Uninstaller 77 Diagnostics");
                Shutdown(0);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Uninstaller 77 Diagnostics"); Shutdown(1); }
            return;
        }
        if (args.Any(arg => arg is "--run-tests" or "--run-phase10-unit-tests" || arg.StartsWith("--fixture-", StringComparison.Ordinal)))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            Console.Error.WriteLine("Tests and fixtures moved to tests/Engine.Tests. No test or uninstall was started.");
            Shutdown(2); return;
        }
        var testRoot = args.Contains("--ui-attention-test") || args.Contains("--ui-phase11-test") || args.Contains("--ui-phase13-test")
            ? Path.Combine(Path.GetTempPath(), "WinUninstallerTests_" + Guid.NewGuid().ToString("N")) : null;

        var registry = new RegistryDiscoveryService();
        var extractor = new IconExtractionService();
        var iconCache = new IconCacheService(extractor,
            testRoot == null ? null : Path.Combine(testRoot, "IconCache"));
        var db = new DatabaseService(testRoot == null ? null : Path.Combine(testRoot, "tests.db"));
        var uninstaller = new UninstallService();
        var residualScanner = new ResidualScanService();

        // 2. Check for CLI diagnostic mode: --diag
        if (args.Contains("--diag"))
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            RunDiagnostics(registry, extractor, iconCache, db);
            Shutdown(0);
            return;
        }

        if (args.Contains("--icon-audit"))
        {
            RunIconAudit(registry, iconCache);
            Shutdown(0);
            return;
        }

        var isolatedUiTest = args.Contains("--ui-phase13-test");
        if (!shellUninstallShortcut)
        {
            _desktopMutex = new Mutex(true, isolatedUiTest ? $@"Local\Uninstaller77.Phase13.{Environment.ProcessId}" : @"Local\Uninstaller77.Desktop", out var firstDesktopInstance);
            if (!firstDesktopInstance)
            {
                MessageBox.Show("Uninstaller 77 is already open. / Uninstaller 77 مفتوح بالفعل.", "Uninstaller 77");
                Shutdown(0); return;
            }
            Exit += (_, _) => { _desktopMutex.ReleaseMutex(); _desktopMutex.Dispose(); };
        }
        QueueJournal? fixtureJournal = null;
        if (args.Contains("--ui-attention-test") || args.Contains("--ui-phase11-test"))
            fixtureJournal = new QueueJournal(Path.Combine(testRoot!, "Queue"));
        if (args.Contains("--ui-phase11-test"))
        {
            var recorded = DateTimeOffset.UtcNow.AddHours(-2);
            var fixture = new InstalledApp { Id = "Phase11_Old_Operation", Name = "Phase11 review fixture", Source = "Fixture", RegistryHive = "HKCU",
                RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Phase11_" + Guid.NewGuid().ToString("N") };
            fixtureJournal!.Save(new("phase11-isolated-review", recorded, true, false,
                [new("phase11-operation-1", fixture, UninstallOperationState.AwaitingCompletion, recorded, null, "Recorded fixture evidence: completion was not established.", -3),
                 new("phase11-operation-2", new InstalledApp { Id = "Phase11_Not_Started", Name = "Phase11 waiting fixture", Source = "Fixture" }, UninstallOperationState.Idle, null, null)]));
        }
        var appListVm = new AppListViewModel(registry, iconCache, db, uninstaller, residualScanner, fixtureJournal);
        var historyVm = new HistoryViewModel(db);
        var mainVm = new MainViewModel(appListVm, historyVm, residualScanner);
        if (args.Contains("--ui-preview-queue-ar") || args.Contains("--ui-preview-grid") || args.Contains("--ui-preview-select") || args.Contains("--ui-preview-details"))
            mainVm.LanguageCode = "ar";
        else if (args.Contains("--ui-preview-english"))
            mainVm.LanguageCode = "en";
        else if (args.Contains("--ui-preview-reduce-motion"))
        {
            mainVm.LanguageCode = "ar";
            mainVm.ReduceMotion = true;
        }

        // Keep the WPF/XAML UI available with --legacy-ui until the WebView2 migration has
        // passed compatibility checks. The HTML host is the normal application entry point.
        Window window = args.Contains("--legacy-ui")
            ? new Views.MainWindow { DataContext = mainVm }
            : new Views.WebMainWindow(mainVm);
        MainWindow = window;
        // Reproducible visual QA uses the real desktop UI and real discovery data.
        // Only the three documented window sizes are accepted; no privileged action is enabled.
        var visualSize = args.FirstOrDefault(a => a.StartsWith("--visual-size=", StringComparison.Ordinal));
        if (visualSize is not null && visualSize[14..] is "1920x1080" or "1366x768" or "1280x800")
        {
            var dimensions = visualSize[14..].Split('x');
            window.Width = int.Parse(dimensions[0]);
            window.Height = int.Parse(dimensions[1]);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
        }
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        ConfigureWindowLifetime(window, appListVm);

        window.Show();
        LogRuntime($"MainWindow shown; type={window.GetType().FullName}; handle={new System.Windows.Interop.WindowInteropHelper(window).Handle}; process={Environment.ProcessPath}");

        // Explicit fixture mode contains no executable uninstall commands.
        if (args.Contains("--ui-attention-test"))
            _ = appListVm.ShowAttentionFixtureAsync();
        else if (args.Contains("--ui-preview-queue") || args.Contains("--ui-preview-queue-ar"))
            appListVm.ShowSyntheticQueuePreview();
        else
        {
            if (shellUninstallShortcut)
                _ = appListVm.PromptUninstallFromShortcutAsync(args[2]);
            else
                _ = appListVm.LoadAppsAsync();

            if (args.Contains("--ui-preview-select"))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1200);
                    await window.Dispatcher.InvokeAsync(() => appListVm.SelectAllVisible(true));
                });
            }
            else if (args.Contains("--ui-preview-details"))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1200);
                    await window.Dispatcher.InvokeAsync(() =>
                    {
                        var first = appListVm.Apps.FirstOrDefault();
                        if (first != null) appListVm.OpenDetails(first);
                    });
                });
            }
        }
    }

    internal static void ConfigureWindowLifetime(Window window, AppListViewModel appListVm)
    {
        window.Closing += (_, eventArgs) =>
        {
            if (Current.MainWindow != window) return;
            var uninstallRunning = appListVm.IsProgressOpen && (!appListVm.IsBatchQueue || appListVm.IsBatchRunning);
            if (!uninstallRunning && !appListVm.IsCleaningResiduals && !appListVm.IsUnregisteredBusy) return;
            eventArgs.Cancel = true;
            const string closeGuard = "An uninstall or cleanup operation is still running. Wait for its result before closing.";
            if (window is Views.WebMainWindow) appListVm.SetWebNotice(closeGuard);
            else MessageBox.Show(closeGuard, "Operation in progress", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        window.Closed += (_, _) => { if (Current.MainWindow == window) appListVm.CancelBackgroundWork(); };

    }

    private static void LogCrash(string category, Exception exception) => LogRuntime($"{category}{Environment.NewLine}{exception}");

    private static void LogStartup(string phase)
    {
        string identity;
        try { identity = System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
        catch (Exception ex) { identity = $"<unavailable: {ex.GetType().Name}: {ex.Message}>"; }

        LogRuntime($"{phase}{Environment.NewLine}" +
            $"PID={Environment.ProcessId}; ProcessPath={Environment.ProcessPath ?? "<null>"}; " +
            $"CurrentDirectory={Environment.CurrentDirectory}; BaseDirectory={AppContext.BaseDirectory}; " +
            $"Runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; " +
            $"Architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; " +
            $"64BitProcess={Environment.Is64BitProcess}; User={identity}; " +
            $"CommandLine={Environment.CommandLine}");
    }

    private static void LogRuntime(string message)
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinUninstaller", "Logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "runtime.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* diagnostics must never mask the original startup failure */ }
    }

    private static void RunDiagnostics(
        RegistryDiscoveryService registry,
        IconExtractionService extractor,
        IconCacheService iconCache,
        DatabaseService db)
    {
        var output = new StringWriter();
        output.WriteLine("=== WinUninstaller Diagnostics ===");
        output.WriteLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // 1. Registry discovery
        output.WriteLine("\n[1] Discovering installed applications...");
        var apps = registry.DiscoverApps(includeMsix: true);
        output.WriteLine($"Found {apps.Count} applications in Registry and Store.");

        // 2. Icon extraction test
        output.WriteLine("\n[2] Checking image availability on sample applications (including generated avatars)...");
        int successIcons = 0;
        int totalTested = Math.Min(30, apps.Count);
        for (int i = 0; i < totalTested; i++)
        {
            var app = apps[i];
            var icon = iconCache.GetIcon(app);
            bool hasIcon = icon != null;
            if (hasIcon) successIcons++;

            var iconStatus = hasIcon ? "[IMAGE]" : "[MISSING]";
            output.WriteLine($"  {iconStatus,-10} {app.Name} (Publisher: {app.Publisher})");
        }
        output.WriteLine($"\nImage availability: {successIcons}/{totalTested} ({(totalTested == 0 ? 0 : (double)successIcons / totalTested * 100):F1}%). Use --icon-audit for actual source classification.");

        // 3. Read-only database diagnostic
        output.WriteLine("\n[3] Reading SQLite history...");
        output.WriteLine($"History records in SQLite: {db.GetHistory().Count}.");

        // 4. Cache directory check
        output.WriteLine($"\n[4] Icon Cache Directory: {iconCache.CacheDir}");
        var cachedFiles = Directory.GetFiles(iconCache.CacheDir, "*.png");
        output.WriteLine($"PNG files cached to disk: {cachedFiles.Length}");

        output.WriteLine("\n=== Diagnostics Completed Successfully ===");

        var resultText = output.ToString();
        Console.WriteLine(resultText);

        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Reports");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "diagnostics_log.txt");
        File.WriteAllText(logPath, resultText);
    }

    private static void RunIconAudit(RegistryDiscoveryService registry, IconCacheService cache)
    {
        var reportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUninstaller", "Reports");
        Directory.CreateDirectory(reportDir);
        var apps = registry.DiscoverApps(includeMsix: true);
        var rows = new List<(InstalledApp App, string Origin, int Width, int Height, string Hash)>();
        foreach (var app in apps)
        {
            try
            {
                var result = cache.AuditIcon(app);
                rows.Add((app, result.Origin, result.Width, result.Height, result.Hash));
            }
            catch { rows.Add((app, "Failed", 0, 0, string.Empty)); }
        }
        var csvPath = Path.Combine(reportDir, "icon_audit.csv");
        static string Cell(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        var csv = new System.Text.StringBuilder("App,Publisher,Source,Origin,Width,Height,Hash\r\n");
        foreach (var row in rows)
            csv.AppendLine(string.Join(",", Cell(row.App.Name), Cell(row.App.Publisher), Cell(row.App.Source),
                Cell(row.Origin), row.Width, row.Height, Cell(row.Hash)));
        File.WriteAllText(csvPath, csv.ToString());
        var duplicates = rows.Where(r => r.Hash.Length > 0)
            .GroupBy(r => r.Hash).Where(g => g.Count() > 1).ToList();
        var summary = $"Total: {rows.Count}\nOriginal candidates: {rows.Count(r => r.Origin is not ("Generated avatar" or "Failed"))}\n" +
            $"Fallback avatars: {rows.Count(r => r.Origin == "Generated avatar")}\nFailed: {rows.Count(r => r.Origin == "Failed")}\n" +
            $"Below 64 px: {rows.Count(r => r.Width is > 0 and < 64)}\nDuplicate pixel groups (review): {duplicates.Count}\n" +
            $"CSV: {csvPath}\n";
        File.WriteAllText(Path.Combine(reportDir, "icon_audit_summary.txt"), summary);
    }

}
