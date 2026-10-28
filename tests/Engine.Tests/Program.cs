using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Services;

internal static class EngineTestsProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == UninstallBroker.Switch)
        {
            try { return UninstallBroker.RunAsync(args).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
        }
        // Explicit, separately authorized UAC acceptance check. Never part of
        // --report-dir; it can remove only the unique HKCU fixture created here.
        if (args.Length == 2 && args[0] == "--broker-uac-check")
        {
            var evidencePath = Path.GetFullPath(args[1]); Directory.CreateDirectory(evidencePath);
            var id = Guid.NewGuid().ToString("N");
            var keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + id;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                { key.SetValue("DisplayName", "Uninstaller 77 Owned UAC Test"); key.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" --fixture-uninstall {id}"); }
                using var session = UninstallBroker.StartAsync("HKCU\\" + keyPath, false, Environment.ProcessPath!, "--fixture-uninstall " + id, elevate: true).GetAwaiter().GetResult();
                using var worker = session.Process;
                var completed = session.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
                using var remaining = Registry.CurrentUser.OpenSubKey(keyPath);
                var passed = completed && session.TreeComplete && session.ExitCode == 0 && remaining is null;
                File.WriteAllText(Path.Combine(evidencePath, "UAC_RESULT.json"), JsonSerializer.Serialize(new
                { passed, completed, session.TrackingStarted, session.TreeComplete, session.ExitCode, session.ChildId, session.Error,
                    fixtureRegistration = "HKCU\\" + keyPath, removed = remaining is null, realProgramsRemoved = 0,
                    note = "Explicit UAC acceptance check; same live broker/job sources, test apphost. Shipping WPF/UI not visually verified by this check." }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(passed ? "UAC fixture passed." : "UAC fixture failed: " + session.Error);
                return passed ? 0 : 1;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(evidencePath, "UAC_ERROR.log"), ex.ToString()); return 2; }
            finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
        }
        if (args.Length == 2 && args[0] == "--inspect-unregistered")
        {
            var catalog = new RegistryDiscoveryService().DiscoverApps(includeMsix: false);
            var plan = new UnregisteredAppService().Prepare(args[1], catalog);
            Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, plan.Name, plan.Root, plan.Target, plan.Kind,
                fileCount = plan.Files.Count, plan.Bytes, plan.Fingerprint, fromBackup = plan.StoredReceipt is not null, startsUninstaller = false, removesFiles = false }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--browser-inventory")
        {
            var diagnostics = new List<BrowserExtensionInventoryService.ScanDiagnostic>();
            var inventory = BrowserExtensionInventoryService.ScanCurrentUser(diagnostics.Add);
            var report = new
            {
                executedUtc = DateTimeOffset.UtcNow, mode = "Read-only desktop-user inventory; no browser launched or modified",
                detectedBrowsers = BrowserExtensionInventoryService.DetectCurrentUserBrowsers(),
                count = inventory.Count, incomplete = diagnostics.Count > 0,
                browsers = inventory.GroupBy(item => item.Browser).Select(group => new { browser = group.Key, count = group.Count(), profiles = group.Select(item => item.Profile).Distinct().ToArray() }),
                identities = inventory.Select(item => new { item.Browser, item.Profile, item.Id, item.InstallationLocation, item.Name, item.Version, item.EnabledState, item.IsPolicyManaged }),
                diagnostics
            };
            var reportPath = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Read-only browser inventory: {inventory.Count} instances, {diagnostics.Count} diagnostics. Evidence: {reportPath}");
            return diagnostics.Count == 0 ? 0 : 1;
        }
        // The test-only process can remove exactly its own unique HKCU fixture.
        if (args.Length == 2 && args[0] == "--fixture-browser-handoff" && Guid.TryParseExact(args[1], "N", out _))
        {
            var folder = Path.Combine(Path.GetTempPath(), "Uninstaller77_BrowserHandoff_" + args[1]);
            if (File.ReadAllText(Path.Combine(folder, "owner.txt")) != args[1]) return 87;
            using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "configuration.json")));
            var browser = configuration.RootElement.GetProperty("browser").GetString()!;
            if (PostUninstallBrowserHandoff.ReadTrustedIdentity(browser) is null) return 87;
            var start = new System.Diagnostics.ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "--headless", "--edge-skip-compat-layer-relaunch", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--disable-component-update", "--disable-sync", "--host-resolver-rules=MAP * 0.0.0.0", "--user-data-dir=" + Path.Combine(folder, "BrowserProfile"), "about:blank" }) start.ArgumentList.Add(argument);
            using var child = System.Diagnostics.Process.Start(start)!;
            _ = child.Handle;
            File.WriteAllText(Path.Combine(folder, "browser.json"), JsonSerializer.Serialize(new { pid = child.Id, start = child.StartTime.ToUniversalTime() }));
            if (configuration.RootElement.GetProperty("worker").GetBoolean())
            {
                using var worker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--fixture-delay 7000") { UseShellExecute = false, CreateNoWindow = true });
            }
            if (configuration.RootElement.GetProperty("remove").GetBoolean())
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + args[1], false);
            return configuration.RootElement.GetProperty("exitCode").GetInt32();
        }
        if (args.Length == 2 && args[0] == "--inspect-launch-registry")
        {
            var app = RegistryDiscoveryService.ReadExactRegistration(args[1]) ?? throw new InvalidDataException("Registration unavailable.");
            var launch = UninstallService.PrepareLaunch(app, false);
            Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, app.Name, app.InstallerType, app.RegistryKeyPath, launch.Executable, launch.Arguments, launch.IsMsi, uninstallerLaunched = false }));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--fixture-uninstall-guid" && Guid.TryParseExact(args[1], "N", out var fixtureGuid))
        {
            var folder = Path.Combine(Path.GetTempPath(), "Uninstaller77_GuidExe_" + args[1]);
            if (File.ReadAllText(Path.Combine(folder, "owner.txt")) != args[1]) return 87;
            var keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + fixtureGuid.ToString("B");
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (key?.GetValue("DisplayName") as string != "Owned GUID EXE " + args[1] ||
                    key.GetValue("UninstallString") as string != $"\"{Environment.ProcessPath}\" --fixture-uninstall-guid {args[1]}") return 87;
            }
            File.WriteAllText(Path.Combine(folder, "executed.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, executable = Environment.ProcessPath, fixture = args[1] }));
            Registry.CurrentUser.DeleteSubKey(keyPath, false);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--fixture-uninstall-shortcut" && Guid.TryParseExact(args[1], "N", out _))
        {
            var fixture = Path.Combine(Path.GetTempPath(), "Uninstaller77_DesktopShortcut_" + args[1]);
            if (File.ReadAllText(Path.Combine(fixture, "owner.txt")) != args[1]) return 87;
            File.Delete(Path.Combine(fixture, "App", "Owned.exe"));
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + args[1], false);
            File.WriteAllText(Path.Combine(fixture, "execution.txt"), "Owned real uninstaller PID=" + Environment.ProcessId);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--fixture-uninstall-pair")
        {
            var identities = args[1].Split(':');
            if (identities.Length != 2 || !identities.All(value => Guid.TryParseExact(value, "N", out _))) return 87;
            var folder = Path.Combine(Path.GetTempPath(), "Uninstaller77_RemovedPeer_" + identities[0]);
            if (!File.Exists(Path.Combine(folder, "owner.txt")) || File.ReadAllText(Path.Combine(folder, "owner.txt")) != args[1]) return 87;
            using var fixtureKeys = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
            File.Delete(Path.Combine(folder, "peer.exe"));
            foreach (var identity in identities) fixtureKeys?.DeleteSubKeyTree("WinUninstaller_Test_" + identity, false);
            File.WriteAllText(Path.Combine(folder, "execution.txt"), "Owned pair uninstaller PID=" + Environment.ProcessId);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--fixture-vendor-refusal" && Guid.TryParseExact(args[1], "N", out _))
        {
            var logs = Path.Combine(Path.GetTempPath(), "Uninstaller77_VendorQueue_" + args[1], "Logs");
            Directory.CreateDirectory(logs);
            var name = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + "_" + Environment.ProcessId + "_Launcher.log";
            File.WriteAllText(Path.Combine(logs, name), "ERROR rUninstall: Cannot uninstall Riot Client due to Riot Games are still installed");
            return 0; // Completed real fixture process, deliberately retaining only its owned registration.
        }
        if (args.Length == 2 && args[0] == "--fixture-uninstall" && Guid.TryParseExact(args[1], "N", out _))
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true);
            root?.DeleteSubKeyTree("WinUninstaller_Test_" + args[1], false);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--fixture-exit" && int.TryParse(args[1], out var exitCode) && exitCode is 1603 or 1605 or 3010)
            return exitCode;
        if (args.Length == 2 && args[0] is "--fixture-child" or "--fixture-delay" && int.TryParse(args[1], out var delay) && delay is >= 100 and <= 10000)
        {
            if (args[0] == "--fixture-child")
                using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe"), $"--fixture-delay {delay}")
                    { UseShellExecute = false, CreateNoWindow = true })) { }
            else Thread.Sleep(delay);
            return 0;
        }
        if (args.Length != 2 || args[0] != "--report-dir")
        {
            Console.Error.WriteLine("Usage: Uninstaller77.EngineTests.exe --report-dir <evidence-directory>");
            return 2;
        }
        var evidence = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(evidence);
        var ownedRoot = Path.Combine(Path.GetTempPath(), "Uninstaller77_EngineAudit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var extractor = new IconExtractionService();
            var suite = new TestRunnerService(new RegistryDiscoveryService(), extractor,
                new IconCacheService(extractor, Path.Combine(ownedRoot, "Icons")),
                new DatabaseService(Path.Combine(ownedRoot, "suite.db")), new UninstallService(), new ResidualScanService());
            using var log = new StreamWriter(Path.Combine(evidence, "ENGINE_TESTS.log")) { AutoFlush = true };
            void Logger(string line) { Console.WriteLine(line); log.WriteLine(line); }
            var results = Task.Run(() => suite.RunAllTestsAsync(Logger)).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(evidence, "ENGINE_RESULTS.json"), JsonSerializer.Serialize(new
            {
                executedUtc = DateTimeOffset.UtcNow, suite = "Active engine plus preserved historical Phase12/13 tests",
                passed = results.Count(r => r.Passed), failed = results.Count(r => !r.Passed),
                fixtureRoot = ownedRoot, results
            }, new JsonSerializerOptions { WriteIndented = true }));
            return results.All(r => r.Passed) ? 0 : 1;
        }
        catch (Exception exception) { File.WriteAllText(Path.Combine(evidence, "RUNNER_ERROR.log"), exception.ToString()); Console.Error.WriteLine(exception); return 2; }
        // Retain this uniquely named directory as evidence; do not delete other test roots.
    }
}
