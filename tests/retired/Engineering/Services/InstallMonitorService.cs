using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>
/// Manual before/after observations. A difference does not establish installer ownership.
/// </summary>
public class InstallMonitorService
{
    private readonly DatabaseService _db;

    public sealed record Snapshot(
        DateTime Timestamp,
        HashSet<string> RegistryKeys,
        HashSet<string> Directories
    );

    public sealed record InstallationDiff(
        string AppName,
        string InstallerPath,
        DateTime StartTime,
        DateTime EndTime,
        List<string> NewRegistryKeys,
        List<string> NewDirectories,
        long TotalBytesAdded
    );

    private Snapshot? _currentBaseline;
    private string? _monitoredAppName;
    private string? _installerPath;

    public bool IsMonitoring => _currentBaseline != null;
    public string? MonitoredAppName => _monitoredAppName;

    public InstallMonitorService(DatabaseService db)
    {
        _db = db;
    }

    public static IReadOnlyList<InstalledApp> MatchCurrentRegistrations(DatabaseService.LoggedInstallation snapshot,
        IEnumerable<InstalledApp> inventory)
    {
        try
        {
            var paths = (JsonSerializer.Deserialize<string[]>(snapshot.TrackedRegistryJson) ?? [])
                .Select(NormalizeRegistration).Where(path => path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return inventory.Where(app => app.Source == "Registry" &&
                NormalizeRegistration(app.RegistryKeyPath) is { } path && paths.Contains(path)).ToArray();
        }
        catch (JsonException) { return Array.Empty<InstalledApp>(); }
    }

    private static string? NormalizeRegistration(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Replace("HKEY_LOCAL_MACHINE\\", "HKLM\\", StringComparison.OrdinalIgnoreCase)
            .Replace("HKEY_CURRENT_USER\\", "HKCU\\", StringComparison.OrdinalIgnoreCase).TrimEnd('\\');
        foreach (var hive in new[] { "HKCU", "HKLM" })
        foreach (var root in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall\", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" })
        {
            var prefix = hive + "\\" + root;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length && !path[prefix.Length..].Contains('\\')) return path;
        }
        return null;
    }

    /// <summary>
    /// Begins tracking by creating a baseline snapshot of key registry and filesystem locations.
    /// </summary>
    public void StartMonitoring(string appName, string? installerPath = null)
    {
        _monitoredAppName = appName;
        _installerPath = installerPath;
        _currentBaseline = CaptureSnapshot();
    }

    /// <summary>
    /// Completes the monitoring session, computes the exact delta, and stores the log into SQLite.
    /// </summary>
    public InstallationDiff FinishMonitoring()
    {
        if (_currentBaseline == null)
            throw new InvalidOperationException("No monitoring session is currently active.");

        var postSnapshot = CaptureSnapshot();
        var startTime = _currentBaseline.Timestamp;
        var endTime = postSnapshot.Timestamp;

        // Diff registry keys
        var newKeys = postSnapshot.RegistryKeys
            .Except(_currentBaseline.RegistryKeys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k)
            .ToList();

        // Diff directories
        var newDirs = postSnapshot.Directories
            .Except(_currentBaseline.Directories, StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d)
            .ToList();

        long bytesAdded = 0;
        foreach (var dir in newDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    bytesAdded += Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                }
            }
            catch { }
        }

        var diff = new InstallationDiff(
            _monitoredAppName ?? "Unknown Application",
            _installerPath ?? "",
            startTime,
            endTime,
            newKeys,
            newDirs,
            bytesAdded
        );

        // Persist to database
        var filesJson = JsonSerializer.Serialize(newDirs);
        var regJson = JsonSerializer.Serialize(newKeys);
        _db.AddLoggedInstallation(
            diff.AppName,
            diff.InstallerPath,
            filesJson,
            regJson,
            $"Observed differences: {newDirs.Count} top-level directories, {newKeys.Count} registry keys. Ownership is not established."
        );

        _currentBaseline = null;
        _monitoredAppName = null;
        _installerPath = null;

        return diff;
    }

    public void CancelMonitoring()
    {
        _currentBaseline = null;
        _monitoredAppName = null;
        _installerPath = null;
    }

    private static Snapshot CaptureSnapshot()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Registry Roots
        CaptureRegistrySubkeys(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", keys);
        CaptureRegistrySubkeys(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", keys);
        CaptureRegistrySubkeys(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall", keys);
        CaptureRegistrySubkeys(Registry.LocalMachine, @"Software", keys);
        CaptureRegistrySubkeys(Registry.CurrentUser, @"Software", keys);

        // Filesystem Roots
        CaptureDirList(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), dirs);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86)) CaptureDirList(pf86, dirs);

        CaptureDirList(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dirs);
        CaptureDirList(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), dirs);
        CaptureDirList(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), dirs);

        return new Snapshot(DateTime.Now, keys, dirs);
    }

    private static void CaptureRegistrySubkeys(RegistryKey hive, string subPath, HashSet<string> target)
    {
        try
        {
            using var key = hive.OpenSubKey(subPath);
            if (key == null) return;
            foreach (var name in key.GetSubKeyNames())
            {
                target.Add($@"{hive.Name}\{subPath}\{name}");
            }
        }
        catch { }
    }

    private static void CaptureDirList(string root, HashSet<string> target)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
            {
                target.Add(d);
            }
        }
        catch { }
    }
}
