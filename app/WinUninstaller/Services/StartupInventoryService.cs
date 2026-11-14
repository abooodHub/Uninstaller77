using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace WinUninstaller.Services;

internal static class StartupInventoryService
{
    internal const string SettingsUri = "ms-settings:startupapps";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal sealed record Entry(string Id, string Name, string Source, string Scope, string Location,
        string? Executable = null, string? Icon = null, string Status = "Unknown", string? ChangeToken = null);
    internal sealed record Inventory(IReadOnlyList<Entry> Items, int FailedSources)
    {
        internal string SnapshotId { get; } = Guid.NewGuid().ToString("N");
    }
    internal sealed record BatchResult(bool Enabled, int Changed, IReadOnlyList<string> Failed);
    private sealed record Registration(Entry Entry, string? Candidate);

    internal static Inventory Scan()
    {
        var registrations = new List<Registration>();
        var failedSources = 0;

        ReadSource(() => ReadRun(RegistryHive.CurrentUser, RegistryView.Default, "User", $@"HKCU\{RunKey}", registrations));
        if (Environment.Is64BitOperatingSystem)
        {
            ReadSource(() => ReadRun(RegistryHive.LocalMachine, RegistryView.Registry64, "Machine", $@"HKLM\{RunKey}", registrations));
            ReadSource(() => ReadRun(RegistryHive.LocalMachine, RegistryView.Registry32, "Machine",
                @"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", registrations));
        }
        else
        {
            ReadSource(() => ReadRun(RegistryHive.LocalMachine, RegistryView.Registry32, "Machine", $@"HKLM\{RunKey}", registrations));
        }

        ReadSource(() => ReadFolder(Environment.SpecialFolder.Startup, "User", registrations));
        ReadSource(() => ReadFolder(Environment.SpecialFolder.CommonStartup, "Machine", registrations));
        var icons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        return new Inventory(registrations.Select(registration => AddPresentation(registration, icons))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray(), failedSources);

        void ReadSource(Action read)
        {
            try { read(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                failedSources++;
            }
        }
    }

    private static void ReadRun(RegistryHive hive, RegistryView view, string scope, string location, List<Registration> registrations)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(RunKey, writable: false);
        if (key is null) return;

        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(name) ||
                key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command ||
                string.IsNullOrWhiteSpace(command)) continue;

