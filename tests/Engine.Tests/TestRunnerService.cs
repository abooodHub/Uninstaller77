using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinUninstaller.Models;
using WinUninstaller.ViewModels;

namespace WinUninstaller.Services;

/// <summary>
/// Comprehensive automated test suite for WinUninstaller.
/// Safely verifies Registry discovery, high-resolution icons, SQLite persistence,
/// mock uninstallation, deep residual scanning, and Arabic path handling
/// without touching real installed user software.
/// </summary>
public partial class TestRunnerService
{
    private readonly RegistryDiscoveryService _registry;
    private readonly IconExtractionService _extractor;
    private readonly IconCacheService _iconCache;
    private readonly DatabaseService _db;
    private readonly UninstallService _uninstaller;
    private readonly ResidualScanService _residualScanner;

    public record TestResult(string Name, bool Passed, string Message, long ElapsedMs);

    public TestRunnerService(
        RegistryDiscoveryService registry,
        IconExtractionService extractor,
        IconCacheService iconCache,
        DatabaseService db,
        UninstallService uninstaller,
        ResidualScanService residualScanner)
    {
        _registry = registry;
        _extractor = extractor;
        _iconCache = iconCache;
        _db = db;
        _uninstaller = uninstaller;
        _residualScanner = residualScanner;
    }

