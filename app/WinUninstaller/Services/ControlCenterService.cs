using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WinUninstaller.Services;

/// <summary>Fixed local destinations and a support report that excludes application identities and log contents.</summary>
public static class ControlCenterService
{
    public const string WindowsAppsSettingsUri = "ms-settings:appsfeatures";
    public sealed record RuntimeInfo(string AppVersion, string DotNetVersion, string WebViewVersion, string OperatingSystem);
    public sealed record SourceInfo(string Source, int Scanned, int Accepted, int Errors, int SourceErrors,
        bool TimedOut, bool Cancelled, long ElapsedMs, string Status);
    public sealed record InventoryInfo(bool Confirmed, string State, int? Applications, SourceInfo[] Sources);
    public sealed record QueueInfo(bool Running, bool NeedsReview, string State, int Total, int Processed,
        int Successful, int Failed, int Skipped);
    public sealed record SupportReport(int SchemaVersion, DateTimeOffset CreatedUtc, string Product,
        RuntimeInfo Runtime, InventoryInfo Inventory, QueueInfo Queue, int VisibleHistoryRecords, bool IncludesTestHistory);

    public static RuntimeInfo GetRuntimeInfo(string? webViewVersion) => new(
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "Unavailable",
        RuntimeInformation.FrameworkDescription,
        string.IsNullOrWhiteSpace(webViewVersion) ? "Unavailable" : webViewVersion,
        RuntimeInformation.OSDescription);

    public static string ClassifySource(int accepted, int errors, int sourceErrors, bool timedOut, bool cancelled)
    {
        if (cancelled) return "Cancelled";
        if (timedOut) return "TimedOut";
        if (sourceErrors > 0) return accepted > 0 ? "Partial" : "Failed";
        return errors > 0 ? "Partial" : "Completed";
    }

    public static string ReadFolderRequest(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid folder request.");
        var properties = payload.EnumerateObject().ToArray();
        if (properties.Length != 1 || properties[0].Name != "folder" || properties[0].Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Folder requests accept one named destination only.");
        var folder = properties[0].Value.GetString();
        if (folder is not ("Logs" or "Quarantine" or "Application" or "PendingRemoval"))
            throw new InvalidDataException("Folder destination is not authorized.");
        return folder;
    }

    public static string ResolveFolder(string folder, string dataRoot, string applicationRoot) => folder switch
    {
        "Logs" => Path.GetFullPath(Path.Combine(dataRoot, "Logs")),
        "Quarantine" => Path.GetFullPath(Path.Combine(dataRoot, "Quarantine")),
        "PendingRemoval" => Path.GetFullPath(Path.Combine(dataRoot, "PendingRemoval")),
        "Application" => Path.GetFullPath(applicationRoot),
        _ => throw new InvalidDataException("Folder destination is not authorized.")
    };

    public static string SerializeSupportReport(RuntimeInfo runtime, InventoryInfo inventory, QueueInfo queue,
        int visibleHistoryRecords, bool includesTestHistory)
    {
        var report = new SupportReport(1, DateTimeOffset.UtcNow, "Uninstaller 77", runtime,
            inventory with { Applications = inventory.Confirmed ? inventory.Applications : null },
            queue, visibleHistoryRecords, includesTestHistory);
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
    }
}
