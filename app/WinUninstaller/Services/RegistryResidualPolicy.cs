using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WinUninstaller.Services;

internal enum RegistryResidualKind { Key, Value }

internal sealed record RegistryResidualIdentity(string Name, string Publisher, string InstallRoot,
    string RegistrationPath, string Architecture);

internal sealed record RegistryResidualSnapshot(string Path, int View, Dictionary<string, string> Values,
    int SubkeyCount, bool StringValuesOnly);

internal sealed record RegistryResidualEvidence(RegistryResidualIdentity Identity,
    RegistryResidualSnapshot Snapshot, RegistryResidualKind Kind, string Fingerprint);

/// <summary>Fail-closed policy for a small self-identifying product key. No name-only candidate is eligible.</summary>
internal static class RegistryResidualPolicy
{
    private static readonly HashSet<string> Metadata = new(StringComparer.OrdinalIgnoreCase)
    { "InstallLocation", "UninstallRegistryKey", "DisplayName", "Publisher", "DisplayVersion" };

    internal static RegistryResidualKind? Evaluate(RegistryResidualIdentity identity, RegistryResidualSnapshot snapshot)
    {
        if (identity is null || snapshot is null || snapshot.Values is null ||
            string.IsNullOrWhiteSpace(snapshot.Path) || string.IsNullOrWhiteSpace(identity.RegistrationPath) ||
            snapshot.View is not (32 or 64) || !snapshot.StringValuesOnly || snapshot.SubkeyCount < 0 ||
            snapshot.Values.Count is < 2 or > 32 || !SafeSegment(identity.Name) || !SafeSegment(identity.Publisher) ||
            identity.RegistrationPath.Length > 512 || identity.InstallRoot.Length > 1024 ||
            ProtectedVendor(identity.Publisher) || !ValidRoot(identity.InstallRoot) ||
            !ValidRegistration(identity.RegistrationPath) ||
            !snapshot.Path.Equals(ExpectedPath(identity), StringComparison.OrdinalIgnoreCase)) return null;
        if (snapshot.Values.Sum(pair => pair.Key?.Length + pair.Value?.Length ?? 0) > 4096 ||
            snapshot.Values.Any(pair => pair.Key is null || pair.Value is null ||
            pair.Value.Length > 4096 || pair.Value.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
            pair.Key.Length > 256 || pair.Key.IndexOfAny(['\\', '\0', '\r', '\n']) >= 0)) return null;
        if (snapshot.Values.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Values.Count) return null;
        if (!ValueEquals(snapshot, "InstallLocation", identity.InstallRoot) ||
            !ValueEquals(snapshot, "UninstallRegistryKey", identity.RegistrationPath) ||
            TryValue(snapshot, "DisplayName", out var name) && !name.Equals(identity.Name, StringComparison.Ordinal) ||
            TryValue(snapshot, "Publisher", out var publisher) && !publisher.Equals(identity.Publisher, StringComparison.Ordinal))
            return null;
        // A whole key must have only installation metadata. Unknown settings or descendants may contain user data.
        return snapshot.SubkeyCount == 0 && snapshot.Values.Keys.All(Metadata.Contains)
            ? RegistryResidualKind.Key : RegistryResidualKind.Value;
    }

    internal static string Fingerprint(RegistryResidualSnapshot snapshot)
    {
        var canonical = new StringBuilder().Append(snapshot.Path.ToUpperInvariant()).Append('\n')
            .Append(snapshot.View).Append('\n').Append(snapshot.SubkeyCount).Append('\n').Append(snapshot.StringValuesOnly);
        foreach (var pair in snapshot.Values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            canonical.Append('\n').Append(pair.Key.ToUpperInvariant()).Append(':').Append(pair.Value.Length).Append(':').Append(pair.Value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    internal static byte[] BackupReg(RegistryResidualEvidence evidence)
    {
        if (Evaluate(evidence.Identity, evidence.Snapshot) != evidence.Kind ||
            Fingerprint(evidence.Snapshot) != evidence.Fingerprint) throw new InvalidOperationException("Registry ownership changed.");
        var full = evidence.Snapshot.Path.Replace("HKCU\\", "HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase)
            .Replace("HKLM\\", "HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase);
        var builder = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n[").Append(full).Append("]\r\n");
        var values = evidence.Kind == RegistryResidualKind.Key
            ? evidence.Snapshot.Values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            : evidence.Snapshot.Values.Where(pair => pair.Key.Equals("InstallLocation", StringComparison.OrdinalIgnoreCase));
        foreach (var pair in values)
            builder.Append('"').Append(Escape(pair.Key)).Append("\"=\"").Append(Escape(pair.Value)).Append("\"\r\n");
        return Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(builder.ToString())).ToArray();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static string ExpectedPath(RegistryResidualIdentity identity)
    {
        var hive = identity.RegistrationPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ? "HKCU" : "HKLM";
        return $@"{hive}\Software\{identity.Publisher}\{identity.Name}";
    }

    private static bool ValueEquals(RegistryResidualSnapshot snapshot, string name, string expected) =>
        TryValue(snapshot, name, out var value) && value.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryValue(RegistryResidualSnapshot snapshot, string name, out string value)
    {
        foreach (var pair in snapshot.Values)
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = pair.Value; return true; }
        value = "";
        return false;
    }

    private static bool ValidRegistration(string path)
    {
        const string user = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\";
        const string machine = @"HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\";
        const string user32 = @"HKCU\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";
        const string machine32 = @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\";
        var prefix = path.StartsWith(user, StringComparison.OrdinalIgnoreCase) ? user
            : path.StartsWith(machine, StringComparison.OrdinalIgnoreCase) ? machine
            : path.StartsWith(user32, StringComparison.OrdinalIgnoreCase) ? user32
            : path.StartsWith(machine32, StringComparison.OrdinalIgnoreCase) ? machine32 : "";
        return prefix.Length > 0 && path.Length > prefix.Length &&
            path[prefix.Length..].IndexOfAny(['\\', '\0', '\r', '\n']) < 0;
    }

    private static bool SafeSegment(string value) => !string.IsNullOrWhiteSpace(value) && value.Length is >= 3 and <= 100 &&
        value.IndexOfAny(['\\', '/', '[', ']', '"', '=', '\0', '\r', '\n']) < 0 && !value.EndsWith('.') && !value.EndsWith(' ');

    private static bool ProtectedVendor(string vendor) => vendor.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ||
        vendor.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ||
        vendor.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) ||
        vendor.Equals("Classes", StringComparison.OrdinalIgnoreCase);

    private static bool ValidRoot(string root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || root.Contains('~')) return false;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!root.TrimEnd('\\').Equals(full, StringComparison.OrdinalIgnoreCase) ||
                full.Split('\\').Any(segment => new[] { "Common Files", "WindowsApps", "Microsoft Shared", "Windows", "Microsoft" }
                    .Contains(segment, StringComparer.OrdinalIgnoreCase))) return false;
            var allowed = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) };
            return allowed.Any(basePath => !string.IsNullOrWhiteSpace(basePath) &&
                full.StartsWith(Path.TrimEndingDirectorySeparator(basePath) + "\\", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
