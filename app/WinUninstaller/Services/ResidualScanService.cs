using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Conservative review and reversible cleanup of a former install directory.</summary>
public class ResidualScanService
{
    public sealed record ScanProgress(string Stage, int ScannedLocations);
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

    public async Task<ResidualScanResult> ScanResidualsAsync(InstalledApp app, CancellationToken ct = default, IProgress<ScanProgress>? progress = null)
    {
        if (ct.IsCancellationRequested) return new(ResidualScanState.Cancelled, []);
        if (app.Source == "MSIX" || app.InstallerType == InstallType.Msix)
            return new(ResidualScanState.Unsupported, []);
        if (string.IsNullOrWhiteSpace(app.RegistryKeyPath))
            return new(ResidualScanState.NotScanned, [], "No recorded installation identity.");
        var locations = 0;
        var measuredProgress = new ForwardProgress(value => { locations = value.ScannedLocations; progress?.Report(value); });
        try
        {
            var items = await Task.Run(() => CollectResiduals(app, ct, measuredProgress), ct);
            if (locations == 0) return new(ResidualScanState.NotScanned, [], "No supported locations were checked.");
            return new(items.Count == 0 ? ResidualScanState.CompletedNoFindings : ResidualScanState.CompletedWithFindings, items, ScannedLocations: locations);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(ResidualScanState.Cancelled, [], ScannedLocations: locations); }
        catch (Exception ex) { return new(ResidualScanState.Failed, [], ex.Message, locations); }
    }

    protected virtual List<ResidualItem> CollectResiduals(InstalledApp app, CancellationToken ct, IProgress<ScanProgress>? progress)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<ResidualItem>();
        var locations = 0;
        if (IsEligibleInstallDirectory(app.InstallLocation, app))
        {
            progress?.Report(new("Checking former install directory", ++locations));
            result.Add(new ResidualItem
            {
                Type = ResidualType.Directory, Path = Path.GetFullPath(app.InstallLocation),
                SizeBytes = CalculateDirectorySize(app.InstallLocation, ct), Confidence = ConfidenceLevel.Medium,
                Reason = "Former install location; review contents and shared use before moving to quarantine.",
                CanClean = true, IsSelected = false
            });
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
                    Directory.CreateDirectory(backupRoot);
                    var destination = Path.Combine(backupRoot, Guid.NewGuid().ToString("N"));
                    var size = CalculateDirectorySize(item.Path, ct);
                    var manifest = new { OriginalPath = item.Path, QuarantinePath = destination,
                        AppName = app.Name, CreatedUtc = DateTimeOffset.UtcNow };
                    backup = destination + ".json";
                    File.WriteAllText(backup, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                    try { Directory.Move(item.Path, destination); }
                    catch { File.Delete(backup); throw; }
                    cleaned++;
                    bytes += size;
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

    public static bool IsEligibleInstallDirectory(string? path, InstalledApp app)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return false;
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if (IsReservedPath(path)) return false;
            if (ProtectedRoots.Any(root => !string.IsNullOrWhiteSpace(root) && SamePath(full, root))) return false;
            if (HasReparsePointInPath(full)) return false;
            if (string.IsNullOrWhiteSpace(app.RegistryKeyPath) || !SamePath(full, app.InstallLocation)) return false;
            if (RegistryKeyExists(app.RegistryKeyPath)) return false;

            var discovery = new RegistryDiscoveryService();
            var inventory = discovery.DiscoverApps(includeMsix: false);
            if (discovery.LastDiagnostics.Any(source => source.Errors != 0 || source.SourceErrors != 0 || source.TimedOut || source.Cancelled)) return false;
            foreach (var other in inventory)
            {
                if (string.IsNullOrWhiteSpace(other.InstallLocation) ||
                    string.Equals(other.RegistryKeyPath, app.RegistryKeyPath, StringComparison.OrdinalIgnoreCase)) continue;
                var otherPath = Path.GetFullPath(other.InstallLocation).TrimEnd(Path.DirectorySeparatorChar);
                if (SamePath(full, otherPath) || IsWithin(full, otherPath) || IsWithin(otherPath, full)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsWithin(string child, string parent) =>
        child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);

    private static void AddInformationalMatches(InstalledApp app, List<ResidualItem> items, IProgress<ScanProgress>? progress, int startingCount)
    {
        var name = app.Name.Trim();
        if (name.Length < 3 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('\\') || name.Contains('/') || name is "." or "..") return;
        var publisher = app.Publisher.Trim();
        var validPublisher = publisher.Length >= 3 &&
            publisher.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            !publisher.Contains('\\') && !publisher.Contains('/');
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
            AddFolder(Path.Combine(folder, name));
            if (validPublisher) AddFolder(Path.Combine(folder, publisher, name));
            progress?.Report(new($"Checking folder location: {folder}", scanned));
        }
        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            scanned++;
            AddKey($@"{hive}\Software\{name}");
            if (validPublisher) AddKey($@"{hive}\Software\{publisher}\{name}");
            progress?.Report(new($"Checking Registry hive: {hive}", scanned));
        }

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

    private static long CalculateDirectorySize(string path, CancellationToken ct)
    {
        long size = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (HasReparsePointInPath(current)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                    try { size += new FileInfo(file).Length; } catch { }
                foreach (var dir in Directory.EnumerateDirectories(current))
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) pending.Push(dir);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return size;
    }
}
