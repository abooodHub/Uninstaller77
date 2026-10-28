using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>
/// Bounded, user-scoped monitoring. File events come from FileSystemWatcher; Registry,
/// service, task, and process changes are conservative before/after or polling observations.
/// Activity during a session is not assumed to have been caused by an installer.
/// </summary>
public sealed class AdvancedInstallMonitorService : IDisposable
{
    public const int MaxSnapshotEntries = 20_000;
    public const int MaxSessionEvents = 10_000;
    public const int UiEventLimit = 200;
    private const int MaxWatchers = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly DatabaseService _db;
    private readonly object _gate = new();
    private readonly List<InstallMonitorEvent> _recent = [];
    private readonly List<InstallMonitorEvent> _pending = [];
    private readonly Dictionary<string, long> _recentSignatures = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _monitorCts;
    private Task? _flushTask;
    private Task? _processTask;
    private MonitorSnapshot? _baseline;
    private InstallMonitorSession? _session;
    private int? _installerProcessId;
    private double _startCpuSeconds;
    private long _startMemoryBytes;
    private bool _acceptEvents;
    private bool _eventLimitWarningAdded;
    private ProcessSnapshot _lastProcesses = new([]);

    public event EventHandler? Changed;
    public bool IsMonitoring { get { lock (_gate) return _session?.State == InstallMonitorState.Monitoring.ToString(); } }
    public InstallMonitorState State { get { lock (_gate) return _session is null ? InstallMonitorState.Idle : Enum.Parse<InstallMonitorState>(_session.State); } }
    public string SessionId { get { lock (_gate) return _session?.SessionId ?? ""; } }
    public InstallMonitorSession? ActiveSession { get { lock (_gate) return _session; } }
    public InstallMonitorSession? CurrentViewSession
    {
        get
        {
            var active = ActiveSession;
            if (active is not null && IsActiveState(active.State)) return active;
            return SelectedSessionId is { } id ? _db.GetMonitorSession(id) ?? active : active;
        }
    }
    public IReadOnlyList<InstallMonitorEvent> RecentEvents { get { lock (_gate) return _recent.ToArray(); } }

    public AdvancedInstallMonitorService(DatabaseService db)
    {
        _db = db;
        _db.MarkInterruptedMonitorSessions();
    }

