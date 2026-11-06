using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using WinUninstaller.Models;
using WinUninstaller.Services;
using WinUninstaller.ViewModels;
using WinUninstaller.Views;

internal static class NativeProtectionUiCheck
{
    internal static int Run(string evidenceRoot)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("This UI check must run without elevation.");

        var profile = Path.Combine(Path.GetFullPath(evidenceRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var exitCode = 2;
        var thread = new Thread(() =>
        {
            try { exitCode = RunWindow(profile); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(profile, "error.log"), ex.ToString()); Console.Error.WriteLine(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Console.WriteLine("Native UI evidence: " + profile);
        return exitCode;
    }

    private static int RunWindow(string profile)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Uninstaller77;component/Resources/Styles.xaml") });
        var database = new DatabaseService(Path.Combine(profile, "history.db"));
        database.AddRecord(new UninstallRecord
        {
            AppName = "Queue protection", Outcome = "QueueProtection", UninstallDate = "2026-11-05 05:18:53",
            Notes = """{"Restore":{"Status":"Failed","Reason":"Permission"},"Backups":[{"App":"Owned UI history fixture","Result":{"Status":"Succeeded"},"File":"C:\\OwnedFixture\\0001.reg"}]}"""
        });
        var extractor = new IconExtractionService();
        var residuals = new ResidualScanService();
        var apps = new AppListViewModel(new RegistryDiscoveryService(), new IconCacheService(extractor, Path.Combine(profile, "Icons")),
            database, new UninstallService(), residuals, new QueueJournal(Path.Combine(profile, "Queue")));
        var main = new MainViewModel(apps, new HistoryViewModel(database), residuals, profile);
        var window = new WebMainWindow(main, Path.Combine(profile, "WebView2")) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Top = 30, Left = 30 };
        var browserContainer = (Panel)window.FindName("BrowserContainer");
        WebView2 CurrentBrowser() => browserContainer.Children.OfType<WebView2>().Single();
        var checks = new List<JsonElement>();
        var startupFrames = new Dictionary<string, JsonElement>();
        var residualLanguages = new List<string>();
        application.Startup += async (_, _) =>
        {
            var exitCode = 1;
            try
            {
                window.Show();
                var readyBy = DateTime.UtcNow.AddSeconds(20);
                while (CurrentBrowser().CoreWebView2 is null && DateTime.UtcNow < readyBy) await Task.Delay(100);
                await Expect("!!document.querySelector('.app-shell')");
                await CurrentBrowser().CoreWebView2!.AddScriptToExecuteOnDocumentCreatedAsync("""
                    window.__startupFrames = [];
                    new MutationObserver(() => {
                      const shell = document.querySelector('.app-shell');
                      if (!shell) return;
                      const frame = document.documentElement.lang + ':' + shell.dir;
                      if (window.__startupFrames.at(-1) !== frame) window.__startupFrames.push(frame);
                    }).observe(document, { subtree: true, childList: true, attributes: true, attributeFilter: ['lang', 'dir'] });
                    """);
                foreach (var language in new[] { "en", "ar" })
                {
                    main.LanguageCode = language;
                    var direction = language == "ar" ? "rtl" : "ltr";
                    CurrentBrowser().CoreWebView2!.Reload();
                    await Expect($"window.__startupFrames?.length > 0 && document.documentElement.lang === '{language}' && document.querySelector('.app-shell')?.dir === '{direction}'");
                    using (var frames = JsonDocument.Parse(await CurrentBrowser().ExecuteScriptAsync("window.__startupFrames")))
                    {
                        startupFrames.Add(language, frames.RootElement.Clone());
                        var expected = $"{language}:{direction}";
                        if (frames.RootElement.EnumerateArray().Any(frame => frame.GetString() != expected))
                            throw new InvalidOperationException($"Expected only {expected} at startup, got {frames.RootElement}.");
                    }
                    await Click(".sidebar-bottom button:nth-of-type(2)");
                    await Expect("!!document.querySelector('.settings-modal .protection-option input')");
                    var otherLanguage = language == "en" ? "ar" : "en";
                    await Click(otherLanguage == "en" ? ".language-picker button:nth-of-type(1)" : ".language-picker button:nth-of-type(2)");
                    await Expect($"document.documentElement.lang === '{otherLanguage}'");
                    await Click(language == "en" ? ".language-picker button:nth-of-type(1)" : ".language-picker button:nth-of-type(2)");
                    await Expect($"document.documentElement.lang === '{language}' && document.querySelector('.app-shell')?.dir === '{direction}'");
                    foreach (var (width, height) in new[] { (1320, 820), (1000, 650) })
                    {
                        window.Width = width; window.Height = height;
                        await Task.Delay(250);
                        var setting = Literal(UiStrings.Get(language, "protectionSetting"));
                        var hint = Literal(UiStrings.Get(language, "protectionHint"));
                        await Expect($$"""
                            (() => { const option = document.querySelector('.protection-option');
                              const panel = document.querySelector('.settings-modal');
                              const bounds = panel.getBoundingClientRect();
                              const sidebar = document.querySelector('.sidebar').getBoundingClientRect();
                              const area = document.querySelector('.main-panel').getBoundingClientRect();
                              return option.textContent.includes({{setting}}) && panel.textContent.includes({{hint}}) && !option.querySelector('input').checked
                                && bounds.left >= 0 && bounds.top >= 0 && bounds.right <= innerWidth + 1 && bounds.bottom <= innerHeight + 1
                                && ('{{direction}}' === 'rtl' ? sidebar.left > area.left : sidebar.left < area.left); })()
                            """);
                        using var snapshot = JsonDocument.Parse(await CurrentBrowser().ExecuteScriptAsync($$"""
                            ({ language: document.documentElement.lang, direction: document.querySelector('.app-shell').dir,
                               requestedWidth: {{width}}, requestedHeight: {{height}}, viewport: [innerWidth, innerHeight],
                               option: document.querySelector('.protection-option').textContent.trim(),
                               hint: document.querySelector('.protection-option').parentElement.querySelector('p').textContent.trim() })
                            """));
                        checks.Add(snapshot.RootElement.Clone());
                    }

                    await Click(".settings-modal .modal-actions button.primary");
                    await Expect("!document.querySelector('.settings-modal')");
                    await Click(".sidebar-bottom button:nth-of-type(1)");
                    await Click(".history-row[data-outcome=QueueProtection] button");
                    var failure = Literal(UiStrings.Get(language, "protectionReasonPermission"));
                    await Expect($"document.querySelector('.protection-details')?.textContent.includes({failure}) && document.querySelector('.protection-evidence')?.textContent.includes('0001.reg')");
                    await Click(".settings-modal .modal-header button");
                    await Expect("!document.querySelector('.protection-details')");
                    await CheckResidualReview(language, direction);
                    residualLanguages.Add(language);
                }

                await Expect("!window.__winUninstallerDiagnostics?.errors?.length");
                File.WriteAllText(Path.Combine(profile, "result.json"), JsonSerializer.Serialize(new
                { passed = true, fixtureHistory = true, nativeWebView2 = CurrentBrowser().CoreWebView2!.Environment.BrowserVersionString,
                    assembly = typeof(WebMainWindow).Assembly.Location, historyDetailsLanguages = new[] { "en", "ar" },
                    residualLanguages, startupFrames, settings = checks },
                    new JsonSerializerOptions { WriteIndented = true }));
                exitCode = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(profile, "error.log"), ex.ToString());
                Console.Error.WriteLine(ex);
            }
            finally { window.Close(); application.Shutdown(exitCode); }
        };
        return application.Run();

        async Task CheckResidualReview(string language, string direction)
        {
            var id = "U77_NativeResidual_" + Guid.NewGuid().ToString("N");
            var root = Path.Combine(profile, id);
            Directory.CreateDirectory(root);
            var file = Path.Combine(root, "owned-leftover.bin");
            File.WriteAllText(file, "Owned fixture file");
            var fixture = new InstalledApp { Id = id, Name = id, Source = "Fixture", InstallLocation = root,
                RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + id };
            var complete = new TaskCompletionSource<UninstallService.UninstallResult>();
            var runQueue = typeof(AppListViewModel).GetMethod("RunQueueAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            Func<InstalledApp, Task<UninstallService.UninstallResult>> execute = _ => complete.Task;
            var queue = (Task)runQueue.Invoke(apps, [new List<InstalledApp> { fixture }, false, true, execute])!;
            try
            {
                await Expect("!!document.querySelector('.queue-workspace.is-compact [role=progressbar]') && !document.querySelector('.queue-disclosure').open");
                await Click(".queue-disclosure summary");
                await Expect("document.querySelector('.queue-disclosure')?.open === true");
            }
            finally
            {
                complete.TrySetResult(new(true, 0, VerificationStatus.VerifiedRemoved, "Owned fixture result", "", ExitObserved: true));
            }
            await queue;
            await Expect($$"""
                (() => {
                  const review = document.querySelector('.residual-review');
                  if (!review) return false;
                  const bounds = review.getBoundingClientRect();
                  return review.closest('[dir]')?.dir === '{{direction}}' && document.documentElement.lang === '{{language}}'
                    && bounds.top >= 0 && bounds.bottom <= innerHeight + 1 && !document.querySelector('.queue-workspace')
                    && review.querySelector('.residual-row-main bdi[dir=ltr]')?.textContent.endsWith('owned-leftover.bin')
                    && review.querySelector('.residual-row input').checked === false;
                })()
                """);
            await Click(".residual-toolbar button:first-child");
            await Expect("document.querySelector('.residual-row input')?.checked === true && !document.querySelector('.residual-danger').disabled");
            await Click(".residual-danger");
            await Expect("!!document.querySelector('.residual-confirmation')");
            if (!File.Exists(file)) throw new InvalidOperationException("Deletion preceded explicit confirmation.");
            if (language == "en")
            {
                await Click(".residual-review .modal-actions button.quiet");
                await Expect("!document.querySelector('.residual-confirmation')");
                await Click(".residual-review .modal-actions button.quiet");
                await Expect("!document.querySelector('.residual-review') && !!document.querySelector('.queue-workspace')");
                if (!File.Exists(file)) throw new InvalidOperationException("Skipping changed the fixture file.");
            }
            else
            {
                await Click(".residual-danger");
                await Expect("!document.querySelector('.residual-review') && !!document.querySelector('.queue-workspace')");
                if (File.Exists(file)) throw new InvalidOperationException("The confirmed reviewed fixture file was not deleted.");
            }
            await Click(".queue-header button");
            await Expect("!document.querySelector('.queue-workspace')");

            var absentId = id + "_Absent";
            var absent = new InstalledApp { Id = absentId, Name = absentId, Source = "Fixture",
                InstallLocation = Path.Combine(root, "already-gone"), RegistryKeyPath = fixture.RegistryKeyPath + "_Absent" };
            await (Task)runQueue.Invoke(apps, [new List<InstalledApp> { absent }, false, true, execute])!;
            await Expect("!!document.querySelector('.residual-scan-summary') && !document.querySelector('.residual-review, .residual-danger')");
            await Click(".residual-scan-summary summary");
            var missing = Literal(UiStrings.Get(language, "residualFolderMissing"));
            await Expect($"document.querySelector('.residual-scan-summary')?.open && document.querySelector('.residual-scan-summary-content')?.textContent.includes({missing})");
            await Click(".queue-header button");
            await Expect("!document.querySelector('.queue-workspace')");
        }

        static string Literal(string value) => JsonSerializer.Serialize(value);
        Task Click(string selector) => Expect($"(() => {{ const element = document.querySelector({Literal(selector)}); if (!element) return false; element.click(); return true; }})()");

        async Task Expect(string expression)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var activeBrowser = CurrentBrowser();
                try
                {
                    if (activeBrowser.CoreWebView2 is not null && await activeBrowser.ExecuteScriptAsync(expression) == "true") return;
                }
                catch (ObjectDisposedException) { /* Browser recovery replaced this instance; retry against the current child. */ }
                await Task.Delay(100);
            }
            throw new InvalidOperationException("The native WebView2 assertion did not pass: " + expression);
        }
    }
}
