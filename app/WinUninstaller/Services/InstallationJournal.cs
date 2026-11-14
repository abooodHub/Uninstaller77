using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

internal sealed record MonitoredRegistration(string Key, string Name, string Publisher, string Version, string Root, long? LastWrite = null)
{
    internal static MonitoredRegistration? Capture(InstalledApp app, long? lastWrite = null)
    {
        if (app.Source != "Registry" || !UninstallBroker.IsRegistrationPath(app.RegistryKeyPath) || !Path.IsPathFullyQualified(app.InstallLocation)) return null;
        try
        {
            return new(app.RegistryKeyPath.ToUpperInvariant(), app.Name, app.Publisher, app.Version,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(app.InstallLocation)).ToUpperInvariant(), lastWrite);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    internal static MonitoredRegistration? ReadCurrent(string path)
    {
        try
        {
            var before = UninstallRegistration.ReadLastWrite(path);
            if (before is null || RegistryDiscoveryService.ReadExactRegistration(path) is not { } app ||
                UninstallRegistration.ReadLastWrite(path) != before) return null;
            return Capture(app, before);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        { return null; }
    }
}

internal sealed record InstallationChange(string Kind, string Change, string Path, DateTimeOffset At, string Phase,
    string Association = "Observed", string? After = null, long Bytes = 0, int View = 0, string? Before = null);

internal sealed record InstallationReport(string Id, string Root, string RegistryScope, string State, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, int BaselineFiles, int BaselineKeys, MonitoredRegistration? Registration,
    InstallationChange[] Changes, string[] Issues);

internal sealed class InstallationJournal(string dataDirectory)
{
    private const int MaximumReportBytes = 4 * 1024 * 1024;
    private string Folder => Path.Combine(dataDirectory, "InstallationMonitoring");
    internal const string EvidenceReason = "The installation log links this unchanged item to the exact former installation. Review only.";

    internal void Save(InstallationReport report)
    {
        Directory.CreateDirectory(Folder);
        if (!LocalPathWithoutLinks(Folder)) throw new IOException("The installation log directory is redirected.");
        var path = Path.Combine(Folder, report.Id + ".json");
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, report);
                output.Flush(flushToDisk: true);
                if (output.Length > MaximumReportBytes) throw new IOException("The installation log exceeded the storage limit.");
            }
            File.Move(pending, path, overwrite: true);
        }
        finally { File.Delete(pending); }
    }

    internal InstallationReport[] Recent(Action<string>? reportError = null)
    {
        if (!Directory.Exists(Folder)) return [];
        if (!LocalPathWithoutLinks(Folder)) throw new IOException("The installation log directory is redirected.");
        var results = new List<InstallationReport>();
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Take(50))
        {
            try
            {
                if (file.Length > MaximumReportBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out _)) continue;
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (JsonSerializer.Deserialize<InstallationReport>(stream) is { Changes: not null, Issues: not null, Root: not null, RegistryScope: not null } report &&
                    report.Id + ".json" == file.Name && report.Changes.All(change => change is { Path.Length: > 0 and <= 760, Kind: not null, Change: not null }))
                    results.Add(report.State is "Capturing" or "Monitoring" ? report with { State = "Interrupted" } : report);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            { reportError?.Invoke(ex.Message); }
        }
        return results.ToArray();
    }

    internal int AddResidualEvidence(InstalledApp app, List<ResidualItem> items, CancellationToken ct)
    {
        var identity = MonitoredRegistration.Capture(app, app.RegistrationLastWrite);
        if (identity is not { LastWrite: > 0 } || UninstallRegistration.Read(app.RegistryKeyPath).State != UninstallRegistration.Presence.Absent) return 0;
        var report = Recent().FirstOrDefault(log => MatchesRemoval(log, identity));
        if (report is null) return 0;
        var remainingHashBytes = 512L * 1024 * 1024;
        var verified = new List<ResidualItem>();
        foreach (var observation in report.Changes.Where(change => change.Association == "RegistrationLinked" &&
                     change.Phase == "After" && change.Change == "Created" && change.After is not null))
        {
            ct.ThrowIfCancellationRequested();
            var isFile = observation.Kind == "File";
            var path = observation.Path;
            try
            {
                if (isFile)
                {
                    if (!Within(path, identity.Root) || !LocalPathWithoutLinks(path) ||
                        ReadFile(path, ref remainingHashBytes, ct).Hash != observation.After) continue;
                    // Keep the existing per-file approval from the ordinary install-directory scan.
                    var reviewedFile = items.Any(item => item.ReviewedFiles?.Any(file =>
                        SamePath(Path.Combine(item.Path, file.RelativePath), path)) == true);
                    if (reviewedFile || items.Any(item => item.Type == ResidualType.File && SamePath(item.Path, path))) continue;
                }
                else
                {
                    if (observation.Kind != "Registry" || !InstallationScope.Valid(report.RegistryScope) ||
                        !InstallationScope.IsRegistryPathInScope(path, report.RegistryScope) ||
                        InstallationScope.ReadKey(path, observation.View, identity) is not { Linked: true } current || current.Hash != observation.After) continue;
                    if (observation.View != 0) path += observation.View == 32 ? " [32-bit]" : " [64-bit]";
                }
                verified.Add(new() { Path = path, Type = isFile ? ResidualType.File : ResidualType.RegistryKey,
                    SizeBytes = isFile ? observation.Bytes : 0, Reason = EvidenceReason, Confidence = ConfidenceLevel.Medium });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { }
        }
        if (UninstallRegistration.Read(app.RegistryKeyPath).State != UninstallRegistration.Presence.Absent) return 0;
        foreach (var item in verified)
        {
            var existing = items.FirstOrDefault(candidate => candidate.Type == item.Type &&
                (candidate.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) || item.Type == ResidualType.RegistryKey &&
                    item.Path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) &&
                    candidate.Path.Equals(item.Path + (Environment.Is64BitOperatingSystem ? " [64-bit]" : " [32-bit]"), StringComparison.OrdinalIgnoreCase)));
            if (existing is null) items.Add(item);
            else { existing.Reason = EvidenceReason; existing.Confidence = ConfidenceLevel.Medium; }
        }
        return 1;
    }

    internal static bool MatchesRemoval(InstallationReport report, MonitoredRegistration identity) =>
        identity.LastWrite > 0 && report.State == "Completed" && report.Registration == identity && SamePath(report.Root, identity.Root);

    internal static (string? Hash, long Bytes, long LastWrite) ReadFile(string path, ref long hashBudget, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var bytes = info.Length;
        var lastWrite = info.LastWriteTimeUtc.Ticks;
        if (bytes > 64L * 1024 * 1024 || bytes > hashBudget) return (null, bytes, lastWrite);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != bytes) throw new IOException("File changed during capture.");
        hashBudget -= bytes;
        ct.ThrowIfCancellationRequested();
        var hash = Convert.ToHexString(SHA256.HashData(file));
        ct.ThrowIfCancellationRequested();
        if (File.GetLastWriteTimeUtc(path).Ticks != lastWrite) throw new IOException("File changed during capture.");
        return (hash, bytes, lastWrite);
    }

    internal static bool SamePath(string? left, string? right) => Path.IsPathFullyQualified(left ?? "") &&
        Path.IsPathFullyQualified(right ?? "") && Path.TrimEndingDirectorySeparator(Path.GetFullPath(left!))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right!)), StringComparison.OrdinalIgnoreCase);

    internal static bool Within(string path, string root) => Path.IsPathFullyQualified(path) &&
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static bool LocalPathWithoutLinks(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
}
