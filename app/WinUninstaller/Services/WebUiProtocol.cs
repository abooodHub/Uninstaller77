using System.Text.Json;

namespace WinUninstaller.Services;

/// <summary>Strict envelope validation for the only command channel exposed to WebView2.</summary>
public sealed record WebUiRequest(string Id, string Operation, JsonElement Payload);

public static class WebUiProtocol
{
    public const int MaxMessageBytes = 64 * 1024;
    public const int MaxJsonNodes = 2048;
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    {
        "getState", "navigate", "refresh", "search", "sort", "category",
        "closeUnregistered", "confirmUnregistered", "retryUnregisteredDelete", "reviewPendingDeletion",
        "selectAll", "selectApp", "openDetails", "closeDetails", "promptUninstall",
        "cancelConfirm", "setUninstallOptions", "confirmUninstall", "cancelProgress",
        "closeProgress", "stopBatch", "runBatch", "selectResidual", "selectAllResiduals",
        "deselectAllResiduals", "skipResiduals", "deleteReviewedResiduals", "restoreQuarantine",
        "setLanguage", "setQueueProtection", "openSystemRestore", "loadHistory", "changeHistoryPage", "setShowTestRecords",
        "openHistoryDetails", "closeHistoryDetails", "clearHistory", "openSettings", "closeSettings", "openProductWebsite",
        "scanExtensions", "exportApps",
        "refreshControlCenter", "openControlFolder", "openWindowsAppsSettings", "exportSupportReport",
        "retryQueueVerification", "reviewQueue", "openQueueReview", "acknowledgeQueueReview", "inventoryRendered", "performanceMark"
    };

    public static bool IsTrustedSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               uri.Host.Equals("appassets.winuninstaller", StringComparison.OrdinalIgnoreCase) &&
               uri.UserInfo.Length == 0 &&
               uri.Port == 443 &&
               uri.Query.Length == 0 &&
               uri.Fragment.Length == 0 &&
               (uri.AbsolutePath.Equals("/index.html", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath == "/");
    }

    public static bool TryParse(string json, out WebUiRequest? request, out string error)
    {
        request = null;
        error = "Invalid request.";
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
        {
            error = "Message is empty or exceeds the size limit.";
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var nodes = 0;
            if (!ValidateJson(root, ref nodes))
            {
                error = "Request contains duplicate fields, invalid text, or too many values.";
                return false;
            }
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 3 || names.Distinct(StringComparer.Ordinal).Count() != 3 ||
                !names.Contains("id", StringComparer.Ordinal) || !names.Contains("op", StringComparer.Ordinal) ||
                !names.Contains("payload", StringComparer.Ordinal))
            {
                error = "Request envelope has unexpected or missing fields.";
                return false;
            }
            var idValue = root.GetProperty("id");
            var opValue = root.GetProperty("op");
            var payload = root.GetProperty("payload");
            if (idValue.ValueKind != JsonValueKind.String || opValue.ValueKind != JsonValueKind.String || payload.ValueKind != JsonValueKind.Object)
            {
                error = "Request fields have invalid types.";
                return false;
            }
            var id = idValue.GetString() ?? "";
            var operation = opValue.GetString() ?? "";
            if (id.Length is < 1 or > 64 || id.Any(character => !IsRequestIdCharacter(character)) ||
                operation.Length > 40 || !Operations.Contains(operation))
            {
                error = "Request id or operation is not authorized.";
                return false;
            }
            if (operation == "openProductWebsite" && payload.EnumerateObject().Any())
            {
                error = "The product website action does not accept parameters.";
                return false;
            }
            if (operation == "openSystemRestore" && payload.EnumerateObject().Any())
            {
                error = "The System Restore action does not accept parameters.";
                return false;
            }
            request = new WebUiRequest(id, operation, payload.Clone());
            error = "";
            return true;
        }
        catch (JsonException)
        {
            error = "Malformed JSON request.";
            return false;
        }
    }

    private static bool ValidateJson(JsonElement element, ref int nodes)
    {
        if (++nodes > MaxJsonNodes) return false;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || !IsWellFormedUtf16(property.Name) || !ValidateJson(property.Value, ref nodes))
                        return false;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (!ValidateJson(item, ref nodes)) return false;
                break;
            case JsonValueKind.String:
                if (!IsWellFormedUtf16(element.GetString() ?? string.Empty)) return false;
                break;
        }
        return true;
    }

    private static bool IsRequestIdCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' or ':';

    private static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
}