            var (candidate, _) = RegisteredUninstallCommand.SplitCommand(Environment.ExpandEnvironmentVariables(command));
            var machine = scope == "Machine";
            var approvalSource = machine && view == RegistryView.Registry32 && Environment.Is64BitOperatingSystem ? "Run32" : "Run";
            var approval = StartupApproval.Read(approvalSource, name, RunIdentity(command), machine, canChange: !IsProtectedEntry(name, candidate));
            registrations.Add(new Registration(new Entry($"run:{location}:{name}", name, "Run", scope, location,
                Status: approval.Status, ChangeToken: approval.ChangeToken), candidate));
        }
    }

    private static void ReadFolder(Environment.SpecialFolder folder, string scope, List<Registration> registrations)
    {
        var location = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(location)) return;

        try
        {
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = false };
            foreach (var path in Directory.EnumerateFiles(location, "*", options))
            {
                if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var fileName = Path.GetFileName(path);
                var shortcut = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
                var machine = scope == "Machine";
                var identity = !machine && shortcut ? ShortcutIdentity(path) : null;
                var approval = StartupApproval.Read("StartupFolder", fileName, identity, machine,
                    canChange: !IsProtectedEntry(Path.GetFileNameWithoutExtension(fileName), identity is null ? null : ShellShortcutFile.ReadTarget(path)));
                registrations.Add(new Registration(new Entry($"folder:{scope}:{path}", shortcut ? Path.GetFileNameWithoutExtension(fileName) : fileName,
                    "StartupFolder", scope, path, Status: approval.Status, ChangeToken: approval.ChangeToken), path));
            }
        }
        catch (DirectoryNotFoundException)
        {
            // A Startup folder may not have been created yet.
        }
    }

    internal static void SetEnabled(Entry entry, string expectedToken, bool enabled)
    {
        if (entry.Scope != "User" || entry.ChangeToken is null ||
            !string.Equals(entry.ChangeToken, expectedToken, StringComparison.Ordinal))
            throw new InvalidOperationException("Startup entry changed or cannot be managed here.");

        if (entry.Source == "Run")
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command ||
                IsProtectedEntry(entry.Name, RegisteredUninstallCommand.SplitCommand(Environment.ExpandEnvironmentVariables(command)).exe))
                throw new InvalidOperationException("Startup registration changed.");
            StartupApproval.SetCurrentUser("Run", entry.Name, RunIdentity(command), expectedToken, enabled);
            return;
        }

        if (entry.Source != "StartupFolder") throw new InvalidOperationException("Unsupported startup source.");
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.DoNotVerify);
        var identity = ShortcutIdentity(entry.Location);
        var target = identity is null ? null : ShellShortcutFile.ReadTarget(entry.Location);
        if (string.IsNullOrEmpty(folder) || !string.Equals(folder, Path.GetDirectoryName(entry.Location), StringComparison.OrdinalIgnoreCase) ||
            identity is null || IsProtectedEntry(entry.Name, target)) throw new InvalidOperationException("Startup shortcut changed.");
        StartupApproval.SetCurrentUser("StartupFolder", Path.GetFileName(entry.Location), identity, expectedToken, enabled);
    }

    internal static Entry[] ResolveBatch(Inventory inventory, string snapshotId, IReadOnlyList<int> indices, bool enabled)
    {
        if (inventory.SnapshotId != snapshotId || indices.Count == 0 || indices.Distinct().Count() != indices.Count)
            throw new InvalidOperationException("Startup selection changed.");

        var entries = new Entry[indices.Count];
        for (var i = 0; i < indices.Count; i++)
        {
            var index = indices[i];
            if (index < 0 || index >= inventory.Items.Count) throw new InvalidOperationException("Invalid startup entry.");
            var entry = inventory.Items[index];
            if (entry.Scope != "User" || entry.ChangeToken is null || entry.Status != (enabled ? "Disabled" : "Enabled"))
                throw new InvalidOperationException("Startup entry changed or cannot be managed here.");
            entries[i] = entry;
        }
        return entries;
    }

    internal static BatchResult ApplyBatch(IReadOnlyList<Entry> entries, bool enabled,
        Action<Entry, string, bool> apply, Action<Exception> logFailure)
    {
        var failed = new List<string>();
        foreach (var entry in entries)
        {
            try { apply(entry, entry.ChangeToken!, enabled); }
            catch (Exception ex) when (IsChangeFailure(ex))
            {
                logFailure(ex);
                failed.Add(entry.Name);
            }
        }
        return new BatchResult(enabled, entries.Count - failed.Count, failed);
    }

    internal static bool IsChangeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException
        or InvalidOperationException or ArgumentException or NotSupportedException;

    private static string RunIdentity(string command) => "Run\0" + command;

    private static string? ShortcutIdentity(string path)
    {
        try
        {
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || LocalFile(path) is not { } file) return null;
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length <= 1024 * 1024 ? "StartupFolder\0" + Convert.ToHexString(SHA256.HashData(stream)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsProtectedEntry(string name, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate) ||
            !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(candidate).Equals("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return string.IsNullOrWhiteSpace(windows) || LocalFile(candidate) is not { } path ||
                path.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static Entry AddPresentation(Registration registration, Dictionary<string, string?> icons)
    {
        var entry = registration.Entry;
        try
        {
            var candidate = registration.Candidate;
            if (string.IsNullOrWhiteSpace(candidate)) return entry;
            if (entry.Source == "StartupFolder" && candidate.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shortcut = LocalFile(candidate);
                candidate = shortcut is not null && new FileInfo(shortcut).Length <= 1024 * 1024
                    ? ShellShortcutFile.ReadTarget(shortcut) : null;
            }

            if (candidate is null || !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return entry;
            var executable = LocalFile(candidate);
            if (executable is null) return entry;
            entry = entry with { Executable = executable };
            if (!icons.TryGetValue(executable, out var icon))
            {
                var bitmap = IconExtractionService.ExtractHighResFromPe(executable, 0);
                if (bitmap is not null)
                {
                    var scale = 64d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
                    var sized = scale < 1 ? new TransformedBitmap(bitmap, new ScaleTransform(scale, scale)) : bitmap;
                    icon = "data:image/png;base64," + Convert.ToBase64String(BitmapEncodingService.EncodePng(sized));
                }
                icons[executable] = icon;
            }
            return entry with { Icon = icon };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException
            or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
        {
            return entry;
        }
    }

    private static string? LocalFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetPathRoot(path) is not { Length: 3 } root ||
            path.AsSpan(2).Contains(':') || new DriveInfo(root).DriveType != DriveType.Fixed) return null;

        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        var attributes = FileAttributes.Directory;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
        }
        return (attributes & FileAttributes.Directory) == 0 ? full : null;
    }
}
