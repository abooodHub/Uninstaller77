using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinUninstaller.Services;

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
Check(typeof(BrowserExtensionInventoryService).GetMethod("OpenExtensionManager") is null, "Shipping service cannot launch removed browser management");
Check(typeof(WinUninstaller.ViewModels.AppListViewModel).GetMethod("ToggleView") is null, "Shipping ViewModel excludes grid toggle");
foreach (var operation in new[] { "toggleView", "manageExtensionInBrowser", "setReduceMotion", "scanHealth", "scanMonitor", "shredPath", "forceUninstall" })
    Check(!WebUiProtocol.TryParse(JsonSerializer.Serialize(new { id = "integrity", op = operation, payload = new { } }), out _, out _), "Shipping bridge denies " + operation);

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
db.AddRecord(new() { AppName = "Owned integrity fixture", Success = false, ExitCode = 1603, Outcome = "Failed", Notes = "No process launched" });
var record = db.GetHistory().Single();
Check(!record.Success && record.ExitCode == 1603 && record.Outcome == "Failed", "History roundtrip preserves real failure fields");
Console.WriteLine($"{checks.Count} Passed / 0 Failed; isolated database retained at {dbPath}");
