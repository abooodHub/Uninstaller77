using System.IO;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

internal sealed class InstallationRecorder(InstallationJournal journal)
{
    private const int MaximumEntries = 1_200;
    private const int MaximumLiveEvents = 500;
    private const int MaximumPathLength = 640;
    private sealed record Snapshot(Dictionary<string, (string? Hash, long Bytes, long LastWrite)> Files,
        Dictionary<(string Path, int View), InstallationScope.KeyStamp> Keys, List<InstalledApp> Apps,
        bool FilesComplete, bool RegistryComplete, bool CatalogComplete)
    {
        internal bool Complete => FilesComplete && RegistryComplete && CatalogComplete;
    }

    internal async Task RecordAsync(string root, string registryScope, Action<InstallationReport> publish,
        CancellationToken finish, CancellationToken cancel = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root) || !InstallationScope.ValidDirectory(root) ||
            !InstallationJournal.LocalPathWithoutLinks(root) || (registryScope.Length > 0 && !InstallationScope.Valid(registryScope)))
            throw new InvalidDataException("Choose an existing local application folder and a valid product Registry key.");

        var report = new InstallationReport(Guid.NewGuid().ToString("N"), root, registryScope, "Capturing", DateTimeOffset.UtcNow,
            null, 0, 0, null, [], []);
        var observations = new List<InstallationChange>();
        var issues = new HashSet<string>(StringComparer.Ordinal);
        var sync = new object();
        var lastFileEvents = new Dictionary<string, (string Change, DateTimeOffset At)>(StringComparer.OrdinalIgnoreCase);
        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        watcher.Created += FileEvent;
        watcher.Changed += FileEvent;
        watcher.Deleted += FileEvent;
        watcher.Renamed += (_, data) => { ObserveFile(data.OldFullPath, "Deleted"); ObserveFile(data.FullPath, "Created"); };
        watcher.Error += (_, _) => Warn("File notifications were interrupted. The final comparison still runs.");
        using var endRecording = CancellationTokenSource.CreateLinkedTokenSource(finish, cancel);

        try
        {
            Save(report);
            var before = Capture();
            cancel.ThrowIfCancellationRequested();
            var previousKeys = before.Keys;
            var canComparePreviousKeys = before.RegistryComplete;
            watcher.EnableRaisingEvents = true;
            report = report with { State = "Monitoring", BaselineFiles = before.Files.Count, BaselineKeys = before.Keys.Count };
            var lastSavedCount = Save(report);
            var canLink = before.Complete;
            while (!endRecording.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), endRecording.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (endRecording.IsCancellationRequested) { break; }
                var keys = CaptureRegistry(out var readComplete);
                canLink &= readComplete;
                if (readComplete)
                {
                    foreach (var delta in CompareKeys(previousKeys, keys, "During", canComparePreviousKeys).Take(MaximumLiveEvents + 1)) Track(delta);
                    previousKeys = keys;
                    canComparePreviousKeys = true;
                }
                var counts = CountObservations();
                if (counts != lastSavedCount)
                {
                    lastSavedCount = Save(report);
                }
            }
            watcher.EnableRaisingEvents = false;
            cancel.ThrowIfCancellationRequested();
            var after = Capture();
            cancel.ThrowIfCancellationRequested();
            var candidate = canLink && after.Complete ? MatchRegistration(root, before.Apps, after.Apps) : null;
            var current = candidate is null ? null : MonitoredRegistration.ReadCurrent(candidate.Key);
            var registration = current is not null && current with { LastWrite = null } == candidate ? current : null;
            var referenced = registration is null ? [] : after.Apps.Where(app => MonitoredRegistration.Capture(app) == candidate)
                .SelectMany(ReferencedFiles).ToArray();
            report = report with { State = "Completed", FinishedAt = DateTimeOffset.UtcNow,
                Registration = registration };
            lock (sync)
            {
                var finalChanges = CompareFiles(before.Files, after.Files, before.FilesComplete, after.FilesComplete)
                    .Concat(CompareKeys(before.Keys, after.Keys, "After", before.RegistryComplete, after.RegistryComplete))
                    .Take(MaximumEntries + 1).ToArray();
                if (finalChanges.Length > MaximumEntries) Warn("The final change limit was reached.");
                if (issues.Count > 0) report = report with { Registration = null };
                foreach (var delta in finalChanges.Take(MaximumEntries))
                {
                    if (report.Registration is not null && delta.Change == "Created" && delta.After is not null &&
                        (delta.Kind == "File" ? referenced.Any(path => InstallationJournal.SamePath(path, delta.Path))
                            : KeyMatches(after.Keys[(delta.Path, delta.View)], report.Registration)))
                        observations.Add(delta with { Association = "RegistrationLinked" });
                    else observations.Add(delta);
                }
                if (report.Registration is { } linked)
                    observations.Add(new("Registration", "Created", linked.Key, DateTimeOffset.UtcNow, "After", "RegistrationLinked"));
                if (!before.Complete || !after.Complete) Warn("Some changes could not be classified because a snapshot missed part of the scope.");
                if (report.Registration is null) Warn("A unique new registration for the monitored folder was not verified.");
            }
            Save(report);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            watcher.EnableRaisingEvents = false;
            Save(report with { State = "Cancelled", Registration = null, FinishedAt = DateTimeOffset.UtcNow });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            watcher.EnableRaisingEvents = false;
            Warn(ex.Message);
            var failed = report with { State = "Failed", Registration = null, FinishedAt = DateTimeOffset.UtcNow };
            try { Save(failed); }
            catch (Exception persistenceError) when (persistenceError is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { Warn(persistenceError.Message); publish(Freeze(failed)); }
        }

        (int Changes, int Issues) Save(InstallationReport snapshot)
        {
            var frozen = Freeze(snapshot);
            journal.Save(frozen);
            publish(frozen);
            return (frozen.Changes.Length, frozen.Issues.Length);
        }

        InstallationReport Freeze(InstallationReport snapshot)
        {
            lock (sync) return snapshot with { Changes = observations.ToArray(), Issues = issues.Order().ToArray() };
        }

        (int Changes, int Issues) CountObservations()
        {
            lock (sync) return (observations.Count, issues.Count);
        }

        void Track(InstallationChange change)
        {
            lock (sync)
            {
                if (observations.Count < MaximumLiveEvents) observations.Add(change);
                else Warn("The live event limit was reached.");
            }
        }

        void Warn(string message)
        {
            lock (sync) { if (issues.Count < 32) issues.Add(message.Length > 350 ? message[..350] : message); }
        }

        void FileEvent(object sender, FileSystemEventArgs data) => ObserveFile(data.FullPath, data.ChangeType.ToString());

        void ObserveFile(string path, string change)
        {
            if (path.Length > MaximumPathLength) { Warn("A file path exceeded the monitoring limit."); return; }
            lock (sync)
            {
                var now = DateTimeOffset.UtcNow;
                if (lastFileEvents.TryGetValue(path, out var last) && last.Change == change &&
                    now - last.At < TimeSpan.FromMilliseconds(250)) return;
                if (lastFileEvents.Count >= MaximumLiveEvents && !lastFileEvents.ContainsKey(path))
                { Warn("The live event limit was reached."); return; }
                lastFileEvents[path] = (change, now);
                Track(new("FileEvent", change, path, now, "During"));
            }
        }

        Snapshot Capture()
        {
            var files = new Dictionary<string, (string? Hash, long Bytes, long LastWrite)>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(root);
            var complete = true;
            var remainingHashBytes = 512L * 1024 * 1024;
            var visited = 0;
            while (pending.TryPop(out var folder))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    if (!InstallationJournal.LocalPathWithoutLinks(folder)) { complete = false; continue; }
                    foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (++visited > MaximumEntries) { pending.Clear(); complete = false; break; }
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 || path.Length > MaximumPathLength) { complete = false; continue; }
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                        else files[path] = InstallationJournal.ReadFile(path, ref remainingHashBytes, cancel);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { complete = false; Warn(ex.Message); }
            }
            if (!complete) Warn("Some file paths were redirected, unreadable, or exceeded the capture limits.");
            if (files.Values.Any(file => file.Hash is null)) Warn("Some files exceeded the hashing budget. Their metadata is visible for review.");
            var registry = CaptureRegistry(out var registryComplete);
            var discovery = new RegistryDiscoveryService();
            var catalog = discovery.DiscoverApps(includeMsix: false, cancellationToken: cancel);
            cancel.ThrowIfCancellationRequested();
            var catalogComplete = discovery.LastDiagnostics.All(source => source.Errors == 0 && source.SourceErrors == 0 && !source.Cancelled && !source.TimedOut);
            if (!catalogComplete) Warn("Uninstall registrations could not all be read.");
            return new(files, registry, catalog, complete, registryComplete, catalogComplete);
        }

        Dictionary<(string Path, int View), InstallationScope.KeyStamp> CaptureRegistry(out bool complete)
        {
            var keys = new Dictionary<(string Path, int View), InstallationScope.KeyStamp>();
            complete = true;
            if (registryScope.Length == 0) return keys;
            foreach (var (hiveName, view) in InstallationScope.RegistryViews(Environment.Is64BitOperatingSystem))
            {
                try
                {
                    using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(hiveName == "HKCU" ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine,
                        view == 0 ? Microsoft.Win32.RegistryView.Default : view == 32 ? Microsoft.Win32.RegistryView.Registry32 : Microsoft.Win32.RegistryView.Registry64);
                    var pending = new Stack<string>();
                    pending.Push(registryScope);
                    while (pending.TryPop(out var path))
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (keys.Count >= MaximumEntries || path.Length > MaximumPathLength) { complete = false; break; }
                        using var key = hive.OpenSubKey(path, writable: false);
                        if (key is null) continue;
                        var full = hiveName + "\\" + path;
                        if (InstallationScope.ReadKey(full, view, null) is not { } stamp) { complete = false; continue; }
                        keys[(full.ToUpperInvariant(), view)] = stamp;
                        if (key.SubKeyCount > MaximumEntries - keys.Count - pending.Count)
                        { complete = false; break; }
                        foreach (var name in key.GetSubKeyNames()) pending.Push(path + "\\" + name);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { complete = false; Warn(ex.Message); }
            }
            if (!complete) Warn("Some Registry keys could not be captured within the configured limits.");
            return keys;
        }
    }

    internal static MonitoredRegistration? MatchRegistration(string root, IReadOnlyList<InstalledApp> before, IReadOnlyList<InstalledApp> after)
    {
        if (before.Any(app => Overlap(app)) || after.Where(Overlap).ToArray() is not [var found] ||
            before.Any(app => string.Equals(app.RegistryKeyPath, found.RegistryKeyPath, StringComparison.OrdinalIgnoreCase)) ||
            !InstallationJournal.SamePath(found.InstallLocation, root)) return null;
        return ReferencedFiles(found).Any(path => InstallationJournal.Within(path, root))
            ? MonitoredRegistration.Capture(found) : null;

        bool Overlap(InstalledApp app) => Path.IsPathFullyQualified(app.InstallLocation) &&
            (InstallationJournal.SamePath(app.InstallLocation, root) || InstallationJournal.Within(app.InstallLocation, root) ||
                InstallationJournal.Within(root, app.InstallLocation));
    }

    private static IEnumerable<string> ReferencedFiles(InstalledApp app)
    {
        var (executable, _) = RegisteredUninstallCommand.SplitCommand(Environment.ExpandEnvironmentVariables(app.UninstallString));
        if (Path.IsPathFullyQualified(executable)) yield return executable;
        if (ShellUninstallContextMenuService.GetDisplayIconExecutable(app.DisplayIcon) is { } icon) yield return icon;
    }

    private static bool KeyMatches(InstallationScope.KeyStamp key, MonitoredRegistration identity) =>
        string.Equals(key.Registration, identity.Key, StringComparison.OrdinalIgnoreCase) && InstallationJournal.SamePath(key.InstallLocation, identity.Root);

    internal static IEnumerable<InstallationChange> CompareFiles(Dictionary<string, (string? Hash, long Bytes, long LastWrite)> before,
        Dictionary<string, (string? Hash, long Bytes, long LastWrite)> after, bool beforeComplete, bool afterComplete)
    {
        foreach (var path in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var existed = before.TryGetValue(path, out var old);
            var exists = after.TryGetValue(path, out var current);
            if ((!existed && beforeComplete) || (!exists && afterComplete) || (existed && exists && old != current))
                yield return new("File", !existed ? "Created" : exists ? "Modified" : "Deleted", path, DateTimeOffset.UtcNow, "After",
                    After: current.Hash, Bytes: current.Bytes, Before: old.Hash);
        }
    }

    internal static IEnumerable<InstallationChange> CompareKeys(Dictionary<(string Path, int View), InstallationScope.KeyStamp> before,
        Dictionary<(string Path, int View), InstallationScope.KeyStamp> after, string phase, bool beforeComplete = true, bool afterComplete = true)
    {
        foreach (var key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out var old);
            after.TryGetValue(key, out var current);
            if (old?.Hash != current?.Hash && (old is not null || beforeComplete) && (current is not null || afterComplete))
                yield return new("Registry", old is null ? "Created" : current is null ? "Deleted" : "Modified", key.Path,
                    DateTimeOffset.UtcNow, phase, After: current?.Hash, View: key.View, Before: old?.Hash);
        }
    }
}
