using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace WinUninstaller.Services;

/// <summary>
/// Reads local Chromium extension manifests (Chrome, Edge, Brave, Opera, Vivaldi).
/// Read-only manifest inventory and policy classification. Does not remove profiles or rate trust.
/// </summary>
public static partial class BrowserExtensionInventoryService
{
    public sealed record ScanDiagnostic(string Browser, string Profile, string Stage, string Failure);
    public sealed record Extension(
        string Browser,
        string Profile,
        string Id,
        string Name,
        string Description,
        string Version,
        string? Icon,
        bool IsPolicyManaged,
        string InstallationLocation,
        string[] Permissions,
        string[] OptionalPermissions,
        string[] HostPermissions,
        string[] OptionalHostPermissions,
        string[] ContentScriptHosts,
        string EnabledState,
        string InstallationSource,
        string ManagementCapability
    );

    public static IReadOnlyList<Extension> ScanCurrentUser(Action<ScanDiagnostic>? diagnostic = null)
    {
        return ScanRoots(CurrentUserRoots(), diagnostic);
    }

    public static IReadOnlyList<string> DetectCurrentUserBrowsers() => DetectBrowsersFromRoots(CurrentUserRoots());

    public static IReadOnlyList<string> DetectBrowsersFromRoots(IEnumerable<(string Browser, string Root)> roots) =>
        roots.Where(entry => Directory.Exists(entry.Root))
             .Select(entry => entry.Browser)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .ToArray();

