using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WinUninstaller.Models;
using WinUninstaller.Services;

namespace WinUninstaller.Views;

public partial class WebMainWindow
{
    private async Task RunPhase11QaAsync()
    {
        if (_visualQaStarted) return;
        _visualQaStarted = true;
        const string root = @"C:\Uninstaller\reports\PHASE11";
        var images = Path.Combine(root, "SCREENSHOTS"); Directory.CreateDirectory(images);
        var checks = new List<object>(); var originalLanguage = _main.LanguageCode;
        try
        {
            Width = 1366; Height = 768; Left = 0; Top = 0;
            await WaitUntil(() => _apps.HasConfirmedInventory && !_apps.IsLoading);
            if (_apps.Apps.Any(app => app.Source == "Logged")) throw new InvalidOperationException("Historical record appeared as installed.");
            foreach (var size in new[] { (1366, 768), (1280, 800), (1920, 1080) })
            foreach (var language in new[] { "en", "ar" })
            {
                Width = size.Item1; Height = size.Item2;
                _main.LanguageCode = language; ScheduleState(); await Task.Delay(300);
                await WaitForUi("apps", language);
                var disabled = await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-testid=queue-acknowledge]')?.disabled === true");
                if (disabled != "true") throw new InvalidOperationException("Review did not require explicit consent.");
                var controlsVisible = await Browser.CoreWebView2.ExecuteScriptAsync("['queue-acknowledge','queue-review-stop','queue-retry-verification'].every(id=>{const b=document.querySelector('[data-testid='+id+']');if(!b)return false;const r=b.getBoundingClientRect();return r.top>=0&&r.left>=0&&r.bottom<=innerHeight&&r.right<=innerWidth;})");
                if (controlsVisible != "true") throw new InvalidOperationException("A review control was outside the viewport.");
                await Capture("unresolved-review-" + language + (size.Item1 == 1366 ? "" : $"-{size.Item1}x{size.Item2}"));
            }
            Width = 1366; Height = 768;
            ScheduleState(); await Task.Delay(250); await WaitForUi("apps", "ar");
            await Click("queue-retry-verification");
            await WaitUntil(() => !_apps.VerificationRetrying && _apps.ReviewSnapshot?.Reviews?.LastOrDefault()?.Action == "ReadOnlyVerification");
            if (!_apps.NeedsQueueReview || _apps.BatchSuccessful != 0) throw new InvalidOperationException("Read-only verification became uninstall success.");
            await Capture("read-only-review-ar");
            await Click("queue-review-stop"); await WaitUntil(() => !_apps.IsProgressOpen);
            if (!_apps.HasPendingQueueReview) throw new InvalidOperationException("Keeping pending discarded the safety barrier.");
            await Click("open-queue-review"); await WaitUntil(() => _apps.IsProgressOpen);
            await Click("review-ack-consent"); await Click("queue-acknowledge");
            await WaitUntil(() => !_apps.IsProgressOpen);
            if (_apps.NeedsQueueReview || _apps.IsBatchRunning || _apps.BatchSuccessful != 0 ||
                _apps.QueueItems.Any(item => item.State == UninstallOperationState.Completed) || _apps.QueueItems[1].StartedAt != null)
                throw new InvalidOperationException("Acknowledgment changed an outcome or started the waiting item.");
            File.WriteAllText(Path.Combine(root, "acknowledged-fixture-evidence.json"), JsonSerializer.Serialize(_apps.ReviewSnapshot, new JsonSerializerOptions { WriteIndented = true }));

            foreach (var state in Enum.GetValues<ResidualScanState>())
                _apps.Database.AddRecord(new() { AppName = "Phase11 scan " + state, InstallerType = state == ResidualScanState.Unsupported ? "MSIX / Store" : "Fixture", Success = false, Outcome = "NotStarted",
                    ResidualStatus = new ResidualScanResult(state, []).StorageStatus, Notes = "Explicit isolated presentation fixture. No uninstaller was launched." });
            _apps.Database.AddRecord(new() { AppName = "Phase11 unresolved outcome", Outcome = "NeedsAttention", Success = false, ExitCode = -3,
                ResidualStatus = "ResidualScan:NotScanned", Notes = "Explicit isolated history fixture; historical completion is unknown. No uninstaller was launched." });
            _history.ShowTestRecords = true;
            foreach (var language in new[] { "en", "ar" })
            {
                _main.LanguageCode = language;
                foreach (var page in new[] { "AllApps", "BrowserExtensions", "WindowsApps", "ControlCenter", "History", "Settings" })
                {
                    _main.IsSettingsOpen = false;
                    if (page == "BrowserExtensions") { _workspacePage = page; if (!_extensionsScanned) await ScanExtensionsAsync(); }
                    else if (page == "ControlCenter") _workspacePage = page;
                    else { _workspacePage = null; _main.NavigateTo(page == "Settings" ? "AllApps" : page); _main.IsSettingsOpen = page == "Settings"; }
                    ScheduleState(); await Task.Delay(250);
                    await WaitForUi(page switch { "BrowserExtensions" => "extensions", "ControlCenter" => "control", "History" => "history", _ => "apps" }, language, page == "Settings");
                    if (page == "History")
                    {
                        var expected = JsonSerializer.Serialize(UiStrings.Get(language, "residualScanUnsupported"));
                        var correct = await Browser.CoreWebView2.ExecuteScriptAsync($"document.body.innerText.includes({expected})");
                        if (correct != "true") throw new InvalidOperationException("Unsupported residual scan text was missing from History.");
                        var unresolved = await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-outcome=NeedsAttention] .text-unresolved') !== null");
                        if (unresolved != "true") throw new InvalidOperationException("Unresolved history was rendered as success or failure.");
                    }
                    await Capture(page.ToLowerInvariant() + "-" + language);
                }
            }
            File.WriteAllText(Path.Combine(root, "functional-smoke.json"), JsonSerializer.Serialize(new
            { passed = true, scope = "Isolated queue/history database; real read-only inventory. No uninstaller or dangerous tool was launched.",
                acknowledgedUnknown = _apps.ReviewSnapshot?.AcknowledgedUnresolvedAt != null, successful = _apps.BatchSuccessful,
                waitingItemStarted = _apps.QueueItems[1].StartedAt != null, screenshots = checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        { File.WriteAllText(Path.Combine(root, "functional-smoke.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), screenshots = checks }, new JsonSerializerOptions { WriteIndented = true })); }
        finally
        {
            _main.IsSettingsOpen = false; _main.LanguageCode = originalLanguage;
            _workspacePage = null; _main.NavigateTo("AllApps"); ScheduleState();
        }

        async Task Click(string testId)
        {
            ScheduleState(); await Task.Delay(150);
            var result = await Browser.CoreWebView2.ExecuteScriptAsync($"(() => {{ const b=document.querySelector('[data-testid={testId}]'); if(!b || b.disabled) return false; b.click(); return true; }})()");
            if (result != "true") throw new InvalidOperationException("Required control unavailable: " + testId);
        }
        static async Task WaitUntil(Func<bool> condition)
        { for (var attempt = 0; attempt < 200 && !condition(); attempt++) await Task.Delay(100); if (!condition()) throw new TimeoutException("Phase11 smoke condition timed out."); }
        async Task WaitForUi(string page, string language, bool settings = false)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var ready = await Browser.CoreWebView2.ExecuteScriptAsync($"document.documentElement.lang === '{language}' && document.querySelector('.app-shell')?.dataset.page === '{page}' && Boolean(document.querySelector('.settings-modal')) === {(settings ? "true" : "false")}");
                if (ready == "true") { await Task.Delay(120); return; }
                await Task.Delay(100);
            }
            throw new TimeoutException($"Rendered UI did not reach {page}/{language}/settings={settings}.");
        }
        async Task Capture(string name)
        {
            using var stream = File.Create(Path.Combine(images, name + ".png"));
            await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            var layout = await Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({page:document.querySelector('.app-shell')?.dataset.page,language:document.documentElement.lang,direction:document.documentElement.dir,viewport:[innerWidth,innerHeight],sidebarLeft:document.querySelector('.sidebar')?.getBoundingClientRect().left,sidebarRight:document.querySelector('.sidebar')?.getBoundingClientRect().right,errors:window.__winUninstallerDiagnostics?.errors||[]})");
            checks.Add(new { name, layout });
        }
    }
}