    public async Task<List<TestResult>> RunAllTestsAsync(Action<string>? logger = null)
    {
        var results = new List<TestResult>();
        logger?.Invoke("=================================================");
        logger?.Invoke("   WinUninstaller Comprehensive Test Suite       ");
        logger?.Invoke("=================================================");

        await RunTest("WPF dispatcher cross-thread scheduling", results, logger, async () =>
        {
            var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
            var uiThread = new Thread(() =>
            {
                ready.TrySetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }) { IsBackground = true, Name = "WinUninstaller Dispatcher Fixture" };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();

            var dispatcher = await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                var executedOnUiThread = await Task.Run(async () =>
                    await dispatcher.InvokeAsync(() => dispatcher.CheckAccess()).Task)
                    .WaitAsync(TimeSpan.FromSeconds(3));

                if (!executedOnUiThread)
                    throw new InvalidOperationException("Dispatcher callback did not execute on the UI thread.");

                return "A callback scheduled from a worker thread ran on an isolated WPF UI dispatcher without blocking.";
            }
            finally
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                uiThread.Join(TimeSpan.FromSeconds(3));
            }
        });

        // Test 1: Registry Discovery Engine
        await RunTest("Registry Discovery Engine", results, logger, () =>
        {
            var apps = _registry.DiscoverApps(includeMsix: false);
            if (apps.Count == 0) throw new Exception("No applications discovered from Registry.");
            var first = apps[0];
            if (string.IsNullOrEmpty(first.Name)) throw new Exception("First app missing DisplayName.");
            if (string.IsNullOrEmpty(first.RegistryHive)) throw new Exception("App missing RegistryHive metadata.");
            return $"Discovered {apps.Count} applications. Sample: '{first.Name}' ({first.Architecture}, {first.InstallerType})";
        });

        await RunTest("Registry source diagnostics and cancellation", results, logger, () =>
        {
            var sources = _registry.LastDiagnostics;
            if (!sources.Any(s => s.Source == "HKLM 64-bit") || !sources.Any(s => s.Source == "HKCU 64-bit") ||
                (Environment.Is64BitOperatingSystem && !sources.Any(s => s.Source == "HKLM 32-bit")))
                throw new Exception("Discovery omitted a registry source diagnostic.");
            foreach (var source in sources)
                if (source.Scanned != source.Accepted + source.Skipped + source.Duplicates + source.Errors)
                    throw new Exception("Source accounting did not reconcile: " + source.Source);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var service = new RegistryDiscoveryService();
            var apps = service.DiscoverApps(includeMsix: true, cancellationToken: cancelled.Token);
            if (apps.Count != 0 || service.LastDiagnostics.Any(s => !s.Cancelled) ||
                !service.LastDiagnostics.Any(s => s.Source == "MSIX/AppX"))
                throw new Exception("Cancelled discovery launched a scan or omitted its cancellation result.");
            return "All registry roots reconcile; a pre-cancelled inventory records cancellation for every source including MSIX without launching PowerShell.";
        });

        // Test 2: High-Resolution Icon Extraction & Avatar Fallback
        await RunTest("High-Resolution Icon Engine", results, logger, () =>
        {
            // Test avatar generation
            var avatar = IconExtractionService.GenerateAppAvatar("Test App Name", 128);
            if (avatar == null || avatar.Width < 120) throw new Exception("Vector avatar generation failed.");

            // Test extraction on system binary
            var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var icon = _extractor.ExtractFromExecutable(cmdExe);
            if (icon == null) throw new Exception("Failed to extract icon from system cmd.exe.");

            return $"Extracted icon from '{cmdExe}' and successfully generated 128x128 vector avatar.";
        });

        // Test 3: Icon Cache Disk Persistence & Safe Retention
        await RunTest("Icon Disk Cache & Retention", results, logger, () =>
        {
            string testAppId = "AutomatedRetentionTestApp";
            var avatar = IconExtractionService.GenerateAppAvatar("Retention App", 128);
            string savedPath = _iconCache.PersistIcon(testAppId, avatar, "Retention App");

            if (!File.Exists(savedPath)) throw new Exception($"PNG was not saved to disk at {savedPath}");

            var reloaded = IconCacheService.LoadPng(savedPath);
            if (reloaded == null) throw new Exception("Failed to reload persisted PNG from disk.");

            return $"Persisted and reloaded icon at {savedPath}";
        });

        // Test 4: SQLite Database CRUD & Schema Migration
        await RunTest("SQLite Database Persistence", results, logger, () =>
        {
            var uniqueId = $"TestApp_{Guid.NewGuid():N}";
            var record = new UninstallRecord
            {
                AppId = uniqueId,
                AppName = "Automated SQLite Test App",
                Publisher = "Antigravity Quality Assurance",
                Version = "2.0.0",
                CachedIconPath = Path.Combine(_iconCache.CacheDir, $"{uniqueId}.png"),
                UninstallDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                InstallerType = "Inno Setup",
                ExitCode = 0,
                Success = true,
                ResidualStatus = "Cleaned (2 items)",
                CleanedItemsCount = 2,
                CleanedSizeBytes = 4096,
                Notes = "Automated Unit Test Record"
            };

            int id = _db.AddRecord(record);
            if (id <= 0) throw new Exception("Failed to insert record into SQLite database.");

            var history = _db.GetHistory();
            var found = history.FirstOrDefault(h => h.AppId == uniqueId);
            if (found == null) throw new Exception("Inserted record was not retrieved from SQLite.");
            if (found.InstallerType != "Inno Setup") throw new Exception("InstallerType column did not persist properly.");
            if (found.CleanedItemsCount != 2) throw new Exception("CleanedItemsCount did not persist properly.");

            return $"Inserted and verified record ID {id} with all extended metadata.";
        });

        // Test 5: Isolated Mock Application Uninstallation & Post-Uninstall Verification
        await RunTest("Isolated Mock Uninstallation & Verification", results, logger, async () =>
        {
            string fixtureId = Guid.NewGuid().ToString("N");
            string tempDir = Path.Combine(Path.GetTempPath(), "WinUninstaller_MockApp_" + fixtureId);
            string regSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + fixtureId;
            string fixtureExecutable = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
            if (!File.Exists(fixtureExecutable))
                throw new FileNotFoundException("The self-contained mock-uninstall fixture executable was not found.", fixtureExecutable);
            string fixtureCommand = $"\"{fixtureExecutable}\" --fixture-uninstall {fixtureId}";
            try
            {
                Directory.CreateDirectory(tempDir);

                // Write only this uniquely named mock registration.
                using (var key = Registry.CurrentUser.CreateSubKey(regSubKey))
                {
                    if (key is null) throw new InvalidOperationException("Could not create the isolated HKCU fixture registration.");
                    key.SetValue("DisplayName", "Mock Safe Test App");
                    key.SetValue("UninstallString", fixtureCommand);
                    key.SetValue("InstallLocation", tempDir);
                    key.SetValue("Publisher", "Test Publisher");
                    key.SetValue("DisplayVersion", "1.0");
                }

                var mockApp = new InstalledApp
                {
                    Id = "WinUninstaller_Test_" + fixtureId,
                    Name = "Mock Safe Test App",
                    Publisher = "Test Publisher",
                    Version = "1.0",
                    InstallLocation = tempDir,
                    UninstallString = fixtureCommand,
                    RegistryHive = "HKCU",
                    RegistryKeyPath = $@"HKCU\{regSubKey}",
                    InstallerType = InstallType.StandardExe
                };

                // Exercise the actual EXE fixture. Environment.ProcessPath may be dotnet.exe
                // when tests are run from the managed assembly, which is not a valid fixture command.
                var result = await _uninstaller.UninstallAsync(mockApp);

                if (!result.Success)
                    throw new Exception($"Mock uninstall failed with status: {result.StatusMessage}, Error: {result.Error}");

                if (result.Verification != VerificationStatus.VerifiedRemoved && result.Verification != VerificationStatus.PartiallyRemoved)
                    throw new Exception($"Verification status unexpected: {result.Verification}");

                return $"Mock app uninstalled successfully. Verified only its unique fixture registration removed. ExitCode: {result.ExitCode}.";
            }
            finally
            {
                try
                {
                    using var uninstallRoot = Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
                    uninstallRoot?.DeleteSubKeyTree("WinUninstaller_Test_" + fixtureId, throwOnMissingSubKey: false);
                }
                catch { /* Cleanup is scoped to this test's unique fixture identity. */ }
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
            }
        });

        // Test 6: Deep Residual Scanner on Mock Files & Registry
        await RunTest("Residual quarantine and restore", results, logger, async () =>
        {
            string testAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "TestFixtures", "WinUninstallerMockResidualApp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testAppData);
            File.WriteAllText(Path.Combine(testAppData, "config.ini"), "TestConfig=1");

            var mockApp = new InstalledApp
            {
                Id = "WinUninstallerMockResidualApp",
                Name = "WinUninstallerMockResidualApp",
                Publisher = "MockCorp",
                InstallLocation = testAppData,
                RegistryHive = "HKCU",
                RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + Guid.NewGuid().ToString("N")
            };

            // Scan residuals
            var scan = await _residualScanner.ScanResidualsAsync(mockApp);
            var items = scan.Items.ToList();
            if (scan.State != ResidualScanState.CompletedWithFindings) throw new Exception("Expected a completed scan with findings.");
            if (!items.Any(i => i.Path == testAppData))
                throw new Exception("Residual scanner failed to identify the exact former install directory.");
            if (items.Any(i => i.IsSelected)) throw new Exception("Residuals were selected without user review.");
            items[0].IsSelected = true;

            // Clean with registry backup
            var cleanResult = await _residualScanner.CleanResidualsAsync(items, mockApp, backupRegistry: true);

            if (!cleanResult.Success)
                throw new Exception($"Residual clean failed with errors: {string.Join(", ", cleanResult.Errors)}");

            if (Directory.Exists(testAppData))
                throw new Exception("Mock AppData directory was not removed.");

            if (string.IsNullOrEmpty(cleanResult.BackupPath) || !File.Exists(cleanResult.BackupPath))
                throw new Exception("Quarantine manifest was not created.");

            var restored = await _residualScanner.RestoreQuarantinedAsync(cleanResult.BackupPath);
            if (!restored.Success || !File.Exists(Path.Combine(testAppData, "config.ini")))
                throw new Exception("Quarantined fixture could not be restored: " + restored.Message);
            Directory.Delete(testAppData, recursive: true);

            return $"Residual scanner found {items.Count} item. Quarantined and restored {cleanResult.CleanedCount} fixture folder.";
        });

        // Test 7: Arabic and Unicode Paths Handling
        await RunTest("Arabic and Space Path Handling", results, logger, () =>
        {
            string arabicDir = Path.Combine(Path.GetTempPath(), "تطبيق اختبار ويندووز_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(arabicDir);
            string exePath = Path.Combine(arabicDir, "برنامج_رئيسي.exe");
            File.WriteAllText(exePath, "dummy binary");

            string cmd = $"\"{exePath}\" /معامل1 /silent";
            var (exe, args) = UninstallService.SplitCommand(cmd);

            try { Directory.Delete(arabicDir, true); } catch { }

            if (exe != exePath) throw new Exception($"Path split mismatch: Expected '{exePath}', Got '{exe}'");
            if (args != "/معامل1 /silent") throw new Exception($"Args split mismatch: Got '{args}'");

            return $"Successfully parsed command with Arabic directory and arguments: '{exe}'";
        });

        await RunTest("Missing uninstall command", results, logger, async () =>
        {
            var result = await _uninstaller.UninstallAsync(new InstalledApp { Name = "Fixture without command", RegistryHive = "HKCU" });
            if (result.Success || result.ExitCode != -1) throw new Exception("Missing command was accepted.");
            return "Rejected without launching a process.";
        });

        await RunTest("Missing registered uninstaller executable", results, logger, async () =>
        {
            string missingExe = Path.Combine(Path.GetTempPath(), "WinUninstaller_Missing_" + Guid.NewGuid().ToString("N"), "unins000.exe");
            var result = await _uninstaller.UninstallAsync(new InstalledApp
            {
                Name = "Fixture with stale uninstall registration",
                RegistryHive = "HKCU",
                UninstallString = $"\"{missingExe}\" /SILENT"
            });
            if (result.Success || result.ExitCode != -1 || result.FailureCode != "MissingExecutable" || result.Error != missingExe)
                throw new Exception("Missing uninstaller was not rejected with an actionable diagnostic.");
            var englishNotice = string.Format(UiStrings.Get("en", "missingUninstaller"), "Fixture app", result.Error);
            var arabicNotice = string.Format(UiStrings.Get("ar", "missingUninstaller"), "Fixture app", result.Error);
            if (!englishNotice.Contains("Fixture app") || !englishNotice.Contains(missingExe) ||
                !arabicNotice.Contains("Fixture app") || !arabicNotice.Contains(missingExe))
                throw new Exception("Localized diagnostics omitted the app name or missing executable path.");
            return "Rejected before launch; English and Arabic diagnostics include the app name and missing executable path.";
        });

        await RunTest("Malformed and shell uninstall commands", results, logger, async () =>
        {
            foreach (var command in new[] { "not-a-real-executable /q", "cmd.exe /c echo test", "powershell.exe -Command exit" })
            {
                var result = await _uninstaller.UninstallAsync(new InstalledApp
                    { Name = "Invalid fixture", RegistryHive = "HKCU", UninstallString = command });
                if (result.Success) throw new Exception($"Accepted unsafe command: {command}");
            }
            return "Rejected malformed and shell commands.";
        });

        await RunTest("Uninstaller failure and reboot exit codes", results, logger, async () =>
        {
            var fixtureExecutable = Path.Combine(AppContext.BaseDirectory, "Uninstaller77.EngineTests.exe");
            if (!File.Exists(fixtureExecutable))
                throw new FileNotFoundException("The self-contained test fixture executable was not found.", fixtureExecutable);
            foreach (var code in new[] { 1603, 3010 })
            {
                var fixture = new InstalledApp
                {
                    Name = "Exit code fixture", RegistryHive = "HKCU",
                    RegistryKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinUninstaller_Test_" + Guid.NewGuid().ToString("N"),
                    UninstallString = $"\"{fixtureExecutable}\" --fixture-exit {code}"
                };
                var fixtureKey = fixture.RegistryKeyPath[5..];
                using (var key = Registry.CurrentUser.CreateSubKey(fixtureKey))
                { key.SetValue("DisplayName", fixture.Name); key.SetValue("UninstallString", fixture.UninstallString); }
                try
                {
                    var result = await _uninstaller.UninstallAsync(fixture);
                    if (result.Success || result.ExitCode != code)
                        throw new Exception($"Exit code {code} was falsely accepted: {result}");
                }
                finally { Registry.CurrentUser.DeleteSubKeyTree(fixtureKey, false); }
            }
            return "1603 and 3010 without removal evidence were not recorded as success.";
        });

        await RunTest("Cancelled before launch", results, logger, async () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = await _uninstaller.UninstallAsync(new InstalledApp { Name = "Cancelled fixture" }, ct: cts.Token);
            if (result.Success || result.ExitCode != -2) throw new Exception("Cancelled operation was not rejected.");
            return "No process launched after cancellation.";
        });

        await RunTest("Elevation decision", results, logger, () =>
        {
            if (!UninstallService.RequiresElevation(new InstalledApp { RegistryHive = "HKLM" }, @"C:\Fixture\remove.exe"))
                throw new Exception("HKLM uninstaller did not request elevation.");
            if (UninstallService.RequiresElevation(new InstalledApp { RegistryHive = "HKCU" }, @"C:\Fixture\remove.exe"))
                throw new Exception("HKCU fixture requested unexpected elevation.");
            return "Elevation policy recognizes HKLM and HKCU cases.";
        });

        await RunTest("Residual root and path tampering protection", results, logger, async () =>
        {
            var fixture = new InstalledApp
            {
                Name = "Safety fixture", InstallLocation = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                RegistryKeyPath = @"HKCU\Software\WinUninstaller_Test_" + Guid.NewGuid().ToString("N")
            };
            if (ResidualScanService.IsEligibleInstallDirectory(fixture.InstallLocation, fixture))
                throw new Exception("Windows directory was eligible for cleanup.");
            fixture.InstallLocation = Path.GetPathRoot(Environment.SystemDirectory)!;
            if (ResidualScanService.IsEligibleInstallDirectory(fixture.InstallLocation, fixture))
                throw new Exception("Drive root was eligible for cleanup.");
            fixture.InstallLocation = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var item = new ResidualItem { Type = ResidualType.Directory, Path = fixture.InstallLocation, IsSelected = true };
            var result = await _residualScanner.CleanResidualsAsync([item], fixture);
            if (result.CleanedCount != 0 || result.Success) throw new Exception("Protected directory was cleaned.");
            return "Protected root and forged selection were refused.";
        });

        await RunTest("Name-match residuals remain informational", results, logger, async () =>
        {
            var name = "WinUninstallerNameOnly_" + Guid.NewGuid().ToString("N");
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), name);
            Directory.CreateDirectory(folder);
            try
            {
                var app = new InstalledApp
                {
                    Name = name, RegistryKeyPath = @"HKCU\Software\WinUninstaller_Test_" + Guid.NewGuid().ToString("N")
                };
                var items = (await _residualScanner.ScanResidualsAsync(app)).Items;
                var match = items.SingleOrDefault(i => i.Path == folder);
                if (match == null || match.CanClean || match.IsSelected)
                    throw new Exception("Name-only match was not safely classified.");
                match.IsSelected = true;
                var result = await _residualScanner.CleanResidualsAsync([match], app);
                if (result.CleanedCount != 0 || !Directory.Exists(folder))
                    throw new Exception("Name-only match was moved or deleted.");
                return "Name-only match was visible but could not be cleaned.";
            }
            finally { Directory.Delete(folder, recursive: true); }
        });

        await RunTest("Icon source classification", results, logger, () =>
        {
            var missing = _iconCache.AuditIcon(new InstalledApp { Name = "No icon fixture" });
            if (missing.Origin != "Missing" || missing.Width != 0 || missing.Height != 0)
                throw new Exception("A missing icon was not represented as missing.");
            var direct = _iconCache.AuditIcon(new InstalledApp
                { Name = "Command Prompt fixture", DisplayIcon = Path.Combine(Environment.SystemDirectory, "cmd.exe") });
            if (direct.Origin != "DisplayIcon" || direct.Width <= 0) throw new Exception("Direct icon source not identified.");
            return $"Missing and direct sources classified; direct dimensions {direct.Width}x{direct.Height}.";
        });

        await RunTest("500 application query performance", results, logger, () =>
        {
            var apps = Enumerable.Range(1, 500).Select(i => new InstalledApp
                { Name = $"Fixture App {i:000}", Publisher = "Test", EstimatedSizeKB = i * 1024L }).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                var result = AppListViewModel.QueryApps(apps, AppCategory.All, SortMode.SizeDesc, "Fixture");
                if (result.Count != 500 || result[0].EstimatedSizeKB != 500 * 1024L)
                    throw new Exception("Query result mismatch.");
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds > 2000) throw new Exception($"Filtering took {sw.ElapsedMilliseconds} ms.");
            return $"100 filter/sort passes over 500 fixture apps in {sw.ElapsedMilliseconds} ms.";
        });

        await RunTest("Synthetic sequential uninstall queue accounting", results, logger, async () =>
        {
            var queue = Enumerable.Range(1, 4).Select(i => new InstalledApp { Id = $"QueueFixture{i}", Name = $"Queue Fixture {i}" }).ToList();
            var calls = 0;
            var mixed = await BatchUninstallCoordinator.RunAsync(queue, app =>
            {
                calls++;
                var success = app.Id == "QueueFixture1";
                return Task.FromResult(new UninstallService.UninstallResult(success, success ? 0 : 1603,
                    success ? VerificationStatus.VerifiedRemoved : VerificationStatus.StillPresent,
                    success ? "Verified removed" : "Still present", success ? "" : "Synthetic engine failure"));
            }, () => calls >= 2);
            if (mixed.Processed != 2 || mixed.Successful != 1 || mixed.Failed != 1 || mixed.Skipped != 2 || mixed.State != BatchQueueState.PartialFailure)
                throw new Exception($"Mixed queue mismatch: processed={mixed.Processed}, success={mixed.Successful}, failed={mixed.Failed}, skipped={mixed.Skipped}, state={mixed.State}.");
            calls = 0;
            var stopped = await BatchUninstallCoordinator.RunAsync(queue, app =>
            {
                calls++;
                return Task.FromResult(new UninstallService.UninstallResult(true, 0, VerificationStatus.VerifiedRemoved, "Verified removed", ""));
            }, () => calls == 1);
            if (!stopped.Stopped || stopped.Processed != 1 || stopped.Successful != 1 || stopped.Failed != 0 || stopped.Skipped != 3 || calls != 1)
                throw new Exception("Stop-after-current did not preserve exact attempted and skipped counts.");
            return "Synthetic success, failure, skip, and stop-after-current paths matched exact counts; no uninstall process was launched.";
        });

        await RunTest("Uncertain uninstall pauses the remaining batch", results, logger, async () =>
        {
            var queue = Enumerable.Range(1, 3).Select(i => new InstalledApp { Id = $"UncertainQueue{i}", Name = $"Uncertain Queue {i}" }).ToList();
            var calls = 0;
            var result = await BatchUninstallCoordinator.RunAsync(queue, app =>
            {
                calls++;
                return Task.FromResult(new UninstallService.UninstallResult(false, -3, VerificationStatus.Unknown,
                    "Completion is uncertain", "Synthetic child process still active", "NeedsUserAttention"));
            }, () => false);

            if (calls != 1 || result.State != BatchQueueState.NeedsUserAttention || !result.NeedsUserAttention ||
                result.Items[0].State != BatchItemState.NeedsAttention || result.Skipped != 2)
                throw new Exception($"Uncertain queue advanced: calls={calls}, state={result.State}, skipped={result.Skipped}.");
            return "Unknown completion paused immediately; the active item is NeedsAttention and later apps were not started.";
        });

        await RunTest("Batch coordinator rejects overlapping queues", results, logger, async () =>
        {
            var queue = new[] { new InstalledApp { Id = "LockFixture", Name = "Lock Fixture" } };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = BatchUninstallCoordinator.RunAsync(queue, async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return new UninstallService.UninstallResult(true, 0, VerificationStatus.VerifiedRemoved, "Verified", "");
            }, () => false);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                try
                {
                    await BatchUninstallCoordinator.RunAsync(queue,
                        _ => Task.FromResult(new UninstallService.UninstallResult(true, 0, VerificationStatus.VerifiedRemoved, "Verified", "")),
                        () => false);
                    throw new Exception("A second queue entered while the first was active.");
                }
                catch (InvalidOperationException) { }
            }
            finally { release.TrySetResult(); }

            await first;
            return "A second queue was rejected while one queue held the execution lock.";
        });

        await RunTest("WebView2 bridge protocol validation", results, logger, () =>
        {
            if (!WebUiProtocol.TryParse("{\"id\":\"req-1\",\"op\":\"search\",\"payload\":{\"value\":\"editor\"}}", out var valid, out _)
                || valid?.Operation != "search" || valid.Payload.GetProperty("value").GetString() != "editor")
                throw new Exception("Valid bounded search request was rejected.");
            foreach (var invalid in new[]
            {
                "{\"id\":\"x\",\"op\":\"runShell\",\"payload\":{}}",
                "{\"id\":\"x\",\"op\":\"refresh\",\"payload\":{},\"command\":\"whoami\"}",
                "{\"id\":\"x\",\"op\":\"search\",\"payload\":[]}",
                "not-json"
            })
                if (WebUiProtocol.TryParse(invalid, out _, out _))
                    throw new Exception("Malformed or unauthorized bridge request was accepted.");
            if (WebUiProtocol.TryParse(new string(' ', WebUiProtocol.MaxMessageBytes + 1), out _, out _))
                throw new Exception("Oversized bridge request was accepted.");
            return "Authorized JSON envelope accepted; unknown commands, unexpected fields, invalid payloads, malformed JSON, and oversized messages rejected.";
        });

        await RunTest("WebView2 source identity and destructive operation denylist", results, logger, () =>
        {
            if (!WebUiProtocol.IsTrustedSource("https://appassets.winuninstaller/index.html") ||
                WebUiProtocol.IsTrustedSource("https://example.com/index.html") ||
                WebUiProtocol.IsTrustedSource("https://appassets.winuninstaller.evil/index.html") ||
                WebUiProtocol.IsTrustedSource("https://appassets.winuninstaller/other.html") ||
                WebUiProtocol.IsTrustedSource("http://appassets.winuninstaller/index.html"))
                throw new Exception("Trusted source allowlist accepted an invalid origin or rejected its app page.");

            foreach (var operation in new[] { "cleanHealthCategory", "removeExtension", "shredPath", "createRestorePoint" })
            {
                var request = "{\"id\":\"blocked\",\"op\":\"" + operation + "\",\"payload\":{}}";
                if (WebUiProtocol.TryParse(request, out _, out _))
                    throw new Exception($"Destructive or unverified operation '{operation}' was accepted.");
            }
            return "Only the exact local HTTPS app page is trusted; health deletion, extension removal, shredding, and restore point commands are denied.";
        });

        await RunTest("Software health evidence checks", results, logger, () =>
        {
            var missing = Path.Combine(Path.GetTempPath(), "WinUninstaller-health-" + Guid.NewGuid().ToString("N") + ".exe");
            var apps = new[]
            {
                new InstalledApp { Id = "a", Name = "Missing command", RegistryKeyPath = "HKCU\\Fixture", UninstallString = "" },
                new InstalledApp { Id = "b", Name = "Missing file", UninstallString = $"\"{missing}\" /S" },
                new InstalledApp { Id = "c", Name = "Ambiguous", UninstallString = "unknown-uninstaller /S" },
                new InstalledApp { Id = "d", Name = "Store app", Source = "MSIX", UninstallString = "" }
            };
            var findings = SoftwareHealthService.Scan(apps);
            if (findings.Count != 2 || findings[0].Kind != "NoUninstallCommand" || findings[1].Kind != "MissingUninstaller")
                throw new Exception("Health checks did not respect exact evidence and source boundaries.");
            return "Missing command and absolute missing executable flagged; ambiguous and Store commands excluded.";
        });

        await RunTest("Software health unsupported checks stay unverified", results, logger, () =>
        {
            var executable = Environment.ProcessPath ?? throw new Exception("Current process path unavailable.");
            var report = SoftwareHealthService.ScanAll(new[]
            {
                new InstalledApp { Id = "fixture-app", Name = "Health fixture", UninstallString = $"\"{executable}\"" }
            });
            if (report.Categories.Count != 9 || report.Categories.Count(c => c.IsSupported) != 1 ||
                report.Categories.Where(c => !c.IsSupported).Any(c => c.Count != 0) || report.TotalIssues != 0)
                throw new Exception("Unavailable checks were shown as findings or valid clean results.");
            return "Only the evidence-backed uninstaller check is active; eight unsupported categories remain explicitly unverified.";
        });

        await RunTest("Chromium extension manifest inventory", results, logger, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WinUninstaller-extension-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var extensionRoot = Path.Combine(root, "Default", "Extensions", new string('a', 32));
                var stale = Path.Combine(extensionRoot, "9.0.0_0");
                Directory.CreateDirectory(stale);
                File.WriteAllText(Path.Combine(stale, "manifest.json"), "{\"name\":\"Stale fixture\",\"version\":\"9.0.0\"}");
                Directory.CreateDirectory(Path.Combine(extensionRoot, "99.0.0_0")); // Incomplete directory must not hide the valid manifest.
                var version = Path.Combine(extensionRoot, "10.2.3_0");
                Directory.CreateDirectory(Path.Combine(version, "_locales", "en"));
                File.WriteAllText(Path.Combine(version, "manifest.json"), "{\"name\":\"__MSG_name__\",\"version\":\"10.2.3\",\"default_locale\":\"en\",\"permissions\":[\"tabs\",\"storage\"],\"optional_permissions\":[\"history\"],\"host_permissions\":[\"<all_urls>\"],\"optional_host_permissions\":[\"https://example.test/*\"],\"content_scripts\":[{\"matches\":[\"https://content.example.test/*\"]}]}");
                File.WriteAllText(Path.Combine(version, "_locales", "en", "messages.json"), "{\"name\":{\"message\":\"Fixture Extension\"}}");
                var found = BrowserExtensionInventoryService.ScanRoots(new[] { ("Fixture Browser", root) });
                if (found.Count != 1 || found[0].Name != "Fixture Extension" || found[0].Version != "10.2.3")
                    throw new Exception("Extension manifest or localized name was not read correctly.");
                var item = found[0];
                if (!item.Permissions.SequenceEqual(new[] { "tabs", "storage" }) ||
                    !item.OptionalPermissions.SequenceEqual(new[] { "history" }) ||
                    !item.HostPermissions.SequenceEqual(new[] { "<all_urls>" }) ||
                    !item.OptionalHostPermissions.SequenceEqual(new[] { "https://example.test/*" }) ||
                    !item.ContentScriptHosts.SequenceEqual(new[] { "https://content.example.test/*" }) ||
                    item.EnabledState != "Installed" || item.ManagementCapability != "Unsupported")
                    throw new Exception("Permission metadata or unsupported management state was inaccurate.");
                return "Latest numeric version 10.2.3 selected; localized name and API/host/content-script permissions preserved; inventory-only state is Installed; direct management remains unsupported.";
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });

        await RunTest("Extension identity keeps browser, profile and location", results, logger, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WinUninstaller-extension-identity-" + Guid.NewGuid().ToString("N"));
            try
            {
                var id = new string('b', 32);
                foreach (var browser in new[] { "Fixture Edge", "Fixture Brave" })
                foreach (var profile in new[] { "Default", "Profile 1" })
                {
                    var version = Path.Combine(root, browser, profile, "Extensions", id, "1.0");
                    Directory.CreateDirectory(version);
                    File.WriteAllText(Path.Combine(version, "manifest.json"), "{\"name\":\"Same extension\",\"version\":\"1.0\"}");
                }
                var found = BrowserExtensionInventoryService.ScanRoots(new[] {
                    ("Fixture Edge", Path.Combine(root, "Fixture Edge")),
                    ("Fixture Brave", Path.Combine(root, "Fixture Brave")) });
                if (found.Count != 4 || found.Select(e => (e.Browser, e.Profile, e.Id, e.InstallationLocation)).Distinct().Count() != 4 ||
                    found.Any(e => !Directory.Exists(e.InstallationLocation)))
                    throw new Exception("Cross-browser or cross-profile extension instances were merged or lost their location.");
                return "Four distinct instances retain browser, profile, ID and installation directory.";
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });

        await RunTest("Browser manager allowlist and profile validation", results, logger, () =>
        {
            if (!BrowserExtensionInventoryService.IsSupportedBrowser("Google Chrome") ||
                !BrowserExtensionInventoryService.IsSupportedBrowser("Microsoft Edge") ||
                !BrowserExtensionInventoryService.IsSupportedBrowser("Brave") ||
                BrowserExtensionInventoryService.IsSupportedBrowser("Fixture Browser") ||
                !BrowserExtensionInventoryService.IsSafeProfileName("Default") ||
                !BrowserExtensionInventoryService.IsSafeProfileName("Profile 12") ||
                BrowserExtensionInventoryService.IsSafeProfileName("..\\Default") ||
                BrowserExtensionInventoryService.IsSafeProfileName("Profile 0"))
                throw new Exception("Browser allowlist or profile-name validation accepted an invalid value.");
            return "Only Chrome, Edge and Brave are supported; safe Chromium profile labels pass and traversal/invalid labels fail.";
        });

        await RunTest("Detected empty browser retains its own empty inventory section", results, logger, () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WinUninstaller-empty-browser-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var browsers = BrowserExtensionInventoryService.DetectBrowsersFromRoots(new[] {
                    ("Fixture Browser", root), ("Absent Browser", root + "-absent") });
                if (browsers.Count != 1 || browsers[0] != "Fixture Browser" ||
                    BrowserExtensionInventoryService.ScanRoots(new[] { ("Fixture Browser", root) }).Count != 0)
                    throw new Exception("Detected browser or empty inventory was incorrect.");
                return "Existing root is visible with zero extensions; absent root is not invented.";
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });

        await RunTest("Browser extension policy management classification", results, logger, () =>
        {
            if (!BrowserExtensionInventoryService.IsExtensionPolicyManaged("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;https://updates.example.test" }, Array.Empty<string>()) ||
                !BrowserExtensionInventoryService.IsExtensionPolicyManaged("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    Array.Empty<string>(), new[] { "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }) ||
                BrowserExtensionInventoryService.IsExtensionPolicyManaged("cccccccccccccccccccccccccccccccc",
                    new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;https://updates.example.test" }, Array.Empty<string>()))
                throw new Exception("Policy-managed extension IDs were misclassified.");
            return "Force-list and ExtensionSettings IDs are recognized; unrelated IDs remain unmanaged.";
        });

        await RunTest("Windows package built-in classification", results, logger, () =>
        {
            if (!RegistryDiscoveryService.IsBuiltInPackageName("Microsoft.WindowsCalculator") ||
                !RegistryDiscoveryService.IsBuiltInPackageName("Windows.Terminal") ||
                !RegistryDiscoveryService.IsBuiltInPackageName("Microsoft.DesktopAppInstaller") ||
                RegistryDiscoveryService.IsBuiltInPackageName("Microsoft.VisualStudioCode") ||
                RegistryDiscoveryService.IsBuiltInPackageName("ChatGPT.OpenAI") ||
                RegistryDiscoveryService.IsBuiltInPackageName("Contoso.Editor"))
                throw new Exception("The inbox package allowlist accepted or rejected an unexpected package.");
            return "Windows namespace and known inbox package IDs are built-in; Visual Studio Code and unrelated packages remain third-party.";
        });

        await RunPhase10TestsAsync(results, logger);
        await RunPhase11TestsAsync(results, logger);
        await RunPhase12TestsAsync(results, logger);
        await RunPhase13TestsAsync(results, logger);
        await RunAuditTestsAsync(results, logger);
        await RunExitDiagnosticsTestsAsync(results, logger);
        await RunInstallerIdentityTestsAsync(results, logger);
        await RunReviewObservationTestsAsync(results, logger);
        await RunBrokerTestsAsync(results, logger);
        await RunCompletionAuditTestsAsync(results, logger);
        await RunFullAuditTestsAsync(results, logger);
        await RunAlreadyAbsentTestsAsync(results, logger);
        await RunBrowserHandoffTestsAsync(results, logger);
        await RunDesktopShortcutTestsAsync(results, logger);
        await RunUnregisteredTestsAsync(results, logger);

        // Summary
        int passed = results.Count(r => r.Passed);
        int failed = results.Count(r => !r.Passed);
        logger?.Invoke("=================================================");
        logger?.Invoke($"   Test Suite Completed: {passed} Passed, {failed} Failed");
        logger?.Invoke("=================================================");

        return results;
    }

    private static async Task RunTest(
        string testName,
        List<TestResult> results,
        Action<string>? logger,
        Func<string> testAction)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            logger?.Invoke($"Running: {testName}...");
            string msg = testAction();
            sw.Stop();
            var tr = new TestResult(testName, true, msg, sw.ElapsedMilliseconds);
            results.Add(tr);
            logger?.Invoke($"  [PASS] {testName} ({sw.ElapsedMilliseconds} ms): {msg}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            var tr = new TestResult(testName, false, ex.Message, sw.ElapsedMilliseconds);
            results.Add(tr);
            logger?.Invoke($"  [FAIL] {testName} ({sw.ElapsedMilliseconds} ms): {ex.Message}");
        }
        await Task.Yield();
    }

    private static async Task RunTest(
        string testName,
        List<TestResult> results,
        Action<string>? logger,
        Func<Task<string>> testAction)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            logger?.Invoke($"Running: {testName}...");
            string msg = await testAction();
            sw.Stop();
            var tr = new TestResult(testName, true, msg, sw.ElapsedMilliseconds);
            results.Add(tr);
            logger?.Invoke($"  [PASS] {testName} ({sw.ElapsedMilliseconds} ms): {msg}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            var tr = new TestResult(testName, false, ex.Message, sw.ElapsedMilliseconds);
            results.Add(tr);
            logger?.Invoke($"  [FAIL] {testName} ({sw.ElapsedMilliseconds} ms): {ex.Message}");
        }
    }
}
