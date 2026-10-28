using System.IO;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class TestRunnerService
{
    private async Task RunFullAuditTestsAsync(List<TestResult> results, Action<string>? logger)
    {
        var root = Path.Combine(Path.GetTempPath(), "Uninstaller77_FullAudit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await RunTest("Real owned process queue continues after an authenticated vendor refusal and removes only the next fixture", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N"); var nextId = Guid.NewGuid().ToString("N");
            var fixture = Path.Combine(Path.GetTempPath(), "Uninstaller77_VendorQueue_" + id);
            var appFolder = Path.Combine(fixture, "App"); var logs = Path.Combine(fixture, "Logs"); Directory.CreateDirectory(appFolder);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory).Where(file => Path.GetExtension(file) is ".dll" or ".json"))
                File.Copy(file, Path.Combine(appFolder, Path.GetFileName(file)));
            var vendorExe = Path.Combine(appFolder, "RiotClientServices.exe");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe"), vendorExe);
            const string keyRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_";
            var vendor = new InstalledApp { Id = id, Name = "Riot Client", Publisher = "Riot Fixture Studio", Source = "Fixture", RegistryHive = "HKCU",
                RegistryKeyPath = "HKCU\\" + keyRoot + id, UninstallString = $"\"{vendorExe}\" --fixture-vendor-refusal {id}" };
            var next = new InstalledApp { Id = nextId, Name = "Owned selected uninstall fixture", Source = "Fixture", RegistryHive = "HKCU",
                RegistryKeyPath = "HKCU\\" + keyRoot + nextId, UninstallString = $"\"{Environment.ProcessPath}\" --fixture-uninstall {nextId}" };
            foreach (var app in new[] { vendor, next })
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(app.RegistryKeyPath[5..]))
                { key.SetValue("DisplayName", app.Name); key.SetValue("Publisher", app.Publisher); key.SetValue("UninstallString", app.UninstallString); }
            var engine = new UninstallService(null, logs);
            var executed = new List<string>(); var outcomes = new List<UninstallService.UninstallResult>();
            try
            {
                var run = await BatchUninstallCoordinator.RunAsync([vendor, next], async app =>
                { executed.Add(app.Id); var outcome = await engine.UninstallAsync(app); outcomes.Add(outcome); return outcome; }, () => false);
                using var retained = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyRoot + id);
                using var removed = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyRoot + nextId);
                if (run.Successful != 1 || run.Failed != 1 || run.Skipped != 0 || run.Stopped || !executed.SequenceEqual(new[] { id, nextId }) ||
                    outcomes[0].FailureCode != "VendorDependenciesInstalled" || outcomes[0].Success || outcomes[0].ExitCode != 0 ||
                    !outcomes[1].Success || retained is null || removed is not null || !Directory.Exists(logs))
                    throw new Exception("Actual process/job/registration evidence failed: " + string.Join("; ", outcomes.Select(outcome => outcome.ToString())));
                return "Two real test EXEs ran sequentially through the live engine and Windows jobs. PID/time-bound owned vendor log reported one failure; the next real fixture removed only its own GUID key. The first key and its application files remained intact.";
            }
            finally
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyRoot + id, false);
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyRoot + nextId, false);
            }
        });
        await RunTest("Full audit: stale absent registrations never launch a cached uninstaller", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            var app = new InstalledApp { Id = id, Name = "Owned absent registration", RegistryHive = "HKCU",
                RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + id,
                UninstallString = $"\"{Environment.ProcessPath}\" --fixture-delay 100" };
            var result = await new UninstallService().UninstallAsync(app);
            if (result.Success || result.FailureCode != "RegistrationAbsent") throw new Exception("A cached command for an absent registration reached execution instead of a pre-launch rejection: " + result);
            return "Already absent exact registration is reported before launching any cached executable; no historical success invented.";
        });
        await RunTest("Full audit: changed per-user registration cannot run the previously confirmed command", results, logger, async () =>
        {
            var id = Guid.NewGuid().ToString("N");
            var path = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + id;
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path))
            { key.SetValue("DisplayName", "Owned changed command"); key.SetValue("UninstallString", $"\"{Environment.ProcessPath}\" --fixture-delay 100"); }
            try
            {
                var result = await new UninstallService().UninstallAsync(new() { Id = id, Name = "Owned changed command", RegistryHive = "HKCU",
                    RegistryKeyPath = "HKCU\\" + path, UninstallString = $"\"{Environment.ProcessPath}\" --fixture-uninstall {id}" });
                using var remaining = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
                if (result.Success || result.FailureCode != "RegistrationChanged" || remaining is null) throw new Exception("A stale per-user command executed after its registration changed: " + result);
                return "Fresh exact registration differs from confirmed arguments; owned fixture remains present and no executable launches.";
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, false); }
        });
        await RunTest("Full audit: concurrent schema migration preserves the database", results, logger, async () =>
        {
            var path = Path.Combine(root, "migration.db");
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE UninstallHistory(Id INTEGER PRIMARY KEY AUTOINCREMENT,AppId TEXT NOT NULL,AppName TEXT NOT NULL,Publisher TEXT,Version TEXT,CachedIconPath TEXT,UninstallDate TEXT NOT NULL,ExitCode INTEGER NOT NULL DEFAULT 0,Success INTEGER NOT NULL DEFAULT 0,Notes TEXT); INSERT INTO UninstallHistory(AppId,AppName,UninstallDate) VALUES('real','Owned old database','2026-01-01');";
                command.ExecuteNonQuery();
            }
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => new DatabaseService(path))));
            if (new DatabaseService(path).GetHistory().Single().AppId != "real") throw new Exception("Concurrent migration lost the legacy record.");
            return "Eight concurrent constructors migrated one owned legacy schema without duplicate-column failures or data loss.";
        });
        await RunTest("Full audit: invalid preferences cannot partially apply language", results, logger, () =>
        {
            var file = Path.Combine(root, "preferences.json");
            File.WriteAllText(file, "{\"language\":\"ar\",\"reduceMotion\":\"invalid\"}");
            try { WinUninstaller.ViewModels.MainViewModel.ReadPreferences(file); throw new Exception("Invalid preference type was accepted."); }
            catch (InvalidOperationException) { }
            File.WriteAllText(file, "{\"language\":\"ar\",\"reduceMotion\":true}");
            if (WinUninstaller.ViewModels.MainViewModel.ReadPreferences(file) != ("ar", true)) throw new Exception("Valid preferences failed.");
            return "The complete preference object is validated before any observable UI property is applied.";
        });
        await RunTest("Full audit: vendor dependency diagnosis requires the exact Riot attempt", results, logger, () =>
        {
            var logs = Path.Combine(root, "VendorLogs"); Directory.CreateDirectory(logs);
            var app = new InstalledApp { Name = "Riot Client", Publisher = "Riot Games, Inc." };
            var now = DateTimeOffset.Now;
            var file = Path.Combine(logs, now.ToString("yyyy-MM-ddTHH-mm-ss") + "_4321_Launcher.log");
            File.WriteAllText(file, "ERROR rUninstall: Cannot uninstall Riot Client due to Riot Games are still installed");
            if (!VendorUninstallDiagnostics.HasRiotDependencyFailure(app, "RiotClientServices.exe", 4321, now, logs)) throw new Exception("Exact owned vendor evidence was not recognized.");
            if (VendorUninstallDiagnostics.HasRiotDependencyFailure(app, "RiotClientServices.exe", 4322, now, logs) ||
                VendorUninstallDiagnostics.HasRiotDependencyFailure(app, "Other.exe", 4321, now, logs) ||
                VendorUninstallDiagnostics.HasRiotDependencyFailure(app, "RiotClientServices.exe", 4321, now.AddDays(1), logs) ||
                VendorUninstallDiagnostics.HasRiotDependencyFailure(new() { Name = "Other", Publisher = app.Publisher }, "RiotClientServices.exe", 4321, now, logs)) throw new Exception("Unrelated or stale log was accepted.");
            File.WriteAllText(file, new string('x', 1_048_577));
            if (VendorUninstallDiagnostics.HasRiotDependencyFailure(app, "RiotClientServices.exe", 4321, now, logs)) throw new Exception("Oversized vendor evidence accepted.");
            return "Matching app/executable/PID/time recognized; other app, executable, PID, stale time and oversized logs rejected. No vendor process launched.";
        });
        await RunTest("Full audit: a completed vendor refusal does not stop other explicitly selected applications", results, logger, async () =>
        {
            var launched = 0;
            var result = await BatchUninstallCoordinator.RunAsync([new() { Id = "OwnedVendor" }, new() { Id = "OwnedNext" }], app =>
            { launched++; return Task.FromResult(app.Id == "OwnedVendor"
                ? new UninstallService.UninstallResult(false, 0, VerificationStatus.StillPresent, VendorUninstallDiagnostics.RiotDependencyMessage, VendorUninstallDiagnostics.RiotDependencyAdvice, "VendorDependenciesInstalled")
                : new UninstallService.UninstallResult(true, 0, VerificationStatus.VerifiedRemoved, "Owned selected fixture removed.", "")); }, () => false);
            if (launched != 2 || result.Failed != 1 || result.Skipped != 0 || result.Successful != 1 || result.Stopped || result.State != BatchQueueState.PartialFailure)
                throw new Exception("A finished vendor refusal unnecessarily stopped the confirmed queue or became success.");
            return "Finished vendor refusal remains failed; only the next explicitly confirmed fixture runs, with separate honest results.";
        });
        await RunTest("Full audit: manual stop and uncertain completion still block continuation after vendor errors", results, logger, async () =>
        {
            foreach (var uncertain in new[] { false, true })
            {
                var calls = 0;
                var run = await BatchUninstallCoordinator.RunAsync([new() { Id = "Owned1" }, new() { Id = "Owned2" }], _ =>
                { calls++; return Task.FromResult(new UninstallService.UninstallResult(false, 0, uncertain ? VerificationStatus.Unknown : VerificationStatus.StillPresent,
                    "Owned refusal", "", uncertain ? "NeedsUserAttention" : "VendorDependenciesInstalled")); }, () => !uncertain && calls > 0);
                if (calls != 1 || run.Skipped != 1 || run.Successful != 0 || run.NeedsUserAttention != uncertain || run.Stopped == uncertain)
                    throw new Exception("Manual stop or actual completion uncertainty allowed another launch.");
            }
            return "An explicit stop and unknown process completion both preserve the next selected fixture without launch.";
        });
        await RunTest("Full audit: oversized broker request is rejected before worker launch", results, logger, async () =>
        {
            try
            {
                using var worker = await UninstallBroker.StartAsync("Owned", false, "Owned.exe", new string('ا', 40_000), elevate: false);
                throw new Exception("Oversized request reached a worker.");
            }
            catch (InvalidDataException) { return "Frame encoding rejected the owned oversized request before pipe creation, process launch or uncertainty commitment."; }
        });
        await RunTest("Full audit: icon cache identities cannot collide or exceed filename limits", results, logger, () =>
        {
            if (IconCacheService.SanitizeId("owned:a") == IconCacheService.SanitizeId("owned?a"))
                throw new Exception("Distinct app identities map to the same disk icon filename.");
            if (IconCacheService.SanitizeId(new string('x', 600)).Length > 100)
                throw new Exception("A long Registry identity creates an invalid cache filename.");
            return "Distinct identities and long IDs have bounded unique icon keys.";
        });
        await RunTest("Full audit: test-history filtering preserves real notes containing test or fixture", results, logger, () =>
        {
            var db = new DatabaseService(Path.Combine(root, "history.db"));
            db.AddRecord(new() { AppId = "HKCU_RealA", AppName = "Contest Editor", Notes = "Latest installer result" });
            db.AddRecord(new() { AppId = "HKCU_RealB", AppName = "Regular Tool", Notes = "Supports fixture data" });
            db.AddRecord(new() { AppId = "TestApp_" + Guid.NewGuid().ToString("N"), AppName = "Automated SQLite Test App", Notes = "Isolated test" });
            var rows = db.GetHistory(false);
            if (rows.Count != 2 || rows.Any(row => row.AppId.StartsWith("TestApp_")))
                throw new Exception("A real uninstall record was hidden by free-text substring filtering.");
            return "Only explicit legacy test identities are filtered; real free-text notes remain visible.";
        });
        await RunTest("Full audit: future dates are not recently installed and Unix install timestamps parse", results, logger, () =>
        {
            var future = new InstalledApp { InstallDate = DateTime.Today.AddDays(30).ToString("yyyyMMdd") };
            var unix = new InstalledApp { InstallDate = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (future.IsRecentApp) throw new Exception("Future metadata is classified as recently installed.");
            if (unix.ParsedInstallDate?.Date != DateTime.Today || !unix.IsRecentApp) throw new Exception("A vendor Unix install timestamp is not recognized.");
            return "Future metadata excluded; real-world vendor Unix seconds displayed as a date.";
        });
        await RunTest("Full audit: browser profile caps explicitly mark inventory incomplete", results, logger, () =>
        {
            var browser = Path.Combine(root, "BrowserCap");
            for (var i = 0; i < 21; i++) Directory.CreateDirectory(Path.Combine(browser, "Profile " + i));
            var diagnostics = new List<BrowserExtensionInventoryService.ScanDiagnostic>();
            BrowserExtensionInventoryService.ScanRoots([("Google Chrome", browser)], diagnostics.Add);
            if (!diagnostics.Any(item => item.Stage == "Limit")) throw new Exception("21 profiles silently truncated to 20 without an incomplete-inventory diagnostic.");
            return "A safety cap is reported as an incomplete scan rather than a complete empty inventory.";
        });
        await RunTest("Full audit: oversized extension manifests are bounded and reported", results, logger, () =>
        {
            var browser = Path.Combine(root, "BrowserSize");
            var version = Path.Combine(browser, "Default", "Extensions", new string('a', 32), "1.0_0"); Directory.CreateDirectory(version);
            File.WriteAllText(Path.Combine(version, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new { name = "Owned oversized manifest", version = "1.0", description = new string('x', 1_048_577) }));
            var diagnostics = new List<BrowserExtensionInventoryService.ScanDiagnostic>();
            var rows = BrowserExtensionInventoryService.ScanRoots([("Google Chrome", browser)], diagnostics.Add);
            if (rows.Count != 0 || diagnostics.Count == 0) throw new Exception("An oversized manifest was read without a safety limit or diagnostic.");
            return "Oversized owned metadata is rejected with a visible incomplete-scan diagnostic.";
        });
    }
}
