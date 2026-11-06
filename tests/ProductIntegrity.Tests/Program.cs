using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinUninstaller.Services;

if (args.Length == 2 && args[0] == "--native-protection-ui")
    return NativeProtectionUiCheck.Run(args[1]);

var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    checks.Add(name); Console.WriteLine("PASS " + name);
}
var assembly = typeof(UninstallService).Assembly;
foreach (var type in new[] { "SoftwareHealthEngine", "SoftwareHealthService", "AdvancedInstallMonitorService", "InstallMonitorService", "FileShredderService", "SystemRestoreService", "TestRunnerService" })
    Check(assembly.GetType("WinUninstaller.Services." + type) is null, "Shipping assembly excludes " + type);
Check(assembly.GetType("WinUninstaller.Models.InstallMonitorSession") is null, "Shipping assembly excludes monitor models");
var placeholderConvertBack = new WinUninstaller.Converters.NullImageConverter().ConvertBack(
    new object(), typeof(object), null!, System.Globalization.CultureInfo.InvariantCulture);
Check(ReferenceEquals(placeholderConvertBack, System.Windows.Data.Binding.DoNothing),
    "One-way image placeholder converter refuses reverse writes without throwing");
Check(typeof(BrowserExtensionInventoryService).GetMethod("OpenExtensionManager") is null, "Shipping service cannot launch removed browser management");
Check(typeof(WinUninstaller.ViewModels.AppListViewModel).GetMethod("ToggleView") is null, "Shipping ViewModel excludes grid toggle");
foreach (var method in new[] { "ShowAttentionFixtureAsync", "ShowSyntheticQueuePreview", "LoadSyntheticApps" })
    Check(typeof(WinUninstaller.ViewModels.AppListViewModel).GetMethod(method) is null, "Shipping ViewModel excludes " + method);
Check(typeof(UninstallService).GetProperty("BeforeFinalRegistrationValidationAsync",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is null,
    "Shipping uninstall service excludes the engine-only TOCTOU fixture hook");
Check(typeof(DatabaseService).GetField("BeforeSchemaCommitForTests",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static) is null,
    "Shipping database service excludes the engine-only migration failure hook");
foreach (var method in new[] { "RunVisualQaAsync", "RunPhase11QaAsync", "RunAttentionQaAsync" })
    Check(typeof(WinUninstaller.Views.WebMainWindow).GetMethod(method,
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is null, "Shipping window excludes " + method);
foreach (var operation in new[] { "toggleView", "manageExtensionInBrowser", "setReduceMotion", "scanHealth", "scanMonitor", "shredPath", "forceUninstall" })
    Check(!WebUiProtocol.TryParse(JsonSerializer.Serialize(new { id = "integrity", op = operation, payload = new { } }), out _, out _), "Shipping bridge denies " + operation);

Check(WebUiProtocol.TryParse("{\"id\":\"website\",\"op\":\"openProductWebsite\",\"payload\":{}}", out _, out _), "Product website action accepts an empty payload");
foreach (var payload in new[] { "{\"url\":\"https://example.invalid\"}", "{\"path\":\"C:\\\\Windows\\\\notepad.exe\"}", "{\"arguments\":\"--uninstall\"}", "{\"url\":\"\",\"url\":\"\"}" })
    Check(!WebUiProtocol.TryParse("{\"id\":\"website\",\"op\":\"openProductWebsite\",\"payload\":" + payload + "}", out _, out _), "Product website refuses supplied destinations and parameters: " + payload);
var websiteLaunch = ProductWebsite.CreateLaunchInfo();
Check(websiteLaunch.FileName == "https://abooodhub.github.io/Uninstaller77/" && websiteLaunch.UseShellExecute && websiteLaunch.Arguments.Length == 0 && websiteLaunch.ArgumentList.Count == 0 && websiteLaunch.Verb.Length == 0,
    "Product website launch uses only the fixed HTTPS page in the default browser without arguments or elevation");
Check(!WebUiProtocol.IsTrustedSource(ProductWebsite.Address), "Public website is not trusted to send desktop bridge commands");
Check(WebUiProtocol.TryParse("{\"id\":\"restore\",\"op\":\"openSystemRestore\",\"payload\":{}}", out _, out _) &&
    !WebUiProtocol.TryParse("{\"id\":\"restore\",\"op\":\"openSystemRestore\",\"payload\":{\"path\":\"other.exe\"}}", out _, out _),
    "System Restore launch accepts no executable, arguments or other payload properties");

var root = Path.Combine(Path.GetTempPath(), "Uninstaller77_ProductIntegrity_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "fixture.db");
var db = new DatabaseService(dbPath);
using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT sqlite_version();";
    var sqliteVersion = (string)command.ExecuteScalar()!;
    Check(Version.Parse(sqliteVersion) >= new Version(3, 50, 2), "Loaded native SQLite is patched: " + sqliteVersion);
    command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('LoggedInstallations','InstallMonitorSessions','InstallMonitorEvents');";
    Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "Fresh product database does not create removed feature tables");
    command.CommandText = "CREATE TABLE InstallMonitorSessions (Evidence TEXT); INSERT INTO InstallMonitorSessions VALUES ('historical evidence, do not drop');";
    command.ExecuteNonQuery();
}
_ = new DatabaseService(dbPath);
using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
{
    connection.Open(); using var command = connection.CreateCommand();
    command.CommandText = "SELECT Evidence FROM InstallMonitorSessions;";
    Check((string?)command.ExecuteScalar() == "historical evidence, do not drop", "Opening historical database preserves retired evidence tables");
}
db.AddRecord(new() { AppName = "Owned legacy fixture", Success = false, ExitCode = 1603, Outcome = "Failed", Notes = "Exit code: 1603" });
using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "ALTER TABLE UninstallHistory DROP COLUMN ExitObserved;";
    command.ExecuteNonQuery();
}
db = new DatabaseService(dbPath);
var record = db.GetHistory().Single();
Check(!record.Success && record.ExitCode == 1603 && record.Outcome == "Failed" && record.ExitObserved is null && record.ObservedExitCode == 1603,
    "History migration preserves legacy outcomes and exit evidence");
var launchedId = db.AddRecord(new() { AppName = "Owned exit fixture", ExitCode = -1, ExitObserved = true });
var unlaunchedId = db.AddRecord(new() { AppName = "Owned preflight fixture", ExitCode = -1, ExitObserved = false, Notes = "Exit code: -1" });
var history = db.GetHistory().ToDictionary(item => item.Id);
Check(history[launchedId].ObservedExitCode == -1 && history[unlaunchedId].ObservedExitCode is null,
    "Structured exit observation takes precedence over diagnostic text");
Console.WriteLine($"{checks.Count} Passed / 0 Failed; isolated database retained at {dbPath}");
return 0;