    public async Task<InstallMonitorSession> StartManualAsync(string name, string scopePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Manual observation";
        var root = ValidateScope(scopePath);
        var sessionId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var process = Process.GetCurrentProcess();
        var startingCpu = process.TotalProcessorTime.TotalSeconds;
        var startingMemory = process.PrivateMemorySize64;
        _startCpuSeconds = startingCpu;
        _startMemoryBytes = startingMemory;
        var initial = new InstallMonitorSession(sessionId, Bound(name, 128), root, "", "", "Not applicable", InstallMonitorState.CapturingBaseline.ToString(), now, null, 0, 0, true, "[]", "{}", 0, 0);
        lock (_gate)
        {
            if (_session is not null && IsActiveState(_session.State)) throw new InvalidOperationException("A monitoring session is already active.");
            _session = initial; _baseline = null; _recent.Clear(); _pending.Clear(); _recentSignatures.Clear(); _installerProcessId = null; _eventLimitWarningAdded = false;
        }
        _db.CreateMonitorSession(initial, "{}");
        RaiseChanged();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var baseline = await Task.Run(() => CaptureSnapshot(root, linked.Token), linked.Token).ConfigureAwait(false);
            lock (_gate) _baseline = baseline;
            var warnings = baseline.Warnings.ToList();
            var monitoring = initial with { State = InstallMonitorState.Monitoring.ToString(), CoverageComplete = baseline.Warnings.Count == 0, WarningsJson = JsonSerializer.Serialize(warnings), SummaryJson = SerializeCounts([]) };
            _db.UpdateMonitorSession(monitoring, JsonSerializer.Serialize(baseline, JsonOptions));
            lock (_gate) { _session = monitoring; _acceptEvents = true; _lastProcesses = CaptureProcesses(); }
            StartWatcher(root);
            _monitorCts = new CancellationTokenSource();
            _flushTask = FlushLoopAsync(_monitorCts.Token);
            _processTask = ProcessPollLoopAsync(_monitorCts.Token);
            RaiseChanged();
            return monitoring;
        }
        catch (OperationCanceledException)
        {
            await FailStartAsync("Baseline capture was cancelled or exceeded its 45 second time limit.", startingCpu, startingMemory).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await FailStartAsync($"Baseline capture failed: {ex.GetType().Name}: {ex.Message}", startingCpu, startingMemory).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<InstallMonitorSession> StartAssistedAsync(string name, string scopePath, string installerPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(installerPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected installer no longer exists.", fullPath);
        var ext = Path.GetExtension(fullPath);
        if (!new[] { ".exe", ".msi", ".msix" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Only EXE, MSI, and MSIX installers can be selected.");
        var signature = InspectSignature(fullPath);
        var session = await StartManualAsync(name, scopePath, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _session = _session! with { InstallerPath = fullPath, InstallerPublisher = signature.Publisher, InstallerSignatureStatus = signature.Status };
            session = _session;
            PersistSession();
        }
        try
        {
            Process? child;
            if (ext.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                child = Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{fullPath}\"") { UseShellExecute = true });
            else child = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
            if (child is null) throw new InvalidOperationException("Windows did not return a process handle for the selected installer.");
            lock (_gate) _installerProcessId = child.Id;
            AddEvent("Processes", "InstallerLaunched", fullPath, "", $"PID={child.Id}", MonitorAssociation.ConfirmedAssociation, "Direct launcher process handle", child.Id);
            AddWarning("Installer process was started directly; descendant activity and file/Registry ownership are not inferred.");
            return _session!;
        }
        catch (Exception ex)
        {
            AddEvent("Errors", "InstallerLaunchFailed", fullPath, "", "Launch failed", MonitorAssociation.UnknownAssociation, "Process.Start", null, ex.Message);
            lock (_gate) { _session = _session! with { State = InstallMonitorState.Failed.ToString(), CoverageComplete = false, EndedAtUtc = DateTimeOffset.UtcNow }; PersistSession(); }
            await StopWatchersAsync().ConfigureAwait(false); RaiseChanged(); throw;
        }
    }

    public async Task<InstallMonitorSession> StopAsync(bool cancelled = false)
    {
        InstallMonitorSession session;
        lock (_gate)
        {
            if (_session is null || _session.State != InstallMonitorState.Monitoring.ToString())
                throw new InvalidOperationException("There is no active monitoring session to stop.");
            _session = _session with { State = (cancelled ? InstallMonitorState.Stopping : InstallMonitorState.StopRequested).ToString() };
            session = _session; _acceptEvents = false;
            PersistSession();
        }
        RaiseChanged();
        await StopWatchersAsync().ConfigureAwait(false);
        lock (_gate) { _session = _session! with { State = InstallMonitorState.CapturingFinalState.ToString() }; PersistSession(); }
        RaiseChanged();
        var root = session.ScopePath;
        MonitorSnapshot final;
        try { final = await Task.Run(() => CaptureSnapshot(root, CancellationToken.None)).ConfigureAwait(false); }
        catch (Exception ex)
        {
            AddWarning("Final snapshot was incomplete: " + ex.GetType().Name + ": " + ex.Message);
            final = new MonitorSnapshot(DateTimeOffset.UtcNow, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), CaptureProcesses(), ["Final snapshot failed."]);
        }
        lock (_gate) _session = _session! with { State = InstallMonitorState.Comparing.ToString() };
        RaiseChanged();
        if (_baseline is not null) CompareSnapshots(_baseline, final);
        AddProcessDifferences(_lastProcesses, final.Processes);
        foreach (var warning in final.Warnings) AddWarning(warning);
        await FlushPendingAsync().ConfigureAwait(false);
        var currentProcess = Process.GetCurrentProcess();
        var cpuSeconds = Math.Max(0, currentProcess.TotalProcessorTime.TotalSeconds - _startCpuSeconds);
        var memoryDelta = currentProcess.PrivateMemorySize64 - _startMemoryBytes;
        lock (_gate)
        {
            var warnings = ReadWarnings(_session!.WarningsJson);
            var completedState = cancelled ? InstallMonitorState.Cancelled : InstallMonitorState.Completed;
            var counts = EventCounts(_session.SessionId);
            var warningsJson = JsonSerializer.Serialize(warnings);
            _session = _session with
            {
                State = completedState.ToString(), EndedAtUtc = DateTimeOffset.UtcNow,
                CoverageComplete = _session.CoverageComplete && warnings.Count == 0,
                WarningsJson = warningsJson, SummaryJson = JsonSerializer.Serialize(counts),
                CpuSeconds = Math.Round(cpuSeconds, 3), MemoryDeltaBytes = memoryDelta
            };
            PersistSession(); session = _session;
        }
        RaiseChanged(); return session;
    }

    public void SelectSession(string sessionId)
    {
        if (_db.GetMonitorSession(sessionId) is null) throw new InvalidDataException("Monitoring session was not found.");
        SelectedSessionId = sessionId; RaiseChanged();
    }
    public string? SelectedSessionId { get; private set; }

    public IReadOnlyList<InstallMonitorSession> GetSessions() => _db.GetMonitorSessions(30);
    public IReadOnlyList<InstallMonitorEvent> GetSelectedEvents(int limit = UiEventLimit) =>
        SelectedSessionId is { } id ? _db.GetMonitorEvents(id, 0, Math.Clamp(limit, 1, UiEventLimit)) : RecentEvents;
    public IReadOnlyList<InstallMonitorEvent> GetEvents(string sessionId, int offset, int limit) => _db.GetMonitorEvents(sessionId, offset, limit);

    public void ExportSession(string sessionId, string path, string format)
    {
        var session = _db.GetMonitorSession(sessionId) ?? throw new InvalidDataException("Monitoring session was not found.");
        var events = new List<InstallMonitorEvent>();
        for (var offset = 0; offset < Math.Min(session.EventCount, MaxSessionEvents); offset += 500)
            events.AddRange(_db.GetMonitorEvents(sessionId, offset, 500));
        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder("TimestampUtc,Category,ChangeType,Target,PreviousMetadata,NewMetadata,Association,Source,ProcessId,Diagnostic\r\n");
            foreach (var e in events) sb.AppendJoin(',', Csv(e.TimestampUtc.ToString("O")), Csv(e.Category), Csv(e.ChangeType), Csv(Sanitize(e.Target)), Csv(Sanitize(e.PreviousMetadata)), Csv(Sanitize(e.NewMetadata)), Csv(e.Association.ToString()), Csv(e.Source), Csv(e.ProcessId?.ToString() ?? ""), Csv(Sanitize(e.Diagnostic ?? ""))).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
        else
        {
            var safeSession = session with { ScopePath = Sanitize(session.ScopePath), InstallerPath = Sanitize(session.InstallerPath) };
            var doc = new { session = safeSession, events = events.Select(e => e with { Target = Sanitize(e.Target), PreviousMetadata = Sanitize(e.PreviousMetadata), NewMetadata = Sanitize(e.NewMetadata), Diagnostic = e.Diagnostic is null ? null : Sanitize(e.Diagnostic) }) };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
    }

    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string Sanitize(string value)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? value : value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }
    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max];

    private static string ValidateScope(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Choose a folder to monitor.");
        var full = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("The selected monitoring folder does not exist.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("A junction or symbolic link cannot be used as the monitoring root.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static (string Status, string Publisher) InspectSignature(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            using var chain = new X509Chain(); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            var valid = chain.Build(cert);
            return (valid ? "Signed; chain valid (revocation not checked)" : "Signed; chain could not be validated", cert.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch (CryptographicException) { return ("Unsigned or signature unavailable", ""); }
        catch (Exception ex) { return ("Signature status unknown: " + ex.GetType().Name, ""); }
    }

    private async Task FailStartAsync(string message, double cpuStart, long memoryStart)
    {
        lock (_gate)
        {
            if (_session is null) return;
            AddWarningLocked(message);
            _session = _session with { State = InstallMonitorState.Failed.ToString(), EndedAtUtc = DateTimeOffset.UtcNow, CoverageComplete = false, ErrorCount = _session.ErrorCount + 1, CpuSeconds = Math.Max(0, Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds - cpuStart), MemoryDeltaBytes = Process.GetCurrentProcess().PrivateMemorySize64 - memoryStart };
            _db.UpdateMonitorSession(_session);
        }
        RaiseChanged(); await FlushPendingAsync().ConfigureAwait(false);
    }

    private void StartWatcher(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime, InternalBufferSize = 64 * 1024 };
            watcher.Created += (_, e) => AddFileEvent("Created", e.FullPath, "", CurrentFileMeta(e.FullPath), "FileSystemWatcher");
            watcher.Changed += (_, e) => AddFileEvent("Modified", e.FullPath, "", CurrentFileMeta(e.FullPath), "FileSystemWatcher");
            watcher.Deleted += (_, e) => AddFileEvent("Deleted", e.FullPath, BaselineFileMeta(e.FullPath), "", "FileSystemWatcher");
            watcher.Renamed += (_, e) => AddFileEvent("Renamed", e.FullPath, e.OldFullPath, CurrentFileMeta(e.FullPath), "FileSystemWatcher");
            watcher.Error += (_, e) => { MarkCoverageIncomplete("FileSystemWatcher overflow or I/O error; some filesystem events may be missing: " + e.GetException().GetType().Name); };
            watcher.EnableRaisingEvents = true; _watcher = watcher;
        }
        catch (Exception ex)
        {
            MarkCoverageIncomplete("Filesystem live watch could not start: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private void AddFileEvent(string type, string path, string previousPath, string metadata, string source)
    {
        var prior = type == "Renamed" ? previousPath : BaselineFileMeta(path);
        AddEvent("Files", type, path, prior, metadata, MonitorAssociation.ObservedDuringSession, source, null,
            type == "Renamed" ? "PreviousPath=" + previousPath : null);
    }
    private string BaselineFileMeta(string path) => _baseline?.Files.TryGetValue(path, out var m) == true ? Format(m) : "";
    private static string CurrentFileMeta(string path)
    {
        try { if (File.Exists(path)) { var f = new FileInfo(path); return Format(new FileMeta(f.Length, f.LastWriteTimeUtc.Ticks, (int)f.Attributes)); } } catch { }
        try { if (Directory.Exists(path)) return "Directory"; } catch { }
        return "Unavailable";
    }
    private static string Format(FileMeta meta) => $"size={meta.Size};lastWriteUtcTicks={meta.LastWriteUtcTicks};attributes={meta.Attributes}";

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(350));
        try { while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) await FlushPendingAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessPollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var current = await Task.Run(CaptureProcesses, ct).ConfigureAwait(false);
                AddProcessDifferences(_lastProcesses, current); _lastProcesses = current;
            }
        }
        catch (OperationCanceledException) { }
    }

    private static ProcessSnapshot CaptureProcesses()
    {
        var map = new Dictionary<int, ProcessMeta>();
        foreach (var p in Process.GetProcesses())
        {
            try { map[p.Id] = new ProcessMeta(p.ProcessName, p.StartTime.ToUniversalTime().Ticks); }
            catch (Exception ex) when (ex is SystemException) { }
            finally { p.Dispose(); }
        }
        return new ProcessSnapshot(map);
    }

    private void AddProcessDifferences(ProcessSnapshot before, ProcessSnapshot after)
    {
        foreach (var item in after.Items.Where(x => !before.Items.ContainsKey(x.Key)))
        {
            var root = item.Key == _installerProcessId;
            AddEvent("Processes", "Started", item.Value.Name, "", $"PID={item.Key}; startedUtcTicks={item.Value.StartedUtcTicks}", root ? MonitorAssociation.ConfirmedAssociation : MonitorAssociation.ObservedDuringSession, "Two-second process snapshot polling", item.Key);
        }
        foreach (var item in before.Items.Where(x => !after.Items.ContainsKey(x.Key)))
            AddEvent("Processes", "Stopped", item.Value.Name, $"PID={item.Key}; startedUtcTicks={item.Value.StartedUtcTicks}", "", MonitorAssociation.ObservedDuringSession, "Two-second process snapshot polling", item.Key);
    }

    private void CompareSnapshots(MonitorSnapshot before, MonitorSnapshot after)
    {
        foreach (var kv in after.Files)
            if (!before.Files.TryGetValue(kv.Key, out var old)) AddEvent("Files", "Created", kv.Key, "", Format(kv.Value), MonitorAssociation.ObservedDuringSession, "Before/after snapshot");
            else if (old != kv.Value) AddEvent("Files", "Modified", kv.Key, Format(old), Format(kv.Value), MonitorAssociation.ObservedDuringSession, "Before/after snapshot");
        foreach (var kv in before.Files)
            if (!after.Files.ContainsKey(kv.Key)) AddEvent("Files", "Deleted", kv.Key, Format(kv.Value), "", MonitorAssociation.ObservedDuringSession, "Before/after snapshot");
        CompareMap("Registry", before.Registry, after.Registry, "Registry");
        CompareMap("Services", before.Services, after.Services, "Service");
        CompareMap("ScheduledTasks", before.Tasks, after.Tasks, "ScheduledTask");
        foreach (var warning in before.Warnings.Concat(after.Warnings).Distinct(StringComparer.Ordinal)) AddWarning(warning);
    }

    private void CompareMap(string category, Dictionary<string, string> before, Dictionary<string, string> after, string prefix)
    {
        foreach (var kv in after)
        {
            if (!before.TryGetValue(kv.Key, out var old)) AddEvent(category, prefix + "Created", kv.Key, "", kv.Value, MonitorAssociation.UnknownAssociation, "Bounded before/after Registry snapshot");
            else if (!string.Equals(old, kv.Value, StringComparison.Ordinal)) AddEvent(category, prefix + "Changed", kv.Key, old, kv.Value, MonitorAssociation.UnknownAssociation, "Bounded before/after Registry snapshot");
        }
        foreach (var kv in before) if (!after.ContainsKey(kv.Key)) AddEvent(category, prefix + "Removed", kv.Key, kv.Value, "", MonitorAssociation.UnknownAssociation, "Bounded before/after Registry snapshot");
    }

    private static MonitorSnapshot CaptureSnapshot(string root, CancellationToken ct)
    {
        var warnings = new List<string>();
        var files = new Dictionary<string, FileMeta>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { var info = new FileInfo(file); files[file] = new(info.Length, info.LastWriteTimeUtc.Ticks, (int)info.Attributes); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add("File metadata could not be read: " + ex.GetType().Name); }
                if (files.Count >= MaxSnapshotEntries) { warnings.Add($"Snapshot limit of {MaxSnapshotEntries} files reached; coverage is incomplete."); break; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { warnings.Add("Selected folder could not be fully enumerated: " + ex.GetType().Name); }
        var registry = CaptureRegistry(warnings, ct);
        var services = CaptureServiceSnapshot(warnings, ct);
        var tasks = CaptureTaskSnapshot(warnings, ct);
        return new MonitorSnapshot(DateTimeOffset.UtcNow, files, registry, services, tasks, CaptureProcesses(), warnings.Distinct(StringComparer.Ordinal).ToList());
    }

    private static Dictionary<string, string> CaptureRegistry(List<string> warnings, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            ct.ThrowIfCancellationRequested();
            var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            var basePath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(basePath);
                if (uninstall is not null)
                foreach (var childName in uninstall.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var child = uninstall.OpenSubKey(childName);
                        if (child is null) continue;
                        var keyName = $"{hiveName}\\{view}\\{basePath}\\{childName}";
                        result[keyName + "\\@key"] = "Present";
                        foreach (var valueName in new[] { "DisplayName", "DisplayVersion", "Publisher", "InstallLocation" })
                        {
                            try
                            {
                                if (!child.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase)) continue;
                                var kind = child.GetValueKind(valueName).ToString(); var raw = child.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                                // Store only type and length; never persist the Registry value itself.
                                var length = raw switch { string s => Encoding.UTF8.GetByteCount(s), string[] a => a.Sum(Encoding.UTF8.GetByteCount), byte[] b => b.Length, int => 4, long => 8, _ => 0 };
                                result[keyName + "\\" + valueName] = $"type={kind};bytes={length}";
                            }
                            catch (Exception ex) when (ex is SystemException) { warnings.Add($"Registry value metadata unavailable at {keyName}\\{valueName}: {ex.GetType().Name}"); }
                        }
                    }
                    catch (Exception ex) when (ex is SystemException) { warnings.Add($"Registry uninstall entry unavailable: {ex.GetType().Name}"); }
                    if (result.Count >= MaxSnapshotEntries) { warnings.Add("Registry snapshot reached its entry limit; coverage is incomplete."); return result; }
                }
            }
            catch (Exception ex) when (ex is SystemException) { warnings.Add($"Registry view {hiveName}/{view} could not be read: {ex.GetType().Name}"); }
        }
        return result;
    }

    private static Dictionary<string, string> CaptureServiceSnapshot(List<string> warnings, CancellationToken ct) => CaptureRegistryTree(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services", 1, "Services", warnings, ct, true);
    private static Dictionary<string, string> CaptureTaskSnapshot(List<string> warnings, CancellationToken ct) => CaptureRegistryTree(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree", 6, "ScheduledTasks", warnings, ct, false);

    private static Dictionary<string, string> CaptureRegistryTree(RegistryHive hive, RegistryView view, string path, int depthLimit, string source, List<string> warnings, CancellationToken ct, bool includeSafeValues)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
            if (root is null) { warnings.Add(source + " registry scope is unavailable."); return result; }
            var stack = new Stack<(RegistryKey Key, string Name, int Depth)>(); stack.Push((root, path, 0));
            while (stack.Count > 0 && result.Count < MaxSnapshotEntries)
            {
                ct.ThrowIfCancellationRequested(); var (key, name, depth) = stack.Pop();
                using (key)
                {
                    result[name] = "Present";
                    if (includeSafeValues)
                    foreach (var valueName in new[] { "DisplayName", "Start", "Type", "ImagePath" })
                    {
                        try
                        {
                            if (!key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase)) continue;
                            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            var kind = key.GetValueKind(valueName).ToString();
                            var size = raw switch { string s => Encoding.UTF8.GetByteCount(s), byte[] b => b.Length, int => 4, long => 8, _ => 0 };
                            result[name + "\\" + valueName] = $"type={kind};bytes={size}";
                        }
                        catch (Exception ex) when (ex is SystemException) { warnings.Add(source + " value metadata unavailable: " + ex.GetType().Name); }
                    }
                    if (depth >= depthLimit) continue;
                    try
                    {
                        foreach (var childName in key.GetSubKeyNames().Take(MaxSnapshotEntries - result.Count))
                        {
                            try { var child = key.OpenSubKey(childName); if (child is not null) stack.Push((child, name + "\\" + childName, depth + 1)); }
                            catch (Exception ex) when (ex is SystemException) { warnings.Add(source + " child key unavailable: " + ex.GetType().Name); }
                        }
                    }
                    catch (Exception ex) when (ex is SystemException) { warnings.Add(source + " tree enumeration incomplete: " + ex.GetType().Name); }
                }
            }
            if (result.Count >= MaxSnapshotEntries) warnings.Add(source + " snapshot reached its entry limit; coverage is incomplete.");
        }
        catch (Exception ex) when (ex is SystemException) { warnings.Add(source + " snapshot failed: " + ex.GetType().Name); }
        return result;
    }

    private void AddEvent(string category, string type, string target, string before, string after, MonitorAssociation association, string source, int? pid = null, string? diagnostic = null)
    {
        InstallMonitorEvent? item = null;
        var notifyWithoutEvent = false;
        lock (_gate)
        {
            // The live watcher is stopped before the final snapshot, but comparison
            // results are still authoritative monitor observations and must persist.
            if (_session is null || (!_acceptEvents && _session.State != InstallMonitorState.Comparing.ToString())) return;
            if (_session.EventCount >= MaxSessionEvents)
            {
                if (!_eventLimitWarningAdded) { _eventLimitWarningAdded = true; AddWarningLocked($"Session event limit ({MaxSessionEvents}) reached; further events are not retained and coverage is incomplete."); }
                notifyWithoutEvent = true;
            }
            else
            {
                var signature = string.Join("\0", category, type, target, before, after, pid?.ToString() ?? "");
                var now = DateTimeOffset.UtcNow;
                if (_recentSignatures.TryGetValue(signature, out var previousTicks) && now.UtcTicks - previousTicks < TimeSpan.FromMilliseconds(800).Ticks)
                    notifyWithoutEvent = false;
                else
                {
                    _recentSignatures[signature] = now.UtcTicks;
                    item = new(Guid.NewGuid().ToString("N"), _session.SessionId, now, category, type, Bound(target, 4096), Bound(before, 512), Bound(after, 512), association, source, pid, diagnostic is null ? null : Bound(diagnostic, 512));
                    _pending.Add(item); _recent.Add(item); if (_recent.Count > UiEventLimit) _recent.RemoveAt(0);
                    var counts = ParseCounts(_session.SummaryJson);
                    counts[category] = counts.GetValueOrDefault(category) + 1;
                    _session = _session with { EventCount = _session.EventCount + 1, ErrorCount = _session.ErrorCount + (category == "Errors" ? 1 : 0), SummaryJson = JsonSerializer.Serialize(counts) };
                }
            }
        }
        if (item is null) { if (notifyWithoutEvent) RaiseChanged(); return; }
        RaiseChanged();
    }

    private void AddWarning(string warning)
    {
        lock (_gate) AddWarningLocked(warning);
        RaiseChanged();
    }
    private void AddWarningLocked(string warning)
    {
        if (_session is null) return;
        var warnings = ReadWarnings(_session.WarningsJson);
        if (warnings.Contains(warning, StringComparer.Ordinal)) return;
        warnings.Add(Bound(warning, 512));
        _session = _session with { CoverageComplete = false, WarningsJson = JsonSerializer.Serialize(warnings) };
    }
    private void MarkCoverageIncomplete(string warning)
    {
        AddWarning(warning);
        AddEvent("Errors", "CoverageWarning", "Monitoring scope", "", warning, MonitorAssociation.UnknownAssociation, "Monitor runtime", null, warning);
    }

    private async Task FlushPendingAsync()
    {
        List<InstallMonitorEvent> batch;
        lock (_gate) { if (_pending.Count == 0) { PersistSession(); return; } batch = _pending.ToList(); _pending.Clear(); }
        try { await Task.Run(() => _db.AddMonitorEvents(batch)).ConfigureAwait(false); }
        catch (Exception ex) { lock (_gate) AddWarningLocked("SQLite could not persist an event batch: " + ex.GetType().Name); }
        lock (_gate) PersistSession();
    }

    private async Task StopWatchersAsync()
    {
        if (_watcher is not null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
        _monitorCts?.Cancel();
        var tasks = new[] { _flushTask, _processTask }.Where(t => t is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        _monitorCts?.Dispose(); _monitorCts = null; _flushTask = _processTask = null;
    }

    private void PersistSession() { if (_session is not null) _db.UpdateMonitorSession(_session); }
    private static bool IsActiveState(string state) => state is "Preparing" or "CapturingBaseline" or "Monitoring" or "StopRequested" or "Stopping" or "CapturingFinalState" or "Comparing";
    private static List<string> ReadWarnings(string json) { try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; } catch { return []; } }
    private static Dictionary<string, int> ParseCounts(string json) { try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new(StringComparer.Ordinal); } catch { return new(StringComparer.Ordinal); } }
    private static string SerializeCounts(IReadOnlyList<InstallMonitorEvent> events) => JsonSerializer.Serialize(events.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Count()));
    private Dictionary<string, int> EventCounts(string sessionId)
    {
        var all = new List<InstallMonitorEvent>(); for (var offset = 0; offset < MaxSessionEvents; offset += 500) { var page = _db.GetMonitorEvents(sessionId, offset, 500); all.AddRange(page); if (page.Count < 500) break; }
        return all.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.Count());
    }
    private void RaiseChanged() { try { Changed?.Invoke(this, EventArgs.Empty); } catch { } }

    private static MonitorSnapshot DeserializeBaseline(string json) => JsonSerializer.Deserialize<MonitorSnapshot>(json, JsonOptions) ?? throw new InvalidDataException("Monitoring baseline is invalid.");

    public async Task<InstallMonitorSession> RecoverInterruptedAsync(string sessionId)
    {
        var session = _db.GetMonitorSession(sessionId) ?? throw new InvalidDataException("Monitoring session was not found.");
        if (session.State != InstallMonitorState.Interrupted.ToString()) throw new InvalidOperationException("Only an interrupted session can be recovered for comparison.");
        var baselineJson = _db.GetMonitorBaseline(sessionId) ?? "{}";
        var baseline = DeserializeBaseline(baselineJson);
        var final = await Task.Run(() => CaptureSnapshot(session.ScopePath, CancellationToken.None)).ConfigureAwait(false);
        var generated = new List<InstallMonitorEvent>();
        DiffTo(baseline, final, sessionId, generated);
        if (generated.Count != 0) _db.AddMonitorEvents(generated);
        var warnings = ReadWarnings(session.WarningsJson); warnings.Add("Recovery performs a current comparison; it cannot reconstruct events missed while the app was closed.");
        var updated = session with { State = InstallMonitorState.Interrupted.ToString(), EndedAtUtc = session.EndedAtUtc ?? DateTimeOffset.UtcNow, EventCount = session.EventCount + generated.Count, CoverageComplete = false, WarningsJson = JsonSerializer.Serialize(warnings), SummaryJson = SerializeCounts(generated) };
        _db.UpdateMonitorSession(updated); SelectedSessionId = sessionId; RaiseChanged(); return updated;
    }

    private static void DiffTo(MonitorSnapshot before, MonitorSnapshot after, string sessionId, List<InstallMonitorEvent> output)
    {
        foreach (var kv in after.Files)
            if (!before.Files.TryGetValue(kv.Key, out var old)) output.Add(NewEvent(sessionId,"Files","Created",kv.Key,"",Format(kv.Value),"Before/after recovery comparison"));
            else if (old != kv.Value) output.Add(NewEvent(sessionId,"Files","Modified",kv.Key,Format(old),Format(kv.Value),"Before/after recovery comparison"));
        foreach (var kv in before.Files) if (!after.Files.ContainsKey(kv.Key)) output.Add(NewEvent(sessionId,"Files","Deleted",kv.Key,Format(kv.Value),"","Before/after recovery comparison"));
        DiffMap(before.Registry,after.Registry,"Registry","Registry",sessionId,output);
        DiffMap(before.Services,after.Services,"Services","Service",sessionId,output);
        DiffMap(before.Tasks,after.Tasks,"ScheduledTasks","ScheduledTask",sessionId,output);
    }
    private static void DiffMap(Dictionary<string,string> b,Dictionary<string,string> a,string category,string prefix,string id,List<InstallMonitorEvent> output)
    {
        foreach(var kv in a) { if(!b.TryGetValue(kv.Key,out var old)) output.Add(NewEvent(id,category,prefix+"Created",kv.Key,"",kv.Value,"Before/after recovery Registry comparison")); else if(old!=kv.Value) output.Add(NewEvent(id,category,prefix+"Changed",kv.Key,old,kv.Value,"Before/after recovery Registry comparison")); }
        foreach(var kv in b) if(!a.ContainsKey(kv.Key)) output.Add(NewEvent(id,category,prefix+"Removed",kv.Key,kv.Value,"","Before/after recovery Registry comparison"));
    }
    private static InstallMonitorEvent NewEvent(string id,string category,string type,string target,string before,string after,string source) => new(Guid.NewGuid().ToString("N"),id,DateTimeOffset.UtcNow,category,type,Bound(target,4096),Bound(before,512),Bound(after,512),MonitorAssociation.UnknownAssociation,source);

    public void Dispose()
    {
        try { if (_session?.State == InstallMonitorState.Monitoring.ToString()) { _watcher?.Dispose(); _monitorCts?.Cancel(); } } catch { }
    }

    private sealed record FileMeta(long Size, long LastWriteUtcTicks, int Attributes);
    private sealed record ProcessMeta(string Name, long StartedUtcTicks);
    private sealed record ProcessSnapshot(Dictionary<int, ProcessMeta> Items);
    private sealed record MonitorSnapshot(DateTimeOffset CapturedAtUtc, Dictionary<string, FileMeta> Files,
        Dictionary<string,string> Registry, Dictionary<string,string> Services, Dictionary<string,string> Tasks,
        ProcessSnapshot Processes, List<string> Warnings);
}
