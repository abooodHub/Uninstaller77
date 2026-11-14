using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class ResidualScanService
{
    internal static bool CanReviewPermanentFiles(InstalledApp app, IReadOnlyList<InstalledApp> queue)
    {
        try
        {
            var root = app.InstallLocation;
            if (!Path.IsPathFullyQualified(root)) return false;
            root = Path.GetFullPath(root);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var sharedRoots = new[] { Path.GetTempPath(), Path.Combine(local, "Programs"), Path.Combine(local, "Packages"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData") };
            return !sharedRoots.Any(path => SamePath(root, path)) &&
                !queue.Any(other => other.Id != app.Id && Path.IsPathFullyQualified(other.InstallLocation) &&
                    Overlaps(root, Path.GetFullPath(other.InstallLocation)));
        }
        catch (ArgumentException) { return false; }
    }

    internal static IReadOnlyList<ResidualItem> ToPermanentReview(InstalledApp app, ResidualScanResult scan, IReadOnlyList<InstalledApp> queue,
        DesktopShortcutCleanupService.Snapshot? shortcuts = null, RegistryResidualEvidence? registryEvidence = null)
    {
        var allowFiles = CanReviewPermanentFiles(app, queue);
        var result = new List<ResidualItem>();
        foreach (var item in scan.Items)
        {
            if (!allowFiles || item.ReviewedFiles is not { Count: > 0 } files)
            {
                item.CanClean = false;
                item.IsSelected = false;
                result.Add(item);
                continue;
            }
            result.AddRange(files.Select(file => new ResidualItem
            {
                Path = Path.Combine(item.Path, file.RelativePath), Type = ResidualType.File,
                SizeBytes = file.Bytes, Confidence = ConfidenceLevel.Medium, ReviewedFingerprint = file.Sha256,
                CanClean = true, IsSelected = false
            }));
        }
        if (scan.Completed && shortcuts is not null && shortcuts.Registration.Equals(app.RegistryKeyPath, StringComparison.OrdinalIgnoreCase) &&
            CanReviewShortcuts(app, queue))
        {
            foreach (var link in shortcuts.Candidates)
            {
                if (!IsDesktopShortcut(link.Path) || !IsTargetInFormerRoot(link.Target, app.InstallLocation) ||
                    !ConfirmedMissingFile(link.Target) || !File.Exists(link.Path)) continue;
                long linkBytes;
                try { linkBytes = new FileInfo(link.Path).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                result.RemoveAll(item => item.Type == ResidualType.Shortcut &&
                    item.Path.Equals(link.Path, StringComparison.OrdinalIgnoreCase));
                result.Add(new ResidualItem
                {
                    Type = ResidualType.Shortcut, Path = link.Path, SizeBytes = linkBytes,
                    Confidence = ConfidenceLevel.High,
                    Reason = "Captured before uninstall: unchanged desktop shortcut with no arguments and a target inside the former install directory.",
                    ReviewedFingerprint = link.Hash, ReviewedTarget = link.Target, CanClean = true, IsSelected = false
                });
            }
        }
        if (scan.Completed && RegistryResidualCleanupService.Promote(app, queue, registryEvidence) is { } registryItem)
        {
            if (registryItem.Type == ResidualType.RegistryKey)
                result.RemoveAll(item => item.Type == ResidualType.RegistryKey &&
                    item.Path.Replace(" [64-bit]", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(" [32-bit]", "", StringComparison.OrdinalIgnoreCase)
                        .Equals(registryEvidence!.Snapshot.Path, StringComparison.OrdinalIgnoreCase));
            result.Add(registryItem);
        }
        return result;
    }

    internal enum PermanentDisposition { Deleted, Retained, Changed, Inaccessible }
    internal sealed record PermanentAttempt(ResidualItem Item, PermanentDisposition Disposition, string? BackupPath = null);
    internal sealed record PermanentResult(int Deleted, long Bytes, IReadOnlyList<ResidualItem> Kept,
        IReadOnlyList<PermanentAttempt> Attempts);

    private static bool CanReviewShortcuts(InstalledApp app, IReadOnlyList<InstalledApp> queue)
    {
        try
        {
            if (!CanReviewPermanentFiles(app, queue) || !Path.IsPathFullyQualified(app.InstallLocation) ||
                IsReservedPath(app.InstallLocation) ||
                ProtectedRoots.Any(root => SamePath(root, app.InstallLocation)) ||
                ProtectedTrees.Any(root => Path.IsPathFullyQualified(root) && Overlaps(root, app.InstallLocation)) ||
                UninstallRegistration.Read(app.RegistryKeyPath).State != UninstallRegistration.Presence.Absent)
                return false;
            var discovery = new RegistryDiscoveryService();
            var inventory = discovery.DiscoverApps(includeMsix: false);
            if (discovery.LastDiagnostics.Any(source => source.Errors != 0 || source.SourceErrors != 0 || source.TimedOut || source.Cancelled))
                return false;
            return !inventory.Any(other => !other.RegistryKeyPath.Equals(app.RegistryKeyPath, StringComparison.OrdinalIgnoreCase) &&
                Path.IsPathFullyQualified(other.InstallLocation) && Overlaps(other.InstallLocation, app.InstallLocation));
        }
        catch { return false; }
    }

    private static bool IsDesktopShortcut(string path)
    {
        try
        {
            return IsDesktopShortcutLocation(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }

    private static bool IsDesktopShortcutLocation(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            return parent is not null && new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }.Any(root => SamePath(root, parent)) &&
                !HasReparsePointInPath(parent);
        }
        catch { return false; }
    }

    private static bool ConfirmedMissingFile(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

    private static bool IsTargetInFormerRoot(string target, string root)
    {
        try
        {
            if (!Path.IsPathFullyQualified(target) || !Path.IsPathFullyQualified(root) ||
                !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            var fullTarget = Path.GetFullPath(target);
            return !IsReservedPath(fullTarget) && IsWithin(fullTarget, Path.GetFullPath(root));
        }
        catch { return false; }
    }

    private static (PermanentDisposition Disposition, string? BackupPath) TryDeleteReviewedShortcut(
        InstalledApp app, IReadOnlyList<InstalledApp> queue, ResidualItem item)
    {
        if (!item.CanClean || item.ReviewedFingerprint is null || item.ReviewedTarget is null ||
            !IsDesktopShortcut(item.Path) || !IsTargetInFormerRoot(item.ReviewedTarget, app.InstallLocation) ||
            !CanReviewShortcuts(app, queue) || !ConfirmedMissingFile(item.ReviewedTarget))
            return (PermanentDisposition.Retained, null);
        string? backup = null, manifest = null;
        var deleted = false;
        try
        {
            var link = ShellShortcutFile.Read(item.Path);
            if (link is null || link.Arguments.Length != 0 || !SamePath(link.Target, item.ReviewedTarget))
                return (PermanentDisposition.Changed, null);
            const uint genericRead = 0x80000000, delete = 0x10000, shareRead = 1, openExisting = 3, openReparsePoint = 0x00200000;
            using var handle = CreateFile(item.Path, genericRead | delete, shareRead, IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) return (PermanentDisposition.Inaccessible, null);
            if (!GetFileInformationByHandle(handle, out var info) || info.LinkCount != 1 ||
                (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !HasExactOpenedPath(handle, Path.GetFullPath(item.Path)))
                return (PermanentDisposition.Changed, null);
            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length != item.SizeBytes || stream.Length > 1024 * 1024 ||
                Convert.ToHexString(SHA256.HashData(stream)) != item.ReviewedFingerprint)
                return (PermanentDisposition.Changed, null);
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinUninstaller", "Quarantine");
            if (HasReparsePointInPath(Path.GetDirectoryName(root)!) || Directory.Exists(root) && HasReparsePointInPath(root))
                return (PermanentDisposition.Retained, null);
            Directory.CreateDirectory(root);
            backup = Path.Combine(root, Guid.NewGuid().ToString("N") + ".lnk");
            manifest = backup + ".json";
            stream.Position = 0;
            using (var saved = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.CopyTo(saved); saved.Flush(flushToDisk: true); }
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backup))) != item.ReviewedFingerprint)
                return (PermanentDisposition.Inaccessible, null);
            var manifestBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new QuarantineManifest(item.Path, backup, app.Name,
                DateTimeOffset.UtcNow, "Shortcut", item.ReviewedFingerprint), ManifestJsonOptions));
            if (manifestBytes.Length > 16_384) return (PermanentDisposition.Retained, null);
            using (var savedManifest = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { savedManifest.Write(manifestBytes); savedManifest.Flush(flushToDisk: true); }
            if (!ConfirmedMissingFile(item.ReviewedTarget) ||
                UninstallRegistration.Read(app.RegistryKeyPath).State != UninstallRegistration.Presence.Absent ||
                !HasExactOpenedPath(handle, Path.GetFullPath(item.Path)))
                return (PermanentDisposition.Changed, null);
            byte disposition = 1;
            if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) return (PermanentDisposition.Inaccessible, null);
            deleted = true;
            return (PermanentDisposition.Deleted, manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return (PermanentDisposition.Inaccessible, null); }
        finally
        {
            // A backup is retained only when its matching manifest describes an actual deletion.
            if (!deleted && backup is not null && manifest is not null)
            {
                try { if (File.Exists(manifest)) File.Delete(manifest); if (File.Exists(backup)) File.Delete(backup); }
                catch { /* An orphaned backup is safer than deleting an unverified shortcut. */ }
            }
        }
    }

    internal static Task<PermanentResult> DeleteReviewedFilesAsync(InstalledApp app, IReadOnlyList<InstalledApp> queue,
        IReadOnlyList<ResidualItem> items, IProgress<(int Processed, string Path)>? progress = null,
        CancellationToken stop = default) => Task.Run(() =>
    {
        var kept = new List<ResidualItem>();
        var attempts = new List<PermanentAttempt>();
        long bytes = 0;
        if (!CanReviewPermanentFiles(app, queue))
            return new PermanentResult(0, 0, items,
                items.Select(item => new PermanentAttempt(item, PermanentDisposition.Retained)).ToArray());

        foreach (var item in items)
        {
            if (stop.IsCancellationRequested) break;
            progress?.Report((attempts.Count, item.Path));
            var (disposition, backup) = item.Type switch
            {
                ResidualType.Shortcut => TryDeleteReviewedShortcut(app, queue, item),
                ResidualType.RegistryKey or ResidualType.RegistryValue => RegistryResidualCleanupService.Clean(app, queue, item),
                _ => (IsEligibleInstallDirectory(app.InstallLocation, app) ? TryDeleteReviewedFile(app, item) : PermanentDisposition.Retained, null)
            };
            attempts.Add(new(item, disposition, backup));
            if (disposition == PermanentDisposition.Deleted) bytes += item.SizeBytes;
            else kept.Add(item);
        }
        return new PermanentResult(attempts.Count - kept.Count, bytes, kept, attempts);
    });

    private static PermanentDisposition TryDeleteReviewedFile(InstalledApp app, ResidualItem item)
    {
        if (item.Type != ResidualType.File || !item.CanClean || item.ReviewedFingerprint is null)
            return PermanentDisposition.Retained;
        try
        {
            var path = Path.GetFullPath(item.Path);
            var root = Path.GetFullPath(app.InstallLocation);
            if (!IsWithin(path, root) || Path.GetRelativePath(root, path).Contains(':'))
                return PermanentDisposition.Retained;
            if (HasReparsePointInPath(path)) return PermanentDisposition.Changed;

            const uint genericRead = 0x80000000, delete = 0x10000, shareRead = 1, openExisting = 3, openReparsePoint = 0x00200000;
            using var handle = CreateFile(path, genericRead | delete, shareRead, IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                return error is 2 or 3 ? PermanentDisposition.Changed : PermanentDisposition.Inaccessible;
            }
            if (!GetFileInformationByHandle(handle, out var info)) return PermanentDisposition.Inaccessible;
            if (info.LinkCount != 1 || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                !HasExactOpenedPath(handle, path)) return PermanentDisposition.Changed;

            using var stream = new FileStream(handle, FileAccess.Read);
            if (stream.Length != item.SizeBytes || Convert.ToHexString(SHA256.HashData(stream)) != item.ReviewedFingerprint ||
                !HasExactOpenedPath(handle, path)) return PermanentDisposition.Changed;
            if (UninstallRegistration.Read(app.RegistryKeyPath).State != UninstallRegistration.Presence.Absent)
                return PermanentDisposition.Retained;

            // Mark the object we actually opened. A path-based delete could target a replacement.
            byte disposition = 1;
            return SetFileInformationByHandle(handle, 4, ref disposition, 1)
                ? PermanentDisposition.Deleted : PermanentDisposition.Inaccessible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return PermanentDisposition.Inaccessible; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { return PermanentDisposition.Retained; }
    }

    private static bool HasExactOpenedPath(SafeFileHandle handle, string expected)
    {
        var path = new StringBuilder(expected.Length + 16);
        var length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
        return length > 0 && length < path.Capacity &&
            string.Equals(path.ToString(), @"\\?\" + expected, StringComparison.OrdinalIgnoreCase);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "GetFinalPathNameByHandleW writes a normalized path into a bounded mutable UTF-16 buffer before identity checks.")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, ref byte info, uint size);
}
