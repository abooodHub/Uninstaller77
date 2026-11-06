using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    internal static IReadOnlyList<ResidualItem> ToPermanentReview(InstalledApp app, ResidualScanResult scan, IReadOnlyList<InstalledApp> queue)
    {
        var allowFiles = CanReviewPermanentFiles(app, queue);
        var result = new List<ResidualItem>();
        foreach (var item in scan.Items)
        {
            if (!allowFiles || item.ReviewedFiles is not { Count: > 0 } files)
            {
                item.CanClean = false;
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
        return result;
    }

    internal enum PermanentDisposition { Deleted, Retained, Changed, Inaccessible }
    internal sealed record PermanentAttempt(ResidualItem Item, PermanentDisposition Disposition);
    internal sealed record PermanentResult(int Deleted, long Bytes, IReadOnlyList<ResidualItem> Kept,
        IReadOnlyList<PermanentAttempt> Attempts);

    internal static Task<PermanentResult> DeleteReviewedFilesAsync(InstalledApp app, IReadOnlyList<InstalledApp> queue,
        IReadOnlyList<ResidualItem> items) => Task.Run(() =>
    {
        var kept = new List<ResidualItem>();
        var attempts = new List<PermanentAttempt>();
        long bytes = 0;
        if (!CanReviewPermanentFiles(app, queue) || !IsEligibleInstallDirectory(app.InstallLocation, app))
            return new PermanentResult(0, 0, items,
                items.Select(item => new PermanentAttempt(item, PermanentDisposition.Retained)).ToArray());

        foreach (var item in items)
        {
            var disposition = TryDeleteReviewedFile(app, item);
            attempts.Add(new(item, disposition));
            if (disposition == PermanentDisposition.Deleted) bytes += item.SizeBytes;
            else kept.Add(item);
        }
        return new PermanentResult(items.Count - kept.Count, bytes, kept, attempts);
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
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, ref byte info, uint size);
}
