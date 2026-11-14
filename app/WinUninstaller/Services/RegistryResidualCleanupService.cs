using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Only self-identifying, pre-uninstall captured product metadata is selectable.</summary>
internal static class RegistryResidualCleanupService
{
    private sealed record BackupManifest(string OriginalPath, string QuarantinePath, string AppName,
        DateTimeOffset CreatedUtc, string Kind, string Sha256, RegistryResidualEvidence Evidence);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static RegistryResidualEvidence? Capture(InstalledApp app)
    {
        try
        {
            if (app.Source != "Registry" || !UninstallBroker.IsRegistrationPath(app.RegistryKeyPath)) return null;
            var identity = new RegistryResidualIdentity(app.Name, app.Publisher, app.InstallLocation,
                app.RegistryKeyPath, app.Architecture);
            var view = app.Architecture == "32-bit" ? 32 : 64;
            if (!KeyExistsRegistration(app.RegistryKeyPath, view)) return null;
            var snapshot = Read(RegistryResidualPolicy.ExpectedPath(identity), view);
            if (snapshot is null || RegistryResidualPolicy.Evaluate(identity, snapshot) is not { } kind) return null;
            return new(identity, snapshot, kind, RegistryResidualPolicy.Fingerprint(snapshot));
        }
        catch { return null; }
    }

    internal static ResidualItem? Promote(InstalledApp app, IReadOnlyList<InstalledApp> queue,
        RegistryResidualEvidence? evidence)
    {
        if (evidence is null || !IdentityMatches(app, evidence.Identity) ||
            !StillEligible(app, queue, evidence)) return null;
        var path = evidence.Snapshot.Path + (evidence.Kind == RegistryResidualKind.Value ? @"\InstallLocation" : "") +
            $" [{evidence.Snapshot.View}-bit]";
        return new ResidualItem
        {
            Type = evidence.Kind == RegistryResidualKind.Key ? ResidualType.RegistryKey : ResidualType.RegistryValue,
            Path = path, Confidence = ConfidenceLevel.High, CanClean = true, IsSelected = false,
            Reason = "Captured before uninstall; explicit uninstall-key and install-root values match the recorded identity. Rechecked before cleanup.",
            RegistryEvidence = evidence
        };
    }

