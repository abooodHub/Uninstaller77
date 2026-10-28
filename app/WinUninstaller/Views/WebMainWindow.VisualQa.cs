using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace WinUninstaller.Views;

public partial class WebMainWindow
{
    private bool _visualQaStarted;
    private async Task RunVisualQaAsync()
    {
        if (Environment.GetCommandLineArgs().Contains("--ui-phase11-test"))
        { await RunPhase11QaAsync(); return; }
        if (Environment.GetCommandLineArgs().Contains("--ui-attention-test"))
        { await RunAttentionQaAsync(); return; }
        if (_visualQaStarted || !Environment.GetCommandLineArgs().Contains("--visual-qa")) return;
        _visualQaStarted = true;
        var audit = Environment.GetCommandLineArgs().Contains("--audit-qa");
        var reportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "VisualQA", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId);
        var folder = Path.Combine(reportRoot, "SCREENSHOTS");
        Directory.CreateDirectory(folder);
        var checks = new List<object>();
        var originalLanguage = _main.LanguageCode;
        var originalMotion = _main.ReduceMotion;
        try
        {
            await Task.Delay(200);
            if (_apps.IsLoading && !_apps.HasConfirmedInventory) await Capture("loading-initial");
            for (var attempt = 0; attempt < 150 && _apps.IsLoading; attempt++) await Task.Delay(100);
            if (!_apps.HasConfirmedInventory) throw new InvalidOperationException("Visual QA inventory failed; no synthetic inventory substituted.");
            foreach (var size in new[] { (1920,1080), (1366,768), (1280,800) })
            foreach (var language in new[] { "en", "ar" })
            {
                WindowState = WindowState.Normal; Width = size.Item1; Height = size.Item2; Left = 0; Top = 0;
                _main.LanguageCode = language; _workspacePage = null; _main.NavigateTo("AllApps");
                _apps.SearchText = ""; _apps.SelectAllVisible(false);
                ScheduleState(); await Task.Delay(450);
                var suffix = $"{language}-{size.Item1}x{size.Item2}";
                await Capture("programs-list-" + suffix);
                foreach (var app in _apps.Apps.Take(3)) app.IsBatchSelected = true;
                ScheduleState(); await Task.Delay(150);
                await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.bulk-bar-top .button.primary')?.click()");
                await Task.Delay(350);
                await Capture("confirmation-" + suffix);
                // Close the confirmation only. Never click its destructive primary action.
                await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.premium-confirm .modal-actions .button.quiet')?.click()");
                _apps.SelectAllVisible(false);
                _main.IsSettingsOpen = true; ScheduleState(); await Task.Delay(250);
                await Capture("settings-" + suffix);
                _main.IsSettingsOpen = false;
                if (audit || size.Item1 == 1920)
                {
                    foreach (var page in new[] { "BrowserExtensions", "WindowsApps", "ControlCenter", "History" })
                    {
                        if (page == "BrowserExtensions") { _workspacePage = page; if (!_extensionsScanned) await ScanExtensionsAsync(); }
                        else if (page == "ControlCenter") _workspacePage = page;
                        else { _workspacePage = null; _main.NavigateTo(page); }
                        ScheduleState(); await Task.Delay(300);
                        await Capture(page.ToLowerInvariant() + "-" + suffix);
                    }
                }
            }
            _main.LanguageCode = "en"; _workspacePage = null; _main.NavigateTo("AllApps");
            _apps.SearchText = "7-Zip"; ScheduleState(); await Task.Delay(300);
            await Capture("search-en");
            _apps.SearchText = ""; _main.ReduceMotion = false;
            Width = 1366; Height = 768; ScheduleState(); await Task.Delay(300);
            _performanceAction = "Scroll";
            for (var step = 0; step < 10; step++)
            { await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.virtual-scroll')?.scrollBy(0,220)"); await Task.Delay(250); }
            _performanceAction = "Sort";
            for (var step = 0; step < 8; step++) { _apps.SetSort(step % 2 == 0 ? "NameDesc" : "NameAsc"); await Task.Delay(350); }
            _performanceAction = "Search";
            foreach (var term in new[] { "7", "7-Z", "7-Zip", "Microsoft", "Visual", "" }) { _apps.SearchText = term; await Task.Delay(400); }
            // Hover still needs actual pointer input; CSS :hover cannot be proven by a DOM class.
            _performanceAction = "Idle";
            await Capture("performance-ready-for-pointer");
            _performanceAction = "Idle";
            _apps.ShowSyntheticQueuePreview(); ScheduleState(); await Task.Delay(350);
            await Capture("progress-explicit-synthetic-preview-en");
            _main.LanguageCode = "ar"; ScheduleState(); await Task.Delay(250);
            await Capture("progress-explicit-synthetic-preview-ar");
            _apps.CloseProgress();
        }
        catch (Exception ex) { checks.Add(new { failure = ex.ToString() }); WriteWebLog("Visual QA failed: " + ex); }
        finally
        {
            _apps.SearchText = ""; _apps.SelectAllVisible(false);
            _main.IsSettingsOpen = false; _main.LanguageCode = originalLanguage; _main.ReduceMotion = originalMotion;
            _workspacePage = null; _main.NavigateTo("AllApps"); ScheduleState();
            File.WriteAllText(Path.Combine(reportRoot, "visual-matrix.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }

        async Task Capture(string name)
        {
            using (var stream = File.Create(Path.Combine(folder, name + ".png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            var layout = await Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({language:document.documentElement.lang,direction:document.documentElement.dir,viewport:[innerWidth,innerHeight],horizontalOverflow:document.body.scrollWidth>innerWidth,sidebarLeft:document.querySelector('.sidebar')?.getBoundingClientRect().left,sidebarRight:document.querySelector('.sidebar')?.getBoundingClientRect().right,brandIconLoaded:(()=>{let i=document.querySelector('.brand-mark img');return !!i?.complete && i.naturalWidth>0})(),dialog:document.querySelector('[role=dialog]')?.getAttribute('aria-label')||null,errors:window.__winUninstallerDiagnostics?.errors||[]})");
            checks.Add(new { name, configuredWidth = Width, configuredHeight = Height, browserWidth = Browser.ActualWidth,
                browserHeight = Browser.ActualHeight, layout });
        }
    }

    private async Task RunAttentionQaAsync()
    {
        if (_visualQaStarted) return;
        _visualQaStarted = true;
        var root = @"C:\Uninstaller\reports\VM_UNINSTALL_FIX";
        Directory.CreateDirectory(root);
        try
        {
            for (var attempt = 0; attempt < 100 && !_apps.NeedsQueueReview; attempt++) await Task.Delay(100);
            ScheduleState(); await Task.Delay(500);
            var layouts = new List<object>();
            var originalLanguage = _main.LanguageCode;
            foreach (var size in new[] { (1920, 1080), (1366, 768), (1280, 800) })
            foreach (var language in new[] { "en", "ar" })
            {
                Width = size.Item1; Height = size.Item2; Left = 0; Top = 0;
                _main.LanguageCode = language; ScheduleState(); await Task.Delay(250);
                var visible = await Browser.CoreWebView2.ExecuteScriptAsync("(() => {let b=document.querySelector('[data-testid=queue-review-stop]');if(!b)return false;let r=b.getBoundingClientRect();return r.top>=0&&r.bottom<=innerHeight&&b.contains(document.elementFromPoint(r.left+r.width/2,r.top+r.height/2));})()");
                layouts.Add(new { language, width = size.Item1, height = size.Item2, stopButtonVisible = visible == "true" });
                using var capture = File.Create(Path.Combine(root, $"attention-{language}-{size.Item1}x{size.Item2}.png"));
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
                if (visible != "true") throw new InvalidOperationException($"The review/stop button is obscured at {size} / {language}.");
            }
            File.WriteAllText(Path.Combine(root, "attention-layouts.json"), JsonSerializer.Serialize(layouts, new JsonSerializerOptions { WriteIndented = true }));
            _main.LanguageCode = originalLanguage; ScheduleState(); await Task.Delay(150);
            using (var image = File.Create(Path.Combine(root, "attention-before-stop.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            var clicked = await Browser.CoreWebView2.ExecuteScriptAsync("(() => { const button=document.querySelector('[data-testid=queue-review-stop]'); if (!button) return false; button.click(); return true; })()");
            if (clicked != "true") throw new InvalidOperationException("The rendered review/stop button was missing.");
            for (var attempt = 0; attempt < 100 && _apps.IsBatchRunning; attempt++) await Task.Delay(100);
            if (_apps.IsBatchRunning || _apps.IsProgressOpen || _apps.BatchSkipped != 2 || _apps.BatchSuccessful != 0)
                throw new InvalidOperationException($"Review stop did not complete: running={_apps.IsBatchRunning}, open={_apps.IsProgressOpen}, skipped={_apps.BatchSkipped}.");
            await Task.Delay(350);
            using (var image = File.Create(Path.Combine(root, "attention-after-stop.png")))
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            File.WriteAllText(Path.Combine(root, "webview-stop-test.json"), JsonSerializer.Serialize(new
            { passed = true, scope = "Isolated command-free UI fixture; no uninstaller launched", _apps.IsBatchRunning,
                _apps.IsProgressOpen, _apps.BatchSkipped, _apps.BatchSuccessful, stopRequested = _apps.StopBatchAfterCurrent }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(root, "webview-stop-test.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
        }
    }
}
