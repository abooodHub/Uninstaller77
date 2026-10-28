using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunBrowserHandoffTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        var browser = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
        }.FirstOrDefault(File.Exists);
        await RunTest("Browser handoff policy rejects installer arguments, target browser and forged browser identities", results, logger, () =>
        {
            var identity = new PostUninstallBrowserHandoff.BrowserIdentity("msedge.exe", "msedge.exe", "Microsoft Edge", "Microsoft Corporation");
            var context = new BrowserHandoffContext(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Owned", "Owned editor", @"C:\Owned\Editor", @"C:\Owned\Editor\uninstall.exe");
            if (!PostUninstallBrowserHandoff.AllowedDescription(identity, @"C:\Browser\msedge.exe", "msedge.exe https://example.invalid/", context)) throw new Exception("External browser URL rejected.");
            foreach (var arguments in new[] { "msedge.exe --uninstall https://example.invalid/", "msedge.exe --setup https://example.invalid/", "msedge.exe --renderer-cmd-prefix=cmd https://example.invalid/", "msedge.exe" })
                if (PostUninstallBrowserHandoff.AllowedDescription(identity, @"C:\Browser\msedge.exe", arguments, context)) throw new Exception("Installer or ambiguous browser command was accepted.");
            if (PostUninstallBrowserHandoff.AllowedDescription(identity with { Signer = "Untrusted fixture" }, @"C:\Browser\msedge.exe", "msedge.exe https://example.invalid/", context) ||
                PostUninstallBrowserHandoff.AllowedDescription(identity with { OriginalName = "setup.exe" }, @"C:\Browser\msedge.exe", "msedge.exe https://example.invalid/", context) ||
                PostUninstallBrowserHandoff.AllowedDescription(identity, @"C:\Owned\Editor\msedge.exe", "msedge.exe https://example.invalid/", context) ||
                PostUninstallBrowserHandoff.AllowedDescription(identity, @"C:\Browser\msedge.exe", "msedge.exe https://example.invalid/", context with { TargetName = "Microsoft Edge" }))
                throw new Exception("Spoofed or target application browser was ignored.");
            return "Only an external signed browser role can qualify; names alone, installer flags, empty arguments and the target's own processes cannot qualify.";
        });
        await RunTest("Browser handoff validates real installed browser signature and rejects an unsigned renamed fixture", results, logger, () =>
        {
            if (browser is null) throw new Exception("No installed Edge executable is available for this real-browser check; not marked passed.");
            var identity = PostUninstallBrowserHandoff.ReadTrustedIdentity(browser);
            if (identity is null || identity.Signer != "Microsoft Corporation") throw new Exception("Offline Windows signature validation failed for the installed browser.");
            var owned = Path.Combine(Path.GetTempPath(), "Uninstaller77_UnsignedBrowser_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(owned);
            var fake = Path.Combine(owned, "msedge.exe"); File.Copy(Environment.ProcessPath!, fake);
            if (PostUninstallBrowserHandoff.ReadTrustedIdentity(fake) is not null) throw new Exception("An unsigned test executable renamed as a browser was trusted.");
            return "Actual Microsoft Edge Authenticode and original product identity verified locally; unsigned renamed test EXE refused. No existing browser profile opened.";
        });
        foreach (var brokered in new[] { false, true })
            await RunTest("Real isolated browser may outlive a verified owned uninstall " + (brokered ? "through authenticated worker" : "through direct Windows job"), results, logger, async () =>
            {
                var fixture = CreateBrowserFixture(browser, remove: true, worker: false);
                Process? ownedBrowser = null;
                try
                {
                    if (brokered)
                    {
                        using var session = await UninstallBroker.StartAsync(fixture.App.RegistryKeyPath, false, Environment.ProcessPath!, "--fixture-browser-handoff " + fixture.Id, elevate: false);
                        using var worker = session.Process;
                        ownedBrowser = await PinFixtureBrowser(fixture.Root);
                        if (!await session.WaitAsync(TimeSpan.FromSeconds(20)) || session.ExitCode != 0 || session.TreeComplete || !session.CompletionVerified || session.CompletionKind != "ExternalBrowser" || ownedBrowser.HasExited)
                            throw new Exception("Worker did not retain the exact root exit and distinct external-browser completion evidence: " + session.Error);
                    }
                    else
                    {
                        using var execution = UninstallerProcess.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fixture-browser-handoff " + fixture.Id) { UseShellExecute = false, CreateNoWindow = true },
                            new(fixture.App.RegistryKeyPath, fixture.App.Name, fixture.Root, Environment.ProcessPath!));
                        ownedBrowser = await PinFixtureBrowser(fixture.Root);
                        if (!await execution.WaitAsync(TimeSpan.FromSeconds(20), null) || execution.ObservedExitCode != 0 || execution.IsTreeComplete || !execution.IsRemovalBoundaryComplete || execution.BrowserHandoffProcessIds.Length == 0 || ownedBrowser.HasExited)
                            throw new Exception("Real browser kept the completed root stuck, or was killed/misreported as an empty job.");
                    }
                    using var key = Registry.CurrentUser.OpenSubKey(fixture.App.RegistryKeyPath[5..]);
                    if (key is not null) throw new Exception("Owned uninstall registration did not disappear.");
                    return "Real test uninstaller exited zero and removed only its own GUID key. A real signed Edge process in a unique temporary profile remained alive; completion was explicitly ExternalBrowser, not a falsely empty job. No UAC or user browser was used.";
                }
                finally { CloseOwnedBrowser(ownedBrowser); Registry.CurrentUser.DeleteSubKeyTree(fixture.App.RegistryKeyPath[5..], false); }
            });
        await RunTest("Live engine queue finishes an owned uninstall while its isolated signed browser remains open", results, logger, async () =>
        {
            var fixture = CreateBrowserFixture(browser, remove: true, worker: false);
            var nextId = Guid.NewGuid().ToString("N");
            var next = new InstalledApp { Id = nextId, Name = "Owned next confirmed fixture", Source = "Fixture", RegistryHive = "HKCU",
                RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + nextId,
                UninstallString = $"\"{Environment.ProcessPath}\" --fixture-uninstall {nextId}" };
            using (var key = Registry.CurrentUser.CreateSubKey(next.RegistryKeyPath[5..])) { key.SetValue("DisplayName", next.Name); key.SetValue("UninstallString", next.UninstallString); }
            Process? ownedBrowser = null;
            var engine = new UninstallService(TimeSpan.FromSeconds(15));
            Task<BatchRunResult>? queue = null;
            try
            {
                queue = BatchUninstallCoordinator.RunAsync([fixture.App, next], app => engine.UninstallAsync(app), () => false);
                ownedBrowser = await PinFixtureBrowser(fixture.Root);
                var result = await queue.WaitAsync(TimeSpan.FromSeconds(25));
                using var firstKey = Registry.CurrentUser.OpenSubKey(fixture.App.RegistryKeyPath[5..]);
                using var nextKey = Registry.CurrentUser.OpenSubKey(next.RegistryKeyPath[5..]);
                if (result.Successful != 2 || result.Failed != 0 || result.Skipped != 0 || UninstallService.HasPendingOperation || firstKey is not null || nextKey is not null || ownedBrowser.HasExited)
                    throw new Exception("The real live engine queue remained blocked by the isolated browser or fabricated a removal.");
                return "Two real GUID-bound fixture uninstallers finished through the live engine and the confirmed queue. The signed headless browser kept its unique temporary profile alive and was not killed to manufacture completion.";
            }
            finally
            {
                CloseOwnedBrowser(ownedBrowser);
                if (queue is not null) await queue.WaitAsync(TimeSpan.FromSeconds(20));
                if (UninstallService.HasPendingOperation) await engine.RetryVerificationAsync(fixture.App);
                Registry.CurrentUser.DeleteSubKeyTree(fixture.App.RegistryKeyPath[5..], false);
                Registry.CurrentUser.DeleteSubKeyTree(next.RegistryKeyPath[5..], false);
            }
        });
        await RunTest("Read-only retry resolves an owned browser handoff after an early timeout without relaunching", results, logger, async () =>
        {
            var fixture = CreateBrowserFixture(browser, remove: true, worker: false);
            var engine = new UninstallService(TimeSpan.FromMilliseconds(500));
            Process? ownedBrowser = null;
            try
            {
                var first = await engine.UninstallAsync(fixture.App);
                ownedBrowser = await PinFixtureBrowser(fixture.Root);
                var originalMarker = File.ReadAllText(Path.Combine(fixture.Root, "browser.json"));
                if (first.Success || !UninstallService.HasPendingOperation) throw new Exception("Early timeout did not preserve honest live review evidence.");
                var retried = await engine.RetryVerificationAsync(fixture.App);
                if (!retried.Success || retried.ExitCode != 0 || UninstallService.HasPendingOperation || ownedBrowser.HasExited ||
                    File.ReadAllText(Path.Combine(fixture.Root, "browser.json")) != originalMarker)
                    throw new Exception("Read-only retry remained stuck or relaunched/killed the owned browser fixture.");
                return "Initial 500ms timeout stayed unresolved. Read-only retry verified the same root exit and missing GUID registration while the same real browser PID remained alive; no new launch.";
            }
            finally
            {
                CloseOwnedBrowser(ownedBrowser);
                if (UninstallService.HasPendingOperation) await engine.RetryVerificationAsync(fixture.App);
                Registry.CurrentUser.DeleteSubKeyTree(fixture.App.RegistryKeyPath[5..], false);
            }
        });
        foreach (var scenario in new[]
        {
            (Name: "a retained installation registration", Remove: false, Worker: false, Exit: 0),
            (Name: "a still-running real uninstall worker", Remove: true, Worker: true, Exit: 0),
            (Name: "a failed real uninstaller exit despite registration absence", Remove: true, Worker: false, Exit: 1603)
        })
            await RunTest("Real browser cannot hide " + scenario.Name, results, logger, async () =>
            {
                var fixture = CreateBrowserFixture(browser, scenario.Remove, scenario.Worker, scenario.Exit);
                Process? ownedBrowser = null;
                using var execution = UninstallerProcess.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fixture-browser-handoff " + fixture.Id) { UseShellExecute = false, CreateNoWindow = true },
                    new(fixture.App.RegistryKeyPath, fixture.App.Name, fixture.Root, Environment.ProcessPath!));
                try
                {
                    ownedBrowser = await PinFixtureBrowser(fixture.Root);
                    if (await execution.WaitAsync(TimeSpan.FromSeconds(3), null) || execution.IsRemovalBoundaryComplete || ownedBrowser.HasExited)
                        throw new Exception("Active installer work or retained registration was ignored merely because a browser was also present.");
                    return "Real browser remains running; " + scenario.Name + " prevents early completion. No success was manufactured.";
                }
                finally
                {
                    CloseOwnedBrowser(ownedBrowser);
                    await execution.WaitAsync(TimeSpan.FromSeconds(12), null); // Let the owned seven-second worker finish naturally.
                    Registry.CurrentUser.DeleteSubKeyTree(fixture.App.RegistryKeyPath[5..], false);
                }
            });
    }

    private static (string Id, string Root, InstalledApp App) CreateBrowserFixture(string? browser, bool remove, bool worker, int exitCode = 0)
    {
        if (browser is null) throw new Exception("A real installed Edge is required; browser validation was not executed.");
        var id = Guid.NewGuid().ToString("N"); var root = Path.Combine(Path.GetTempPath(), "Uninstaller77_BrowserHandoff_" + id); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "owner.txt"), id);
        File.WriteAllText(Path.Combine(root, "configuration.json"), JsonSerializer.Serialize(new { browser, remove, worker, exitCode }));
        var app = new InstalledApp { Id = id, Name = "Owned browser handoff fixture", Source = "Fixture", RegistryHive = "HKCU", InstallLocation = root,
            RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + id,
            UninstallString = $"\"{Environment.ProcessPath}\" --fixture-browser-handoff {id}" };
        using var key = Registry.CurrentUser.CreateSubKey(app.RegistryKeyPath[5..]); key.SetValue("DisplayName", app.Name); key.SetValue("InstallLocation", root); key.SetValue("UninstallString", app.UninstallString);
        return (id, root, app);
    }

    private static async Task<Process> PinFixtureBrowser(string root)
    {
        var marker = Path.Combine(root, "browser.json");
        for (var count = 0; count < 100; count++)
        {
            if (File.Exists(marker))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(marker));
                    var process = Process.GetProcessById(document.RootElement.GetProperty("pid").GetInt32());
                    _ = process.Handle;
                    if (Math.Abs((process.StartTime.ToUniversalTime() - document.RootElement.GetProperty("start").GetDateTime()).TotalMilliseconds) > 10 || process.HasExited)
                    { process.Dispose(); throw new Exception("Owned browser identity mismatch or premature exit."); }
                    return process;
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
            await Task.Delay(100);
        }
        throw new Exception("Owned real browser fixture did not start.");
    }

    private static void CloseOwnedBrowser(Process? browser)
    {
        if (browser is null) return;
        try { if (!browser.HasExited) { browser.Kill(entireProcessTree: true); browser.WaitForExit(5000); } }
        finally { browser.Dispose(); } // Pinned handle of this test's unique-profile process only.
    }
}
