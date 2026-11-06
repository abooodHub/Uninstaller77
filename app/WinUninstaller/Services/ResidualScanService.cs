using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Conservative review and reversible cleanup of a former install directory.</summary>
public partial class ResidualScanService
{
    public sealed record ScanProgress(string Stage, int ScannedLocations, ResidualInstallFolderState? InstallFolder = null);
    private sealed class ForwardProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => report(value); }
    public record CleanResult(bool Success, int CleanedCount, long CleanedBytes, string? BackupPath, List<string> Errors);
    public record RestoreResult(bool Success, string Message);
    private sealed record QuarantineManifest(string OriginalPath, string QuarantinePath, string AppName, DateTimeOffset CreatedUtc);

    private static readonly StringComparison PathComparison = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] ProtectedRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    ];
    private static readonly string[] ProtectedTrees =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Environment.GetFolderPath(Environment.SpecialFolder.Favorites),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Uninstaller 77"),
        AppContext.BaseDirectory
    ];

    public async Task<ResidualScanResult> ScanResidualsAsync(InstalledApp app, CancellationToken ct = default, IProgress<ScanProgress>? progress = null)
    {
        if (ct.IsCancellationRequested) return new(ResidualScanState.Cancelled, []);
        if (app.Source == "MSIX" || app.InstallerType == InstallType.Msix)
            return new(ResidualScanState.Unsupported, []);
        if (string.IsNullOrWhiteSpace(app.RegistryKeyPath))
            return new(ResidualScanState.NotScanned, [], "No recorded installation identity.");
        var locations = 0;
        var folderState = ResidualInstallFolderState.NotChecked;
        var measuredProgress = new ForwardProgress(value =>
        {
            locations = value.ScannedLocations;
            folderState = value.InstallFolder ?? folderState;
            progress?.Report(value);
        });
        try
        {
            var items = await Task.Run(() => CollectResiduals(app, ct, measuredProgress), ct);
            if (locations == 0) return new(ResidualScanState.NotScanned, [], "No supported locations were checked.", InstallFolder: folderState);
            return new(items.Count == 0 ? ResidualScanState.CompletedNoFindings : ResidualScanState.CompletedWithFindings, items,
                ScannedLocations: locations, InstallFolder: folderState);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(ResidualScanState.Cancelled, [], ScannedLocations: locations); }
        catch (Exception ex) { return new(ResidualScanState.Failed, [], ex.Message, locations, folderState); }
    }

    protected virtual List<ResidualItem> CollectResiduals(InstalledApp app, CancellationToken ct, IProgress<ScanProgress>? progress)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<ResidualItem>();
        var locations = 0;
        var folderState = InspectInstallFolder(app.InstallLocation, app);
        if (folderState is ResidualInstallFolderState.Checked or ResidualInstallFolderState.Missing) locations++;
        progress?.Report(new("Checking former install directory", locations,
            folderState == ResidualInstallFolderState.Checked ? ResidualInstallFolderState.NotChecked : folderState));
        if (folderState == ResidualInstallFolderState.Checked)
        {
            try
            {
                var files = UnregisteredAppService.Snapshot(app.InstallLocation, ct);
                if (files.Length > 0) result.Add(new ResidualItem
                {
                    Type = ResidualType.Directory, Path = Path.GetFullPath(app.InstallLocation),
                    SizeBytes = files.Sum(file => file.Bytes), Confidence = ConfidenceLevel.Medium,
                    Reason = "Former install location; review contents and shared use before moving to quarantine.",
                    ReviewedFingerprint = UnregisteredAppService.Fingerprint(files), ReviewedFiles = files,
                    CanClean = true, IsSelected = false
                });
                progress?.Report(new("Checking former install directory", locations, ResidualInstallFolderState.Checked));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                progress?.Report(new("Checking former install directory", locations, ResidualInstallFolderState.Restricted));
                result.Add(new ResidualItem
                {
                    Type = ResidualType.Directory, Path = Path.GetFullPath(app.InstallLocation), Confidence = ConfidenceLevel.Low,
                    Reason = "The former install directory could not be verified for quarantine.",
                    CanClean = false, IsSelected = false
                });
            }
        }
        AddInformationalMatches(app, result, progress, locations);
        return result;
    }

    public async Task<CleanResult> CleanResidualsAsync(
        List<ResidualItem> items, InstalledApp app, bool backupRegistry = true,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var selected = items.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return new CleanResult(true, 0, 0, null, []);
            var errors = new List<string>();
            var cleaned = 0;
            long bytes = 0;
            string? backup = null;

            foreach (var item in selected)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Type != ResidualType.Directory ||
                    !item.CanClean || item.ReviewedFingerprint is null ||
                    !SamePath(item.Path, app.InstallLocation) ||
                    !IsEligibleInstallDirectory(item.Path, app))
                {
                    errors.Add($"Refused unverified or shared item: {item.Path}");
                    continue;
                }

                try
                {
                    var backupRoot = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "WinUninstaller", "Quarantine");
                    if (HasReparsePointInPath(Path.GetDirectoryName(backupRoot)!) ||
                        Directory.Exists(backupRoot) && HasReparsePointInPath(backupRoot))
                        throw new IOException("The quarantine folder is redirected.");
                    Directory.CreateDirectory(backupRoot);
                    var destination = Path.Combine(backupRoot, Guid.NewGuid().ToString("N"));
                    var files = UnregisteredAppService.Snapshot(item.Path, ct);
                    if (UnregisteredAppService.Fingerprint(files) != item.ReviewedFingerprint)
                        throw new IOException("The reviewed directory changed. Scan again before moving it to quarantine.");
                    var manifest = new { OriginalPath = item.Path, QuarantinePath = destination,
                        AppName = app.Name, CreatedUtc = DateTimeOffset.UtcNow };
                    var manifestPath = destination + ".json";
                    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                    try { UnregisteredAppService.MoveVerified(item.Path, destination, item.ReviewedFingerprint); }
                    catch
                    {
                        if (Directory.Exists(destination)) backup = manifestPath;
                        else File.Delete(manifestPath);
                        throw;
                    }
                    backup = manifestPath;
                    cleaned++;
                    bytes += files.Sum(file => file.Bytes);
                    progress?.Report($"Moved to quarantine: {item.Path}");
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not move {item.Path}: {ex.Message}");
                }
            }

            return new CleanResult(errors.Count == 0, cleaned, bytes, backup, errors);
        }, ct);
    }

    public Task<RestoreResult> RestoreQuarantinedAsync(string manifestPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var root = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinUninstaller", "Quarantine"));
                var fullManifest = Path.GetFullPath(manifestPath);
                if (!IsWithin(fullManifest, root) || !fullManifest.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                    HasReparsePointInPath(fullManifest) || new FileInfo(fullManifest).Length > 16_384)
                    return new RestoreResult(false, "Manifest is outside the quarantine folder.");
                var manifest = JsonSerializer.Deserialize<QuarantineManifest>(File.ReadAllText(fullManifest));
                if (manifest == null || !SamePath(manifest.QuarantinePath, fullManifest[..^5]) ||
                    !IsWithin(manifest.QuarantinePath, root) || !Directory.Exists(manifest.QuarantinePath) ||
                    HasReparsePointInPath(manifest.QuarantinePath) ||
                    !Path.IsPathFullyQualified(manifest.OriginalPath) ||
                    Directory.Exists(manifest.OriginalPath) || File.Exists(manifest.OriginalPath) ||
                    IsReservedPath(manifest.OriginalPath) ||
                    ProtectedRoots.Any(p => SamePath(p, manifest.OriginalPath)))
                    return new RestoreResult(false, "Manifest or restore target is unsafe or already occupied.");
                var parent = Path.GetDirectoryName(manifest.OriginalPath);
                if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent) || HasReparsePointInPath(parent))
                    return new RestoreResult(false, "Restore parent is unavailable or is a reparse point.");
                Directory.Move(manifest.QuarantinePath, manifest.OriginalPath);
                File.Delete(fullManifest);
                return new RestoreResult(true, $"Restored to {manifest.OriginalPath}");
            }
            catch (Exception ex) { return new RestoreResult(false, ex.Message); }
        }, ct);

    public static bool IsEligibleInstallDirectory(string? path, InstalledApp app) =>
        InspectInstallFolder(path, app) == ResidualInstallFolderState.Checked;

    private static ResidualInstallFolderState InspectInstallFolder(string? path, InstalledApp app)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return ResidualInstallFolderState.NotRecorded;
            if (!Path.IsPathFullyQualified(path) || IsReservedPath(path)) return ResidualInstallFolderState.Restricted;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            // Avoid short-name aliases and Win32 normalization of trailing dots/spaces.
            if (full.Split(Path.DirectorySeparatorChar).Any(part => part.Contains('~') || part.EndsWith('.') || part.EndsWith(' ')) ||
                ProtectedRoots.Any(root => !string.IsNullOrWhiteSpace(root) && SamePath(full, root)) ||
                ProtectedTrees.Any(root => Path.IsPathFullyQualified(root) && Overlaps(full, Path.GetFullPath(root))) ||
                string.IsNullOrWhiteSpace(app.RegistryKeyPath) || !SamePath(full, app.InstallLocation) ||
                UninstallRegistration.Read(app.RegistryKeyPath).State != UninstallRegistration.Presence.Absent)
                return ResidualInstallFolderState.Restricted;
            // Check ancestors before touching the recorded path to avoid following a redirect.
            var ancestors = new Stack<DirectoryInfo>();
            for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
                ancestors.Push(directory);
            foreach (var directory in ancestors)
            {
                var attributes = File.GetAttributes(directory.FullName);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    return ResidualInstallFolderState.Restricted;
            }

            var discovery = new RegistryDiscoveryService();
            var inventory = discovery.DiscoverApps(includeMsix: false);
            if (discovery.LastDiagnostics.Any(source => source.Errors != 0 || source.SourceErrors != 0 || source.TimedOut || source.Cancelled))
                return ResidualInstallFolderState.Restricted;
            foreach (var other in inventory)
            {
                if (string.IsNullOrWhiteSpace(other.InstallLocation) ||
                    string.Equals(other.RegistryKeyPath, app.RegistryKeyPath, StringComparison.OrdinalIgnoreCase)) continue;
                var otherPath = Path.GetFullPath(other.InstallLocation).TrimEnd(Path.DirectorySeparatorChar);
                if (Overlaps(full, otherPath)) return ResidualInstallFolderState.Restricted;
            }
            return ResidualInstallFolderState.Checked;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return ResidualInstallFolderState.Missing; }
        catch { return ResidualInstallFolderState.Restricted; }
    }

    private static bool IsWithin(string child, string parent) =>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private static bool Overlaps(string left, string right)
    {
        left = Path.TrimEndingDirectorySeparator(left);
        right = Path.TrimEndingDirectorySeparator(right);
        return SamePath(left, right) || IsWithin(left, right) || IsWithin(right, left);
    }

    private static void AddInformationalMatches(InstalledApp app, List<ResidualItem> items, IProgress<ScanProgress>? progress, int startingCount)
    {
        var name = app.Name.Trim();
        if (!SafeName(name)) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var versionSeparator = name.LastIndexOf(' ');
        if (versionSeparator >= 3)
        {
            var suffix = name[(versionSeparator + 1)..];
            var version = app.Version.Trim();
            if (suffix.Contains('.') && suffix.Split('.').All(part => part.Length > 0 && part.All(char.IsAsciiDigit)) &&
                (version == suffix || version.StartsWith(suffix + ".", StringComparison.Ordinal)))
                names.Add(name[..versionSeparator]);
        }
        var icon = ShellUninstallContextMenuService.GetDisplayIconExecutable(app.DisplayIcon);
        if (icon?.EndsWith(".exe", PathComparison) == true)
        {
            var executableName = Path.GetFileNameWithoutExtension(icon);
            if (SafeName(executableName) && name.StartsWith(executableName + " ", PathComparison))
                names.Add(executableName);
        }
        var publisher = app.Publisher.Trim();
        var validPublisher = SafeName(publisher);
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        var scanned = startingCount;
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            scanned++;
            foreach (var candidate in names)
            {
                AddFolder(Path.Combine(folder, candidate));
                if (validPublisher) AddFolder(Path.Combine(folder, publisher, candidate));
            }
            progress?.Report(new($"Checking folder location: {folder}", scanned));
        }
        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            scanned++;
            foreach (var candidate in names)
            {
                AddKey($@"{hive}\Software\{candidate}");
                if (validPublisher) AddKey($@"{hive}\Software\{publisher}\{candidate}");
            }
            progress?.Report(new($"Checking Registry hive: {hive}", scanned));
        }

        static bool SafeName(string candidate) => candidate.Length >= 3 && !candidate.EndsWith('.') &&
            !candidate.EndsWith(' ') && candidate.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        void AddFolder(string candidate)
        {
            try
            {
                if ((File.GetAttributes(candidate) & FileAttributes.Directory) == 0 || SamePath(candidate, app.InstallLocation) ||
                    items.Any(i => SamePath(i.Path, candidate))) return;
                items.Add(new ResidualItem
                {
                    Type = ResidualType.Directory, Path = candidate, Confidence = ConfidenceLevel.Low,
                    Reason = "Name match only; ownership unverified. Informational, not eligible for cleanup.",
                    CanClean = false, IsSelected = false
                });
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        void AddKey(string candidate)
        {
            if (!RegistryKeyExists(candidate, suppressErrors: false) || items.Any(i => i.Path.Equals(candidate, StringComparison.OrdinalIgnoreCase))) return;
            items.Add(new ResidualItem
            {
                Type = ResidualType.RegistryKey, Path = candidate, Confidence = ConfidenceLevel.Low,
                Reason = "Name match only; shared Registry data possible. Informational, not eligible for cleanup.",
                CanClean = false, IsSelected = false
            });
        }
    }

    private static bool IsReservedPath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(full);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) ||
            string.Equals(full, root?.TrimEnd(Path.DirectorySeparatorChar), PathComparison)) return true;
        foreach (var segment in new[] { "Windows", "Common Files", "WindowsApps", "Microsoft Shared" })
            if (full.Contains("\\" + segment + "\\", PathComparison) ||
                full.EndsWith("\\" + segment, PathComparison)) return true;
        return false;
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), PathComparison);
    }

    private static bool HasReparsePointInPath(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    private static bool RegistryKeyExists(string path, bool suppressErrors = true)
    {
        try
        {
            var index = path.IndexOf('\\');
            if (index < 0) return true;
            var hive = path[..index].ToUpperInvariant() switch
            {
                "HKCU" => Registry.CurrentUser,
                "HKLM" => Registry.LocalMachine,
                _ => null
            };
            if (hive == null) return true;
            using var key = hive.OpenSubKey(path[(index + 1)..]);
            return key != null;
        }
        catch when (suppressErrors) { return true; }
    }

}
