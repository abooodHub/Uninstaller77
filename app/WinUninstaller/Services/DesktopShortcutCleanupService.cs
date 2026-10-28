using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Removes only unchanged desktop links captured before a verified uninstall.
/// Never launches a shortcut, removes a target, scans recursively or requests elevation.</summary>
internal sealed class DesktopShortcutCleanupService
{
    internal sealed record Candidate(string Path, string Target, string Hash);
    internal sealed record Snapshot(string Registration, Candidate[] Candidates);
    internal sealed record Result(int Removed, int Kept);
    private readonly string[] _desktops;
    internal DesktopShortcutCleanupService() : this(new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }) { }
    internal DesktopShortcutCleanupService(IEnumerable<string> desktops) => _desktops = desktops.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal Snapshot Capture(InstalledApp app)
    {
        var found = new List<Candidate>();
        try
        {
            if (UninstallRegistration.Read(app.RegistryKeyPath).State != UninstallRegistration.Presence.Present) return new(app.RegistryKeyPath, []);
            var root = SafeRoot(app.InstallLocation);
            var exactTarget = ShellUninstallContextMenuService.GetDisplayIconExecutable(app.DisplayIcon);
            // A missing InstallLocation may use the exact primary icon EXE only,
            // never its parent directory as an inferred deletion scope.
            if (exactTarget is not null && (!exactTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || SafeRoot(Path.GetDirectoryName(exactTarget) ?? "") is null)) exactTarget = null;
            if (root is null && exactTarget is null) return new(app.RegistryKeyPath, []);
            foreach (var desktop in _desktops)
            {
                if (!Directory.Exists(desktop) || !NoReparse(desktop)) continue;
                foreach (var shortcut in Directory.EnumerateFiles(desktop, "*.lnk", SearchOption.TopDirectoryOnly).Take(1000))
                {
                    try
                    {
                        if (!NoReparse(shortcut) || new FileInfo(shortcut).Length > 1024 * 1024) continue;
                        var before = Hash(shortcut);
                        var link = ShellShortcutFile.Read(shortcut);
                        // A link with its own arguments (profile, flags, document) was customized
                        // by the user or vendor and is never an automatic cleanup candidate.
                        if (link is null || link.Arguments.Length > 0) continue;
                        var target = link.Target;
                        if ( !Path.IsPathFullyQualified(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !NoReparse(target)) continue;
                        if (!(root is not null && Within(target, root)) && !target.Equals(exactTarget, StringComparison.OrdinalIgnoreCase)) continue;
                        var attributes = File.GetAttributes(target); // Access denied is not absence.
                        if ((attributes & FileAttributes.Directory) != 0 || Hash(shortcut) != before) continue;
                        found.Add(new(shortcut, target, before));
                    }
                    catch { /* Unreadable/ambiguous links remain untouched. */ }
                }
            }
        }
        catch { /* Optional housekeeping cannot change the uninstall result. */ }
        return new(app.RegistryKeyPath, found.ToArray());
    }

    internal Result Complete(Snapshot snapshot, bool success, VerificationStatus verification)
    {
        if (!success || verification != VerificationStatus.VerifiedRemoved ||
            UninstallRegistration.Read(snapshot.Registration).State != UninstallRegistration.Presence.Absent) return new(0, snapshot.Candidates.Length);
        var removed = 0;
        var alreadyGone = 0;
        foreach (var link in snapshot.Candidates)
        {
            try
            {
                if (ConfirmedMissing(link.Path)) { alreadyGone++; continue; }
                if (!NoReparse(link.Path) || !NoReparse(link.Target) || !ConfirmedMissing(link.Target)) continue;
                // Pin the exact link object against replacement/write. Mark it for
                // deletion only after validating its original bytes and target.
                using var handle = CreateFile(link.Path, 0x80000000u | 0x10000u, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) continue;
                using var stream = new FileStream(handle, FileAccess.Read);
                if (stream.Length > 1024 * 1024 || Convert.ToHexString(SHA256.HashData(stream)) != link.Hash ||
                    !ConfirmedMissing(link.Target) || UninstallRegistration.Read(snapshot.Registration).State != UninstallRegistration.Presence.Absent) continue;
                var disposition = new Disposition { Delete = 1 };
                if (SetFileInformationByHandle(handle, 4, ref disposition, 1)) removed++;
            }
            catch { /* Locked/modified/inaccessible links are kept, without UAC. */ }
        }
        return new(removed, snapshot.Candidates.Length - removed - alreadyGone);
    }

    private static string Hash(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool ConfirmedMissing(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }
    private static bool Within(string path, string root) => path.StartsWith(root.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
    private static string? SafeRoot(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows) && (full.Equals(windows, StringComparison.OrdinalIgnoreCase) || Within(full, windows))) return null;
            var reserved = new[] { Path.GetPathRoot(full), Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") };
            if (reserved.Any(value => !string.IsNullOrWhiteSpace(value) && full.Equals(Path.TrimEndingDirectorySeparator(value), StringComparison.OrdinalIgnoreCase)) || !NoReparse(full)) return null;
            return full;
        }
        catch { return null; }
    }
    private static bool NoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch { return false; }
        }
        return true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { public byte Delete; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int type, ref Disposition info, uint size);
}
