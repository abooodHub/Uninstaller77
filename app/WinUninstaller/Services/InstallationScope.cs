using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace WinUninstaller.Services;

internal static class InstallationScope
{
    internal static (string Hive, int View)[] RegistryViews(bool is64Bit) => is64Bit
        ? [("HKCU", 0), ("HKLM", 32), ("HKLM", 64)] : [("HKCU", 0), ("HKLM", 32)];
    internal sealed record KeyStamp(string Hash, bool Linked, string? InstallLocation, string? Registration);

    internal static bool ValidDirectory(string root)
    {
        if (InstallationJournal.SamePath(root, Path.GetPathRoot(root))) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (InstallationJournal.SamePath(root, windows) || InstallationJournal.Within(root, windows)) return false;
        foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var known = Environment.GetFolderPath(folder);
            if (known.Length > 0 && (InstallationJournal.SamePath(root, known) || InstallationJournal.Within(known, root))) return false;
        }
        var ownData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller");
        return !InstallationJournal.SamePath(root, ownData) && !InstallationJournal.Within(root, ownData);
    }

    internal static bool Valid(string scope)
    {
        if (scope.Length > 192) return false;
        var parts = scope.Split('\\');
        return parts.Length is >= 3 and <= 5 && parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase) &&
            parts.Skip(1).All(part => part.Length is > 0 and <= 64 && char.IsLetterOrDigit(part[0]) &&
                part.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')) &&
            !new[] { "Microsoft", "Classes", "Policies", "WOW6432Node", "Windows", "Clients" }.Contains(parts[1], StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsRegistryPathInScope(string path, string scope) =>
        (path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)) &&
        (path[5..].Equals(scope, StringComparison.OrdinalIgnoreCase) || path[5..].StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase));

    internal static KeyStamp? ReadKey(string path, int view, MonitoredRegistration? identity)
    {
        var user = path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase);
        if (view is not (0 or 32 or 64) || view == 0 && !user) return null;
        using var hive = RegistryKey.OpenBaseKey(user ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            view == 0 ? RegistryView.Default : view == 32 ? RegistryView.Registry32 : RegistryView.Registry64);
        using var key = hive.OpenSubKey(path[5..], writable: false);
        if (key is null) return null;
        var before = UninstallRegistration.ReadLastWrite(key.Handle);
        var names = key.GetValueNames();
        if (names.Length > 128) throw new IOException("Registry value limit reached.");
        if (key.SubKeyCount > 1_200) throw new IOException("Registry subkey limit reached.");
        var fields = names.Order(StringComparer.OrdinalIgnoreCase).Select(name => new
        { Name = name, Kind = key.GetValueKind(name), Value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { LastWrite = before, Subkeys = key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase), Values = fields });
        if (bytes.Length > 262_144) throw new IOException("Registry data limit reached.");
        var location = key.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var registration = key.GetValue("UninstallRegistryKey", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (before is null || UninstallRegistration.ReadLastWrite(key.Handle) != before) throw new IOException("Registry key changed during capture.");
        var linked = identity is not null && InstallationJournal.SamePath(location, identity.Root) &&
            string.Equals(registration, identity.Key, StringComparison.OrdinalIgnoreCase);
        return new(Convert.ToHexString(SHA256.HashData(bytes)), linked, location, registration);
    }
}