    private static (string Browser, string Root)[] CurrentUserRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return new[]
        {
            ("Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data")),
            ("Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")),
            ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")),
            ("Opera", Path.Combine(appData, "Opera Software", "Opera Stable")),
            ("Vivaldi", Path.Combine(local, "Vivaldi", "User Data"))
        };
    }

    public static IReadOnlyList<Extension> ScanRoots(IEnumerable<(string Browser, string Root)> roots, Action<ScanDiagnostic>? diagnostic = null)
    {
        var result = new List<Extension>();
        foreach (var (browser, root) in roots)
        {
            try { if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) continue; }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { diagnostic?.Invoke(new(browser, "", "BrowserRoot", ex.GetType().Name)); continue; }

            void Report(string profile, string stage, Exception error) => diagnostic?.Invoke(new(browser, profile, stage, error.GetType().Name));
            var profileDirs = SafeDirectories(root, error => Report("", "Profiles", error))
                .Where(p => Path.GetFileName(p).Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(p).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                .Take(21)
                .ToList();
            if (profileDirs.Count > 20)
            {
                diagnostic?.Invoke(new(browser, "", "Limit", "ProfileLimitExceeded"));
                profileDirs.RemoveAt(20);
            }

            // If Opera or single-profile root
            if (profileDirs.Count == 0 && Directory.Exists(Path.Combine(root, "Extensions")))
                profileDirs.Add(root);

            foreach (var profile in profileDirs)
            {
                var states = ReadEnabledStates(profile, error => Report(Path.GetFileName(profile), "Preferences", error));
                var folder = Path.Combine(profile, "Extensions");
                var extensionDirs = SafeDirectories(folder, error => Report(Path.GetFileName(profile), "Extensions", error)).Take(501).ToArray();
                if (extensionDirs.Length > 500) diagnostic?.Invoke(new(browser, Path.GetFileName(profile), "Limit", "ExtensionLimitExceeded"));
                foreach (var extension in extensionDirs.Take(500))
                {
                    var id = Path.GetFileName(extension);
                    // Chromium extension IDs are 32 lowercase alphabetic characters
                    if (id.Length != 32 || id.Any(c => c is < 'a' or > 'p')) continue;
                    var versionFolder = SafeDirectories(extension, error => Report(Path.GetFileName(profile), "Versions", error))
                        .Where(path => File.Exists(Path.Combine(path, "manifest.json")))
                        .OrderByDescending(path => Version.TryParse(Path.GetFileName(path).Split('_')[0], out var version) ? version : new Version(0, 0))
                        .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (versionFolder is null) continue;
                    try
                    {
                        var manifestPath = Path.Combine(versionFolder, "manifest.json");
                        if (!File.Exists(manifestPath)) continue;

                        using var doc = JsonDocument.Parse(ReadBoundedJson(manifestPath, 1_048_576),
                            new JsonDocumentOptions { MaxDepth = 20 });
                        var manifest = doc.RootElement;
                        if (manifest.ValueKind != JsonValueKind.Object) throw new JsonException("Manifest must be an object.");
                        foreach (var field in new[] { "name", "description", "version", "default_locale" })
                            if (manifest.TryGetProperty(field, out var metadata) && metadata.ValueKind != JsonValueKind.String)
                                throw new JsonException("Manifest metadata must be text.");

                        var name = manifest.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
                        var desc = manifest.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var version = manifest.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";

                        if (name.StartsWith("__MSG_", StringComparison.Ordinal))
                            name = ResolveLocalizedName(versionFolder, manifest, name) ?? id;
                        if (desc.StartsWith("__MSG_", StringComparison.Ordinal))
                            desc = ResolveLocalizedName(versionFolder, manifest, desc) ?? "";

                        var permissions = ReadStringArray(manifest, "permissions");
                        var hostPermissions = ReadStringArray(manifest, "host_permissions");
                        var optionalPermissions = ReadStringArray(manifest, "optional_permissions");
                        var optionalHostPermissions = ReadStringArray(manifest, "optional_host_permissions");
                        var contentScriptHosts = ReadContentScriptHosts(manifest);
                        var policyManaged = IsPolicyManaged(browser, id, error => Report(Path.GetFileName(profile), "Policy", error));
                        var enabledState = states.GetValueOrDefault(id, "Installed");
                        result.Add(new(
                            browser,
                            Path.GetFileName(profile),
                            id,
                            name,
                            desc,
                            version,
                            ReadIcon(versionFolder, manifest),
                            policyManaged,
                            versionFolder,
                            permissions,
                            optionalPermissions,
                            hostPermissions,
                            optionalHostPermissions,
                            contentScriptHosts,
                            enabledState,
                            policyManaged ? "Policy-managed" : "Unknown",
                            "Unsupported"
                        ));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or System.Security.SecurityException)
                    { Report(Path.GetFileName(profile), "Manifest", ex); }
                }
            }
        }
        return result.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static bool IsExtensionPolicyManaged(string extensionId, IEnumerable<string> forceListEntries, IEnumerable<string> configuredIds)
    {
        return forceListEntries.Any(entry => entry.Split(';', 2)[0].Trim().Equals(extensionId, StringComparison.OrdinalIgnoreCase)) ||
               configuredIds.Any(id => id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Read Chromium's persisted extension state without changing profile files.
    /// Modern profiles often omit the legacy state field; in that case a discovered
    /// manifest is reported as Installed rather than making an unsupported enabled claim.
    /// </summary>
    private static Dictionary<string, string> ReadEnabledStates(string profile, Action<Exception> diagnostic)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fileName in new[] { "Secure Preferences", "Preferences" })
        {
            var path = Path.Combine(profile, fileName);
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(ReadBoundedJson(path, 33_554_432), new JsonDocumentOptions { MaxDepth = 64 });
                if (!document.RootElement.TryGetProperty("extensions", out var extensions) ||
                    !extensions.TryGetProperty("settings", out var settings)) continue;
                foreach (var setting in settings.EnumerateObject())
                {
                    if (result.ContainsKey(setting.Name) || setting.Value.ValueKind != JsonValueKind.Object) continue;
                    var entry = setting.Value;
                    // Explicit disabling evidence wins over a stale legacy enabled field.
                    if (entry.TryGetProperty("disable_reasons", out var reasons) && HasDisableReason(reasons))
                        result[setting.Name] = "Disabled";
                    else if (entry.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Number && state.TryGetInt32(out var value))
                    {
                        if (value == 0) result[setting.Name] = "Disabled";
                        else if (value == 1) result[setting.Name] = "Enabled";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Security.SecurityException) { diagnostic(ex); }
        }

        return result;
    }

    private static bool HasDisableReason(JsonElement reasons) => reasons.ValueKind switch
    {
        JsonValueKind.Number => reasons.TryGetInt32(out var value) && value != 0,
        JsonValueKind.Array => reasons.EnumerateArray().Any(reason =>
            reason.ValueKind == JsonValueKind.Number && reason.TryGetInt32(out var value) && value != 0),
        JsonValueKind.Object => reasons.EnumerateObject().Any(reason =>
            reason.Value.ValueKind == JsonValueKind.Number && reason.Value.TryGetInt32(out var value) && value != 0),
        _ => false
    };

    private static bool IsPolicyManaged(string browser, string extensionId, Action<Exception> diagnostic)
    {
        var relativePolicyPath = browser switch
        {
            "Google Chrome" => @"Software\Policies\Google\Chrome",
            "Microsoft Edge" => @"Software\Policies\Microsoft\Edge",
            "Brave" => @"Software\Policies\BraveSoftware\Brave",
            _ => null
        };
        if (relativePolicyPath is null) return false;

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var policy = hive.OpenSubKey(relativePolicyPath);
                if (policy is null) continue;
                if (ReadPolicyKey(policy, extensionId, diagnostic)) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { diagnostic(ex); }
        }
        return false;
    }

    // Accepts a key so regression tests can use their own isolated HKCU fixture,
    // without writing to a real browser's policy/profile.
    public static bool ReadPolicyKey(RegistryKey policy, string extensionId, Action<Exception>? diagnostic = null)
    {
        using var forced = policy.OpenSubKey("ExtensionInstallForcelist");
        var entries = forced?.GetValueNames().Select(name => forced.GetValue(name)).OfType<string>() ?? [];
        if (IsExtensionPolicyManaged(extensionId, entries, [])) return true;
        using var expanded = policy.OpenSubKey($@"ExtensionSettings\{extensionId}");
        using var wildcard = policy.OpenSubKey(@"ExtensionSettings\*");
        if (expanded is not null || wildcard is not null) return true;
        if (policy.GetValue("ExtensionSettings") is not string json) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("ExtensionSettings must be an object.");
            return root.TryGetProperty(extensionId, out var configured) && configured.ValueKind == JsonValueKind.Object ||
                   root.TryGetProperty("*", out var all) && all.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException ex) { diagnostic?.Invoke(ex); return false; }
    }

    private static string? ResolveLocalizedName(string folder, JsonElement manifest, string token)
    {
        if (token.Length < 8) return null;
        var key = token[6..^2];
        var locale = manifest.TryGetProperty("default_locale", out var l) ? l.GetString() : "en";
        if (string.IsNullOrWhiteSpace(locale) || locale.Contains("..") || locale.Contains('/') || locale.Contains('\\')) return null;

        var file = Path.Combine(folder, "_locales", locale, "messages.json");
        if (!File.Exists(file))
        {
            // Fallback to English
            file = Path.Combine(folder, "_locales", "en", "messages.json");
            if (!File.Exists(file)) return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(ReadBoundedJson(file, 1_048_576), new JsonDocumentOptions { MaxDepth = 20 });
            foreach (var item in doc.RootElement.EnumerateObject())
                if (item.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && item.Value.TryGetProperty("message", out var msg))
                    return msg.GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    private static string? ReadIcon(string folder, JsonElement manifest)
    {
        if (!manifest.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Object) return null;
        foreach (var size in new[] { "48", "64", "32", "128", "16" })
        {
            if (!icons.TryGetProperty(size, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var relative = value.GetString() ?? "";
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains("..")) continue;
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var file = new FileInfo(path);
                var mimeType = file.Extension.ToLowerInvariant() switch
                {
                    ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => null
                };
                if (file.Exists && file.Length is > 0 and <= 256_000 && mimeType is not null)
                    return $"data:{mimeType};base64," + Convert.ToBase64String(File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static string[] ReadStringArray(JsonElement manifest, string property)
    {
        if (!manifest.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Take(128)
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(item => item.Length is > 0 and <= 256 && !item.Contains('\0'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] ReadContentScriptHosts(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("content_scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Array) return [];
        return scripts.EnumerateArray().Take(64)
            .Where(script => script.ValueKind == JsonValueKind.Object && script.TryGetProperty("matches", out _))
            .SelectMany(script => ReadStringArray(script, "matches"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
    }

    private static IEnumerable<string> SafeDirectories(string path, Action<Exception>? diagnostic = null)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException) { return Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { diagnostic?.Invoke(ex); return Array.Empty<string>(); }
    }

    // Bound the bytes read, including files that grow while the browser writes them.
    private static byte[] ReadBoundedJson(string path, int maximumBytes)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > maximumBytes) throw new IOException("Browser metadata exceeds the supported size limit.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, Math.Min(buffer.Length, maximumBytes + 1 - (int)output.Length))) > 0)
        {
            output.Write(buffer, 0, count);
            if (output.Length > maximumBytes) throw new IOException("Browser metadata exceeds the supported size limit.");
        }
        return output.ToArray();
    }
}