    internal static (ResidualScanService.PermanentDisposition Disposition, string? BackupPath) Clean(
        InstalledApp app, IReadOnlyList<InstalledApp> queue, ResidualItem item)
    {
        var evidence = item.RegistryEvidence;
        if (!item.CanClean || evidence is null || !IdentityMatches(app, evidence.Identity) ||
            !StillEligible(app, queue, evidence)) return (ResidualScanService.PermanentDisposition.Retained, null);
        string? manifestPath = null;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinUninstaller", "Quarantine", "Registry");
            if (!SafeBackupDirectory(root)) return (ResidualScanService.PermanentDisposition.Retained, null);
            Directory.CreateDirectory(root);
            if (!SafeBackupDirectory(root)) return (ResidualScanService.PermanentDisposition.Retained, null);
            var backupPath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".reg");
            manifestPath = backupPath + ".json";
            var bytes = RegistryResidualPolicy.BackupReg(evidence);
            DurableWrite(backupPath, bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var manifest = new BackupManifest(evidence.Snapshot.Path, backupPath, app.Name,
                DateTimeOffset.UtcNow, evidence.Kind == RegistryResidualKind.Key ? "RegistryKey" : "RegistryValue", hash, evidence);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
            if (manifestBytes.Length > 16_384)
            {
                File.Delete(backupPath); // This file was created by this attempt; no Registry write has occurred.
                return (ResidualScanService.PermanentDisposition.Retained, null);
            }
            DurableWrite(manifestPath, manifestBytes);
            // The backup must be durable and unchanged before any Registry write.
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backupPath))) != hash ||
                !StillEligible(app, queue, evidence))
                return (ResidualScanService.PermanentDisposition.Changed, manifestPath);
            if (!Delete(evidence)) return (ResidualScanService.PermanentDisposition.Inaccessible, manifestPath);
            return (ResidualScanService.PermanentDisposition.Deleted, manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or InvalidOperationException)
        { return (ResidualScanService.PermanentDisposition.Inaccessible, manifestPath); }
    }

    internal static ResidualScanService.RestoreResult Restore(string manifestPath)
    {
        try
        {
            var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinUninstaller", "Quarantine", "Registry"));
            var full = Path.GetFullPath(manifestPath);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !full.EndsWith(".reg.json", StringComparison.OrdinalIgnoreCase) || !SafeBackupDirectory(root) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(full).Length > 16_384) return new(false, "Registry backup manifest is outside the protected folder.");
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllBytes(full));
            if (manifest is null || manifest.Evidence is null ||
                !manifest.QuarantinePath.Equals(full[..^5], StringComparison.OrdinalIgnoreCase) ||
                !manifest.OriginalPath.Equals(manifest.Evidence.Snapshot.Path, StringComparison.OrdinalIgnoreCase) ||
                manifest.Kind != (manifest.Evidence.Kind == RegistryResidualKind.Key ? "RegistryKey" : "RegistryValue") ||
                RegistryResidualPolicy.Evaluate(manifest.Evidence.Identity, manifest.Evidence.Snapshot) != manifest.Evidence.Kind ||
                RegistryResidualPolicy.Fingerprint(manifest.Evidence.Snapshot) != manifest.Evidence.Fingerprint ||
                !File.Exists(manifest.QuarantinePath) ||
                (File.GetAttributes(manifest.QuarantinePath) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(manifest.QuarantinePath).Length > 32_768 ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest.QuarantinePath))) != manifest.Sha256 ||
                !File.ReadAllBytes(manifest.QuarantinePath).SequenceEqual(RegistryResidualPolicy.BackupReg(manifest.Evidence)))
                return new(false, "Registry backup identity or contents changed.");
            if (!RestoreValues(manifest.Evidence)) return new(false, "Registry restore target is occupied or changed.");
            return ResidualScanService.CompleteRestore(full, manifest.OriginalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { return new(false, ex.Message); }
    }

    private static bool IdentityMatches(InstalledApp app, RegistryResidualIdentity identity) =>
        app.Name == identity.Name && app.Publisher == identity.Publisher &&
        app.RegistryKeyPath.Equals(identity.RegistrationPath, StringComparison.OrdinalIgnoreCase) &&
        app.InstallLocation.Equals(identity.InstallRoot, StringComparison.OrdinalIgnoreCase) &&
        app.Architecture == identity.Architecture;

    private static bool StillEligible(InstalledApp app, IReadOnlyList<InstalledApp> queue, RegistryResidualEvidence evidence)
    {
        try
        {
            if (RegistryResidualPolicy.Evaluate(evidence.Identity, evidence.Snapshot) != evidence.Kind ||
                RegistryResidualPolicy.Fingerprint(evidence.Snapshot) != evidence.Fingerprint ||
                KeyExistsRegistration(app.RegistryKeyPath, evidence.Snapshot.View) ||
                queue.Any(other => other.Id != app.Id &&
                    (other.RegistryKeyPath.Equals(evidence.Snapshot.Path, StringComparison.OrdinalIgnoreCase) ||
                     Path.IsPathFullyQualified(other.InstallLocation) &&
                     PathsOverlap(other.InstallLocation, app.InstallLocation)))) return false;
            var current = Read(evidence.Snapshot.Path, evidence.Snapshot.View);
            if (current is null || RegistryResidualPolicy.Fingerprint(current) != evidence.Fingerprint ||
                !CanWriteTarget(evidence)) return false;
            var discovery = new RegistryDiscoveryService();
            var installed = discovery.DiscoverApps(includeMsix: true);
            if (discovery.LastDiagnostics.Any(source => source.Errors != 0 || source.SourceErrors != 0 || source.TimedOut || source.Cancelled))
                return false;
            return !installed.Any(other => !other.RegistryKeyPath.Equals(app.RegistryKeyPath, StringComparison.OrdinalIgnoreCase) &&
                (other.RegistryKeyPath.Equals(evidence.Snapshot.Path, StringComparison.OrdinalIgnoreCase) ||
                 Path.IsPathFullyQualified(other.InstallLocation) && PathsOverlap(other.InstallLocation, app.InstallLocation)));
        }
        catch { return false; }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanWriteTarget(RegistryResidualEvidence evidence)
    {
        var path = evidence.Snapshot.Path;
        using var root = OpenRoot(path, evidence.Snapshot.View);
        var relative = path[5..];
        if (evidence.Kind == RegistryResidualKind.Value)
        {
            using var key = root.OpenSubKey(relative, writable: true);
            return key is not null;
        }
        var separator = relative.LastIndexOf('\\');
        if (separator < 0) return false;
        using var parent = root.OpenSubKey(relative[..separator], writable: true);
        return parent is not null;
    }

    private static RegistryResidualSnapshot? Read(string path, int view)
    {
        using var root = OpenRoot(path, view);
        using var key = root.OpenSubKey(path[5..], writable: false);
        if (key is null) return null;
        var names = key.GetValueNames();
        if (names.Length > 32 || key.SubKeyCount > 256) return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stringsOnly = true;
        foreach (var name in names)
        {
            if (key.GetValueKind(name) != RegistryValueKind.String) { stringsOnly = false; continue; }
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value ||
                !values.TryAdd(name, value)) return null;
        }
        return new(path, view, values, key.SubKeyCount, stringsOnly);
    }

    private static bool KeyExists(string path, int view)
    {
        using var root = OpenRoot(path, view);
        using var key = root.OpenSubKey(path[5..], writable: false);
        return key is not null;
    }

    private static bool KeyExistsRegistration(string path, int candidateView) =>
        KeyExists(path, path.Contains("\\WOW6432Node\\", StringComparison.OrdinalIgnoreCase) ? 64 : candidateView);

    private static RegistryKey OpenRoot(string path, int view) => RegistryKey.OpenBaseKey(
        path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
        view == 32 ? RegistryView.Registry32 : RegistryView.Registry64);

    private static bool Delete(RegistryResidualEvidence evidence)
    {
        var snapshot = evidence.Snapshot;
        using var root = OpenRoot(snapshot.Path, snapshot.View);
        if (evidence.Kind == RegistryResidualKind.Value)
        {
            using var key = root.OpenSubKey(snapshot.Path[5..], writable: true);
            var current = Read(snapshot.Path, snapshot.View);
            if (key is null || current is null || RegistryResidualPolicy.Fingerprint(current) != evidence.Fingerprint) return false;
            key.DeleteValue("InstallLocation", throwOnMissingValue: true);
            return key.GetValueNames().All(name => !name.Equals("InstallLocation", StringComparison.OrdinalIgnoreCase));
        }
        var relative = snapshot.Path[5..];
        var separator = relative.LastIndexOf('\\');
        if (separator < 0) return false;
        using var parent = root.OpenSubKey(relative[..separator], writable: true);
        var beforeDelete = Read(snapshot.Path, snapshot.View);
        if (parent is null || beforeDelete is null || RegistryResidualPolicy.Fingerprint(beforeDelete) != evidence.Fingerprint)
            return false;
        parent.DeleteSubKey(relative[(separator + 1)..], throwOnMissingSubKey: true);
        return !KeyExists(snapshot.Path, snapshot.View);
    }

    private static bool RestoreValues(RegistryResidualEvidence evidence)
    {
        var snapshot = evidence.Snapshot;
        if (KeyExistsRegistration(evidence.Identity.RegistrationPath, snapshot.View)) return false;
        using var root = OpenRoot(snapshot.Path, snapshot.View);
        if (evidence.Kind == RegistryResidualKind.Value)
        {
            using var key = root.OpenSubKey(snapshot.Path[5..], writable: true);
            var current = Read(snapshot.Path, snapshot.View);
            if (key is null || current is null || !MatchesRegistryValueRestoreTarget(snapshot, current)) return false;
            key.SetValue("InstallLocation", snapshot.Values.First(pair => pair.Key.Equals("InstallLocation", StringComparison.OrdinalIgnoreCase)).Value,
                RegistryValueKind.String);
            return true;
        }
        if (KeyExists(snapshot.Path, snapshot.View)) return false;
        var relative = snapshot.Path[5..];
        var separator = relative.LastIndexOf('\\');
        if (separator < 0) return false;
        using var parent = root.OpenSubKey(relative[..separator], writable: true);
        if (parent is null) return false;
        const int keyWrite = 0x20006;
        const int createdNewKey = 1;
        var error = RegCreateKeyEx(parent.Handle, relative[(separator + 1)..], 0, IntPtr.Zero, 0, keyWrite,
            IntPtr.Zero, out var created, out var disposition);
        using var createdHandle = created;
        if (error != 0 || disposition != createdNewKey) return false;
        using var keyToRestore = RegistryKey.FromHandle(createdHandle, root.View);
        foreach (var pair in snapshot.Values) keyToRestore.SetValue(pair.Key, pair.Value, RegistryValueKind.String);
        return true;
    }

    internal static bool MatchesRegistryValueRestoreTarget(RegistryResidualSnapshot original, RegistryResidualSnapshot current)
    {
        if (!current.StringValuesOnly || original.SubkeyCount != current.SubkeyCount ||
            current.Values.Count != original.Values.Count - 1 || current.Values.Keys.Any(key => key.Equals("InstallLocation", StringComparison.OrdinalIgnoreCase))) return false;
        var remaining = new Dictionary<string, string>(original.Values, StringComparer.OrdinalIgnoreCase);
        return remaining.Remove("InstallLocation") &&
            RegistryResidualPolicy.Fingerprint(original with { Values = remaining }) == RegistryResidualPolicy.Fingerprint(current);
    }

    [DllImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyEx(SafeRegistryHandle parent, string subkey, int reserved, IntPtr keyClass,
        int options, int desiredAccess, IntPtr securityAttributes, out SafeRegistryHandle result, out int disposition);

    private static bool SafeBackupDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    private static void DurableWrite(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(bytes); file.Flush(flushToDisk: true);
    }
}
