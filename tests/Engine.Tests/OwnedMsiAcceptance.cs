using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using WinUninstaller.Models;
using WinUninstaller.Services;
using WinUninstaller.ViewModels;

internal static class OwnedMsiAcceptance
{
    internal static async Task<int> RunAsync(string productCode, string reportDirectory, bool withQueueProtection = false)
    {
        if (!Guid.TryParseExact(productCode, "B", out var product)) throw new ArgumentException("Expected a fixture ProductCode.");
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Run this acceptance check from a standard, non-elevated process.");

        var id = product.ToString("N");
        var code = product.ToString("B").ToUpperInvariant();
        var registrations = new[] { "HKCU", "HKLM" }.Select(hive =>
            hive + @"\Software\Microsoft\Windows\CurrentVersion\Uninstall\" + code).ToArray();
        var markerKey = @"Software\Uninstaller77\Fixtures\" + id;
        var payload = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Uninstaller77_MsiFixture_" + id, "ownership.txt");
        var app = registrations.Select(RegistryDiscoveryService.ReadExactRegistration).OfType<InstalledApp>().SingleOrDefault();
        if (app is null || app.Name != "Uninstaller 77 Owned MSI " + id || app.Publisher != "Uninstaller 77 Test Fixtures" ||
            app.Version != "1.0.0" || !File.Exists(payload) || File.ReadAllText(payload) != code ||
            !Equals(Registry.GetValue(@"HKEY_CURRENT_USER\" + markerKey, "ProductCode", null), code))
            throw new InvalidOperationException("The application and both ownership markers must match this fixture.");

        var command = RegisteredUninstallCommand.PrepareLaunch(app, true);
        if (!command.IsMsi || RegisteredUninstallCommand.RequiresElevation(app, command))
            throw new InvalidOperationException("Windows Installer did not confirm an unmanaged, non-elevated user MSI. Nothing launched.");

        if (withQueueProtection) return RunProtectedQueue(app, payload, markerKey, reportDirectory);

        var progress = new List<string>();
        var engine = new UninstallService(TimeSpan.FromSeconds(60));
        var result = await engine.UninstallAsync(app, preferQuiet: true,
            progress: new UninstallDiagnostics.Forward<string>(progress.Add, null));
        var registrationAfter = registrations.ToDictionary(path => path, path => UninstallRegistration.Read(path).State.ToString());
        var registrationAbsent = registrationAfter.Values.All(state => state == nameof(UninstallRegistration.Presence.Absent));
        using var markerAfter = Registry.CurrentUser.OpenSubKey(markerKey);
        var payloadExistsAfter = File.Exists(payload);
        var repeat = registrationAbsent
            ? await engine.UninstallAsync(app, preferQuiet: true) : null;
        var missingProductRequiresElevation = RegisteredUninstallCommand.RequiresElevation(app, command);
        var passed = result.Success && result.ObservedExitCode == 0 &&
            result.Verification == VerificationStatus.VerifiedRemoved &&
            registrationAbsent && markerAfter is null && !payloadExistsAfter &&
            repeat is { FailureCode: "RegistrationAbsent", ObservedExitCode: null } && missingProductRequiresElevation;
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(Path.Combine(reportDirectory, "msi-result.json"), JsonSerializer.Serialize(new
        {
            passed, app.Name, productCode = code, registration = app.RegistryKeyPath, command.Executable, command.Arguments, elevationRequested = false,
            registrationBefore = "Present", payloadBefore = true, markerBefore = true, registrationAfter,
            payloadExistsAfter, markerExistsAfter = markerAfter is not null, result, repeat, missingProductRequiresElevation, progress
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(passed ? "Owned MSI lifecycle passed." : "Owned MSI lifecycle failed. See msi-result.json.");
        return passed ? 0 : 1;
    }

    private static int RunProtectedQueue(InstalledApp app, string payload, string markerKey, string reportDirectory)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var name in new[] { "AccentGreen", "AccentRed", "AccentPeach", "AccentBlue" })
            application.Resources[name] = System.Windows.Media.Brushes.Gray;
        application.Startup += async (_, _) =>
        {
            try
            {
                var database = new DatabaseService(Path.Combine(reportDirectory, "history.db"));
                var extractor = new IconExtractionService();
                var residuals = new ResidualScanService();
                var journal = new QueueJournal(Path.Combine(reportDirectory, "Queue"));
                var vm = new AppListViewModel(new RegistryDiscoveryService(), new IconCacheService(extractor, Path.Combine(reportDirectory, "Icons")),
                    database, new UninstallService(TimeSpan.FromSeconds(60)), residuals, journal) { UseWebUi = true };
                var settings = new MainViewModel(vm, new HistoryViewModel(database), residuals, reportDirectory) { ProtectBeforeUninstall = true };
                var backupBeforeLaunch = false;
                vm.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(vm.ProgressApp) && vm.ProgressApp?.Id == app.Id)
                        backupBeforeLaunch = vm.ProtectionReport?.Backups.Single().File is { } file && File.Exists(file) &&
                            UninstallRegistration.Read(app.RegistryKeyPath).State == UninstallRegistration.Presence.Present &&
                            database.GetHistoryPage(true, null, 10).Any(row => row.Outcome == "QueueProtection");
                };

                vm.PromptUninstall(app);
                vm.IsQuietMode = true;
                vm.AutoScanResiduals = false;
                await vm.ConfirmAndExecuteUninstallAsync();

                var report = vm.ProtectionReport;
                var backup = report?.Backups.Single();
                var exportIsExact = backup?.File is { } exportedFile && File.Exists(exportedFile) &&
                    File.ReadAllText(exportedFile).Contains("[" + app.RegistryKeyPath.Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\").Replace("HKCU\\", "HKEY_CURRENT_USER\\") + "]", StringComparison.OrdinalIgnoreCase);
                var records = database.GetHistoryPage(true, null, 10);
                var protectionRow = records.SingleOrDefault(row => row.Outcome == "QueueProtection");
                var removalRow = records.SingleOrDefault(row => row.AppId == app.Id);
                var registrationAfter = UninstallRegistration.Read(app.RegistryKeyPath).State;
                using var markerAfter = Registry.CurrentUser.OpenSubKey(markerKey);
                var warnings = new Dictionary<string, string>();
                foreach (var language in new[] { "en", "ar" })
                {
                    settings.LanguageCode = language;
                    warnings[language] = vm.ProtectionWarning;
                }
                var passed = backupBeforeLaunch && exportIsExact && report?.Restore == new ProtectionStep("Failed", "Permission") &&
                    backup?.Result.Status == "Succeeded" && vm.BatchState == BatchQueueState.Succeeded && vm.BatchSuccessful == 1 &&
                    vm.CanOpenSystemRestore && vm.IsProgressOpen && warnings.Values.All(text => !string.IsNullOrWhiteSpace(text)) && warnings["en"] != warnings["ar"] &&
                    protectionRow is not null && removalRow is { Success: true, ObservedExitCode: 0 } && protectionRow.Id < removalRow.Id &&
                    registrationAfter == UninstallRegistration.Presence.Absent && !File.Exists(payload) && markerAfter is null;
                File.WriteAllText(Path.Combine(reportDirectory, "protected-queue-result.json"), JsonSerializer.Serialize(new
                {
                    passed, nonElevated = true, backupBeforeLaunch, exportIsExact, protection = report, warnings, vm.CanOpenSystemRestore,
                    queue = vm.BatchState.ToString(), vm.BatchSuccessful, registrationAfter = registrationAfter.ToString(),
                    payloadExistsAfter = File.Exists(payload), markerExistsAfter = markerAfter is not null, history = records, progress = vm.ProgressLogs.ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(passed ? "Owned MSI protected queue passed." : "Owned MSI protected queue failed. See protected-queue-result.json.");
                application.Shutdown(passed ? 0 : 1);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                application.Shutdown(2);
            }
        };
        return application.Run();
    }
}
