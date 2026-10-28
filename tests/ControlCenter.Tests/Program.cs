using System.Text.Json;
using WinUninstaller.Services;

var passed = 0;
var failed = 0;
void Check(string name, Action verify)
{
    try { verify(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
void Refuses(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("An unauthorized request was accepted.");
}

var dataRoot = Path.GetFullPath("isolated-fixture-data");
var appRoot = Path.GetFullPath("isolated-fixture-app");
foreach (var folder in new[] { "Logs", "Quarantine", "Application" })
{
    Check("Exact folder destination " + folder, () =>
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { folder }));
        var token = ControlCenterService.ReadFolderRequest(doc.RootElement);
        var expected = folder == "Application" ? appRoot : Path.Combine(dataRoot, folder);
        Require(ControlCenterService.ResolveFolder(token, dataRoot, appRoot) == expected, "Destination escaped its configured root.");
    });
}
foreach (var folder in new[] { "..", "../Logs", @"Logs\..\..", "logs", @"C:\Windows", "Other" })
{
    Check("Reject folder token " + folder, () =>
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { folder }));
        Refuses(() => ControlCenterService.ReadFolderRequest(doc.RootElement));
        Refuses(() => ControlCenterService.ResolveFolder(folder, dataRoot, appRoot));
    });
}
foreach (var json in new[] { "{}", "[]", "null", "{\"folder\":1}", "{\"folder\":\"Logs\",\"path\":\"C:\\\\Windows\"}", "{\"folder\":\"Logs\",\"folder\":\"Application\"}" })
{
    Check("Reject malformed folder payload " + json, () =>
    {
        using var doc = JsonDocument.Parse(json);
        Refuses(() => ControlCenterService.ReadFolderRequest(doc.RootElement));
    });
}
foreach (var item in new[]
{
    (Accepted: 0, Errors: 0, SourceErrors: 0, TimedOut: false, Cancelled: false, Expected: "Completed"),
    (Accepted: 12, Errors: 1, SourceErrors: 0, TimedOut: false, Cancelled: false, Expected: "Partial"),
    (Accepted: 0, Errors: 0, SourceErrors: 1, TimedOut: false, Cancelled: false, Expected: "Failed"),
    (Accepted: 12, Errors: 0, SourceErrors: 1, TimedOut: false, Cancelled: false, Expected: "Partial"),
    (Accepted: 12, Errors: 0, SourceErrors: 0, TimedOut: true, Cancelled: false, Expected: "TimedOut"),
    (Accepted: 12, Errors: 0, SourceErrors: 0, TimedOut: false, Cancelled: true, Expected: "Cancelled"),
    (Accepted: 12, Errors: 1, SourceErrors: 1, TimedOut: true, Cancelled: true, Expected: "Cancelled")
})
    Check("Discovery state " + item.Expected + " " + item, () => Require(
        ControlCenterService.ClassifySource(item.Accepted, item.Errors, item.SourceErrors, item.TimedOut, item.Cancelled) == item.Expected,
        "A failed, interrupted or partial source was presented as completed."));

var runtime = new ControlCenterService.RuntimeInfo("1.1.8", ".NET fixture", "WebView fixture", "Windows fixture");
var queue = new ControlCenterService.QueueInfo(false, true, "NeedsUserAttention", 4, 2, 1, 1, 2);
var sources = new[] { new ControlCenterService.SourceInfo("HKCU/64-bit", 10, 2, 1, 0, false, false, 12, "Partial") };
foreach (var confirmed in new[] { true, false })
{
    Check("Inventory count confirmation " + confirmed, () =>
    {
        using var doc = JsonDocument.Parse(ControlCenterService.SerializeSupportReport(runtime, new(confirmed, "Loading", 23, sources), queue, 7, false));
        var count = doc.RootElement.GetProperty("inventory").GetProperty("applications");
        Require(confirmed ? count.GetInt32() == 23 : count.ValueKind == JsonValueKind.Null, "Unconfirmed inventory received a misleading count.");
    });
}
Check("Confirmed empty inventory remains zero", () =>
{
    using var doc = JsonDocument.Parse(ControlCenterService.SerializeSupportReport(runtime, new(true, "Empty", 0, []), queue, 0, false));
    Require(doc.RootElement.GetProperty("inventory").GetProperty("applications").GetInt32() == 0, "A genuine empty inventory was lost.");
});
Check("Support report preserves actual queue results and review state", () =>
{
    using var doc = JsonDocument.Parse(ControlCenterService.SerializeSupportReport(runtime, new(true, "Loaded", 23, sources), queue, 7, true));
    var result = doc.RootElement.GetProperty("queue");
    Require(result.GetProperty("successful").GetInt32() == 1 && result.GetProperty("failed").GetInt32() == 1 &&
        result.GetProperty("skipped").GetInt32() == 2 && result.GetProperty("processed").GetInt32() == 2 && result.GetProperty("needsReview").GetBoolean(),
        "Report changed outcomes or discarded unresolved status.");
    Require(doc.RootElement.GetProperty("includesTestHistory").GetBoolean(), "History scope was not disclosed.");
});
Check("Support report is a bounded metadata schema with UTC time", () =>
{
    var json = ControlCenterService.SerializeSupportReport(runtime, new(true, "Loaded", 23, sources), queue, 7, false);
    using var doc = JsonDocument.Parse(json);
    var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
    Require(keys.SetEquals(new[] { "schemaVersion", "createdUtc", "product", "runtime", "inventory", "queue", "visibleHistoryRecords", "includesTestHistory" }), "Unexpected report fields.");
    foreach (var forbidden in new[] { "appName", "uninstallCommand", "installationLocation", "messages", "logContents", "userName" })
        Require(!json.Contains('"' + forbidden + '"', StringComparison.Ordinal), "Private report field: " + forbidden);
    Require(DateTimeOffset.Parse(doc.RootElement.GetProperty("createdUtc").GetString()!).Offset == TimeSpan.Zero, "Report time was not UTC.");
});
foreach (var operation in new[] { "refreshControlCenter", "openControlFolder", "openWindowsAppsSettings", "exportSupportReport" })
    Check("Authorized bridge operation " + operation, () => Require(WebUiProtocol.TryParse(JsonSerializer.Serialize(new { id = "test", op = operation, payload = new { } }), out _, out _), "New operation was rejected."));
foreach (var operation in new[] { "shredPath", "createRestorePoint", "forceUninstall", "runShell", "cleanHealthCategory", "scanHealth" })
    Check("Unavailable bridge operation remains denied " + operation, () => Require(!WebUiProtocol.TryParse(JsonSerializer.Serialize(new { id = "test", op = operation, payload = new { } }), out _, out _), "A removed or dangerous action became available."));
Check("Windows Settings target is fixed", () => Require(ControlCenterService.WindowsAppsSettingsUri == "ms-settings:appsfeatures", "A dynamic URI was introduced."));

Console.WriteLine($"RESULT {passed} passed / {failed} failed; no user files, browser profiles, registry settings or uninstallers were changed.");
Environment.ExitCode = failed == 0 ? 0 : 1;
