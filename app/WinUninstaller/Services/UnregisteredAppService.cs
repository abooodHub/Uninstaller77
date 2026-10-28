using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

/// <summary>Reviewed permanent file deletion and legacy reversible backup support.
/// Never invents uninstall commands, removes Registry entries or walks up to a guessed install root.</summary>
public sealed class UnregisteredAppService
{
    public sealed record Entry(string RelativePath, long Bytes, string Sha256);
    public sealed record Plan(string Id, string Name, string Target, string Root, string Kind,
        long Bytes, string Fingerprint, IReadOnlyList<Entry> Files, string? StoredReceipt = null);
    public sealed record Receipt(string Id, string Name, string Kind, string OriginalPath, string QuarantinePath,
        string Target, string Fingerprint, Entry[] Files, DateTimeOffset CreatedUtc);
    public sealed record DeletionResult(bool Complete, bool CanRetry, int DeletedFiles, long DeletedBytes, int RemainingFiles, int UnverifiedMissingFiles,
        string? PendingPath, string[] DeletedPaths, string[] Errors);
    public sealed record DeletionProgress(int DeletedFiles, int TotalFiles);
    private sealed record Envelope(string Kind, string Body, string Signature);
    public const string ManifestKind = "UnregisteredApplicationV1";
    private const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxFiles = 20000;
    private readonly string _profile;
    private readonly string _store;
    private readonly string _application;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] DataNames = ["data", "user data", "userdata", "profiles", "profile", "logs", ".git", ".ssh", ".env", "node_modules"];

    public UnregisteredAppService() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Quarantine"), AppContext.BaseDirectory) { }

    // A separate fixture profile/store is available to the compiled regression suite only.
    internal UnregisteredAppService(string profile, string store, string application)
    { _profile = Full(profile); _store = Full(store); _application = Full(application); }

    public Plan Prepare(string executable, IEnumerable<InstalledApp> registrations, CancellationToken ct = default)
    {
        var target = Full(executable);
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) Fail("UnsupportedExecutable");
        if (!File.Exists(target) || Within(target, _store)) return PrepareStored(target, registrations, ct);
        var root = Path.GetDirectoryName(target)!;
        ValidateScope(root);
        CheckExecutable(target);
        var catalog = registrations.ToArray();
        if (ShellUninstallContextMenuService.FindMatchingApps(target, catalog).Count != 0 || catalog.Any(app =>
            Path.IsPathFullyQualified(app.InstallLocation) && (Same(app.InstallLocation, root) || Within(app.InstallLocation, root)) ||
            ShellUninstallContextMenuService.GetDisplayIconExecutable(app.DisplayIcon) is string icon && Within(icon, root))) Fail("RegisteredScope");
        var kind = IsElectronBundle(root) ? "ElectronBundle" : Directory.GetFileSystemEntries(root).Length == 1 ? "SingleExecutable" : "";
        if (kind.Length == 0) Fail("UnknownLayout");
        if (Directory.GetFileSystemEntries(root).Any(path => IsDataName(Path.GetFileName(path)))) Fail("MixedData");
        var files = Snapshot(root, ct);
        if (files.Length == 0) Fail("ChangedScope");
        return new(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(target), target, root, kind,
            files.Sum(file => file.Bytes), Fingerprint(files), Array.AsReadOnly(files));
    }

    public string Quarantine(Plan plan, IEnumerable<InstalledApp> currentRegistrations, CancellationToken ct = default)
    {
        // Rebuild every scope and byte observation after confirmation. Caller-supplied paths never enter this method from WebView.
        var fresh = Prepare(plan.Target, currentRegistrations, ct);
        if (!Same(fresh.Root, plan.Root) || fresh.Fingerprint != plan.Fingerprint || fresh.Kind != plan.Kind) Fail("ChangedScope");
        CheckRunning(plan.Target);
        ValidateStore();
        if (!Guid.TryParseExact(plan.Id, "N", out _)) Fail("InvalidReview");
        var destination = Path.Combine(_store, plan.Id);
        var manifest = destination + ".json";
        if (Directory.Exists(destination) || File.Exists(manifest)) Fail("InvalidReview");
        // Windows refuses a directory rename while child handles remain open.
        // Validate under read locks, release them for the atomic rename, then
        // verify the destination and roll back if a concurrent change occurred.
        using (var locks = new FileLocks(plan.Files.Select(file => SafeChild(plan.Root, file.RelativePath))))
            if (Fingerprint(Snapshot(plan.Root, ct)) != plan.Fingerprint) Fail("ChangedScope");
        var receipt = new Receipt(plan.Id, plan.Name, plan.Kind, plan.Root, destination, plan.Target, plan.Fingerprint, plan.Files.ToArray(), DateTimeOffset.UtcNow);
        WriteManifest(manifest, receipt);
        try
        {
            ct.ThrowIfCancellationRequested();
            // No cancellation after this boundary: the complete directory moves together, on the same volume.
            MoveVerified(plan.Root, destination, plan.Fingerprint);
        }
        catch { if (!Directory.Exists(destination)) File.Delete(manifest); throw; }
        return manifest;
    }

    public Receipt Restore(string manifestPath, CancellationToken ct = default)
    {
        if (File.Exists(Full(manifestPath) + ".deleting")) throw Error("PermanentDeletionStarted");
        var receipt = ReadReceipt(manifestPath);
        ValidateScope(receipt.OriginalPath, mustExist: false);
        if (Directory.Exists(receipt.OriginalPath) || File.Exists(receipt.OriginalPath)) Fail("RestoreOccupied");
        var parent = Path.GetDirectoryName(receipt.OriginalPath)!;
        if (!Directory.Exists(parent)) Fail("RestoreParentMissing");
        NoReparse(parent);
        CheckRunning(SafeChild(receipt.QuarantinePath, Path.GetRelativePath(receipt.OriginalPath, receipt.Target)));
        if (Fingerprint(Snapshot(receipt.QuarantinePath, ct)) != receipt.Fingerprint) Fail("BackupChanged");
        using (var locks = new FileLocks(receipt.Files.Select(file => SafeChild(receipt.QuarantinePath, file.RelativePath))))
            if (Fingerprint(Snapshot(receipt.QuarantinePath, ct)) != receipt.Fingerprint) Fail("BackupChanged");
        ct.ThrowIfCancellationRequested();
        MoveVerified(receipt.QuarantinePath, receipt.OriginalPath, receipt.Fingerprint);
        // Preserve the authenticated receipt as historical evidence. A repeated restore is refused by the occupied target check.
        return receipt;
    }

    public bool IsManifest(string path)
    {
        try { return ReadEnvelope(path).Kind == ManifestKind; }
        catch { return false; }
    }

    // Staging is temporary commit preparation, not a retained backup. Only the
    // authenticated reviewed file set is deleted, one file at a time. Never use
    // recursive deletion or run an unregistered command supplied by a shortcut.
    public DeletionResult DeletePermanently(Plan plan, IEnumerable<InstalledApp> currentRegistrations,
        IProgress<DeletionProgress>? progress = null, CancellationToken ct = default)
    {
        if (plan.StoredReceipt is not null)
        {
            var fresh = PrepareStored(plan.Target, currentRegistrations, ct);
            if (!Same(fresh.Root, plan.Root) || fresh.Fingerprint != plan.Fingerprint || fresh.StoredReceipt != plan.StoredReceipt) throw Error("ChangedScope");
            var stored = ReadReceipt(plan.StoredReceipt);
            CheckRunning(SafeChild(stored.QuarantinePath, Path.GetRelativePath(stored.OriginalPath, stored.Target)));
            ct.ThrowIfCancellationRequested();
            return DeleteStaged(plan.StoredReceipt, [], progress);
        }
        var staging = DeletionStore();
        var receipt = staging.Quarantine(plan, currentRegistrations, ct);
        return staging.DeleteStaged(receipt, [], progress);
    }

    public DeletionResult RetryPermanentDeletion(string planId, IReadOnlyList<string> verifiedDeletedPaths,
        IProgress<DeletionProgress>? progress = null)
    {
        if (!Guid.TryParseExact(planId, "N", out _)) throw Error("InvalidReview");
        var staging = DeletionStore();
        return staging.DeleteStaged(Path.Combine(staging._store, planId + ".json"), verifiedDeletedPaths, progress);
    }

    public DeletionResult RetryPermanentDeletion(Plan plan, IReadOnlyList<string> verifiedDeletedPaths, IProgress<DeletionProgress>? progress = null) =>
        plan.StoredReceipt is null ? RetryPermanentDeletion(plan.Id, verifiedDeletedPaths, progress) : DeleteStaged(plan.StoredReceipt, verifiedDeletedPaths, progress);

    private Plan PrepareStored(string originalTarget, IEnumerable<InstalledApp> registrations, CancellationToken ct)
    {
        if (ShellUninstallContextMenuService.FindMatchingApps(originalTarget, registrations).Count != 0) throw Error("RegisteredScope");
        if (!Directory.Exists(_store)) throw Error("UnsupportedExecutable");
        NoReparse(_store);
        var matches = new List<(string Path, Receipt Receipt)>(); var inspected = 0;
        foreach (var path in Directory.EnumerateFiles(_store, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested(); if (++inspected > 1000) throw Error("ScopeTooLarge");
            try
            {
                if (File.Exists(path + ".deleting")) continue;
                var receipt = ReadReceipt(path);
                var relocatedTarget = SafeChild(receipt.QuarantinePath, Path.GetRelativePath(receipt.OriginalPath, receipt.Target));
                if ((Same(receipt.Target, originalTarget) || Same(relocatedTarget, originalTarget)) && Directory.Exists(receipt.QuarantinePath)) matches.Add((path, receipt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or CryptographicException) { /* Other/invalid receipts are not trusted. */ }
        }
        if (matches.Count == 0) throw Error("UnsupportedExecutable");
        if (matches.Count != 1) throw Error("AmbiguousBackup");
        var match = matches[0]; ValidateScope(match.Receipt.OriginalPath, mustExist: false);
        if (ShellUninstallContextMenuService.FindMatchingApps(match.Receipt.Target, registrations).Count != 0) throw Error("RegisteredScope");
        if (Directory.Exists(match.Receipt.OriginalPath) || Fingerprint(Snapshot(match.Receipt.QuarantinePath, ct)) != match.Receipt.Fingerprint) throw Error("BackupChanged");
        return new(Guid.NewGuid().ToString("N"), match.Receipt.Name, match.Receipt.Target, match.Receipt.QuarantinePath,
            match.Receipt.Kind, match.Receipt.Files.Sum(file => file.Bytes), match.Receipt.Fingerprint,
            Array.AsReadOnly(match.Receipt.Files), match.Path);
    }

    private UnregisteredAppService DeletionStore() => new(_profile,
        Path.Combine(Path.GetDirectoryName(_store)!, "PendingRemoval"), _application);

    private DeletionResult DeleteStaged(string path, IReadOnlyList<string> verifiedDeletedPaths, IProgress<DeletionProgress>? progress)
    {
        var receipt = ReadReceipt(path);
        var deleted = verifiedDeletedPaths.ToHashSet(StringComparer.Ordinal);
        var expected = receipt.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        if (deleted.Any(name => !expected.ContainsKey(name))) throw Error("InvalidReview");
        var errors = new List<string>();
        try
        {
            var remaining = Snapshot(receipt.QuarantinePath, CancellationToken.None);
            if (remaining.Any(file => !expected.TryGetValue(file.RelativePath, out var original) || file != original || deleted.Contains(file.RelativePath)) ||
                remaining.Length + deleted.Count != expected.Count) throw Error("ChangedScope");
            // Restore must never treat a partially deleted deployment as a complete backup.
            NoReparse(path + ".deleting");
            using (var marker = new FileStream(path + ".deleting", FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) { marker.Flush(true); }
            progress?.Report(new(deleted.Count, expected.Count));
            foreach (var file in receipt.Files)
            {
                if (deleted.Contains(file.RelativePath)) continue;
                var target = SafeChild(receipt.QuarantinePath, file.RelativePath);
                NoReparse(target);
                using (var stream = File.OpenRead(target))
                    if (stream.Length != file.Bytes || Convert.ToHexString(SHA256.HashData(stream)) != file.Sha256) throw Error("ChangedScope");
                File.Delete(target);
                if (File.Exists(target)) throw Error("DeletionUnconfirmed");
                deleted.Add(file.RelativePath);
                progress?.Report(new(deleted.Count, expected.Count));
            }
            // Enumerate and remove empty directories only. Unexpected files are preserved.
            var directories = new List<string>(); var stack = new Stack<string>(); stack.Push(receipt.QuarantinePath);
            while (stack.TryPop(out var directory))
            {
                NoReparse(directory); directories.Add(directory);
                if (directories.Count > MaxFiles * 2) throw Error("ScopeTooLarge");
                foreach (var child in Directory.EnumerateDirectories(directory)) { NoReparse(child); stack.Push(child); }
            }
            foreach (var directory in directories.OrderByDescending(directory => directory.Length))
            { NoReparse(directory); Directory.Delete(directory, recursive: false); }
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        if (Directory.Exists(receipt.OriginalPath)) errors.Add("UnregisteredApp:SourceReappeared");
        var complete = errors.Count == 0 && deleted.Count == expected.Count && !Directory.Exists(receipt.QuarantinePath);
        var present = -1;
        try { NoReparse(receipt.QuarantinePath); present = receipt.Files.Count(file => File.Exists(SafeChild(receipt.QuarantinePath, file.RelativePath))); }
        catch { /* A changed path cannot supply a trustworthy remaining count. */ }
        return new(complete, !complete && Directory.Exists(receipt.QuarantinePath), deleted.Count,
            deleted.Sum(name => expected[name].Bytes), present, present < 0 ? -1 : Math.Max(0, expected.Count - deleted.Count - present),
            complete ? null : Directory.Exists(receipt.QuarantinePath) ? receipt.QuarantinePath : receipt.OriginalPath,
            deleted.OrderBy(name => name, StringComparer.Ordinal).ToArray(), errors.ToArray());
    }

    public static string ReadAction(JsonElement payload, bool confirmation)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw Error("InvalidReview");
        var fields = payload.EnumerateObject().ToArray();
        if (fields.Length != (confirmation ? 3 : 1) || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            fields.Any(field => field.Name != "planId" && (!confirmation || field.Name is not ("acknowledged" or "permanent"))) ||
            !payload.TryGetProperty("planId", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParseExact(id.GetString(), "N", out _) ||
            confirmation && (!payload.TryGetProperty("acknowledged", out var consent) || consent.ValueKind != JsonValueKind.True ||
                !payload.TryGetProperty("permanent", out var permanent) || permanent.ValueKind != JsonValueKind.True)) throw Error("InvalidReview");
        return id.GetString()!;
    }

    private Receipt ReadReceipt(string path)
    {
        var envelope = ReadEnvelope(path);
        if (envelope.Kind != ManifestKind) Fail("InvalidBackup");
        var body = Convert.FromBase64String(envelope.Body);
        var signature = Convert.FromBase64String(envelope.Signature);
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key(create: false), body), signature)) Fail("InvalidBackup");
        var receipt = JsonSerializer.Deserialize<Receipt>(body) ?? throw Error("InvalidBackup");
        if (!Guid.TryParseExact(receipt.Id, "N", out _) || !Same(path, Path.Combine(_store, receipt.Id + ".json")) ||
            !Same(receipt.QuarantinePath, Path.Combine(_store, receipt.Id)) || receipt.Files is null || receipt.Files.Length is < 1 or > MaxFiles ||
            Fingerprint(receipt.Files) != receipt.Fingerprint || receipt.Kind is not ("ElectronBundle" or "SingleExecutable") || !Within(receipt.Target, receipt.OriginalPath)) Fail("InvalidBackup");
        NoReparse(receipt.QuarantinePath);
        foreach (var file in receipt.Files) _ = SafeChild(receipt.QuarantinePath, file.RelativePath);
        return receipt;
    }

    private Envelope ReadEnvelope(string path)
    {
        var full = Full(path);
        if (!Same(Path.GetDirectoryName(full)!, _store) || !full.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileNameWithoutExtension(full), "N", out _) || new FileInfo(full).Length > 8 * 1024 * 1024) Fail("InvalidBackup");
        NoReparse(full);
        return JsonSerializer.Deserialize<Envelope>(File.ReadAllText(full)) ?? throw Error("InvalidBackup");
    }

    private void WriteManifest(string path, Receipt receipt)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        var envelope = new Envelope(ManifestKind, Convert.ToBase64String(body), Convert.ToBase64String(HMACSHA256.HashData(Key(create: true), body)));
        var encoded = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        if (encoded.Length > 8 * 1024 * 1024) Fail("ScopeTooLarge");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(encoded); stream.Flush(true);
    }

    private byte[] Key(bool create)
    {
        var path = Path.Combine(_store, "unregistered-integrity.key");
        if (!File.Exists(path) && create)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(RandomNumberGenerator.GetBytes(32)); stream.Flush(true);
        }
        NoReparse(path);
        var key = File.ReadAllBytes(path);
        if (key.Length != 32) Fail("InvalidBackup");
        return key;
    }

    private void ValidateStore()
    {
        NoReparse(_store);
        Directory.CreateDirectory(_store);
        NoReparse(_store);
    }

    private void ValidateScope(string root, bool mustExist = true)
    {
        root = Full(root);
        var container = Path.GetDirectoryName(_store)!;
        if (new[] { Path.Combine(container, "Quarantine"), Path.Combine(container, "PendingRemoval") }
            .Any(protectedPath => Same(root, protectedPath) || Within(root, protectedPath) || Within(protectedPath, root))) Fail("ProtectedScope");
        if (!Within(root, _profile) || Path.GetRelativePath(_profile, root).Split(Path.DirectorySeparatorChar).Length < 2 ||
            Same(root, _store) || Within(root, _store) || Within(_store, root) || Same(root, _application) || Within(_application, root) || Within(root, _application)) Fail("ProtectedScope");
        var reserved = new[] { "Desktop", "Documents", "Downloads", "AppData", @"AppData\Local", @"AppData\Roaming", @"AppData\Local\Programs", @"AppData\Local\Temp" };
        if (reserved.Any(path => Same(root, Path.Combine(_profile, path)))) Fail("ProtectedScope");
        if (Path.GetRelativePath(_profile, root).Split(Path.DirectorySeparatorChar).Any(IsDataName)) Fail("ProtectedScope");
        if (!Same(Path.GetPathRoot(root)!, Path.GetPathRoot(_store)!)) Fail("DifferentVolume");
        NoReparse(root);
        if (mustExist && !Directory.Exists(root)) Fail("ChangedScope");
    }

    private static bool IsElectronBundle(string root) => new[] { "resources\\app.asar", "icudtl.dat", "chrome_100_percent.pak" }.All(path => File.Exists(Path.Combine(root, path)));
    private static bool IsDataName(string name) => DataNames.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase);
    private static void CheckExecutable(string target)
    {
        using var stream = File.OpenRead(target);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) Fail("UnsupportedExecutable");
        stream.Position = 0x3c; var offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 4) Fail("UnsupportedExecutable");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) Fail("UnsupportedExecutable");
    }

    private static Entry[] Snapshot(string root, CancellationToken ct)
    {
        NoReparse(root);
        var result = new List<Entry>(); var directories = new Stack<string>(); directories.Push(root); long bytes = 0; var nodes = 0;
        while (directories.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested(); NoReparse(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested(); NoReparse(path);
                if (++nodes > MaxFiles * 2) Fail("ScopeTooLarge");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0) { directories.Push(path); continue; }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                bytes += stream.Length;
                if (result.Count >= MaxFiles || bytes > MaxBytes) Fail("ScopeTooLarge");
                result.Add(new(Path.GetRelativePath(root, path), stream.Length, Convert.ToHexString(SHA256.HashData(stream))));
            }
        }
        return result.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void CheckRunning(string target)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is string path && Same(path, target)) Fail("ApplicationRunning"); }
                catch (System.ComponentModel.Win32Exception) { Fail("ApplicationRunning"); }
                catch (InvalidOperationException) { /* Process exited while observing. File locks are checked next. */ }
            }
        }
    }

    private static void MoveVerified(string source, string destination, string fingerprint)
    {
        NoReparse(source); NoReparse(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
        try { if (Fingerprint(Snapshot(destination, CancellationToken.None)) != fingerprint) Fail("ChangedScope"); }
        catch
        {
            // Preserve every byte, including a concurrently added file. Never overwrite a newly occupied original path.
            if (!Directory.Exists(source) && !File.Exists(source)) Directory.Move(destination, source);
            else throw Error("MoveNeedsReview");
            throw;
        }
    }

    private sealed class FileLocks : IDisposable
    {
        private readonly List<FileStream> _streams = [];
        public FileLocks(IEnumerable<string> files)
        {
            try { foreach (var path in files) _streams.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)); }
            catch { Dispose(); throw Error("FilesInUse"); }
        }
        public void Dispose() { foreach (var stream in _streams) stream.Dispose(); }
    }

    private static string SafeChild(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':')) Fail("InvalidBackup");
        var full = Full(Path.Combine(root, relative));
        if (!Within(full, root)) Fail("InvalidBackup");
        return full;
    }
    private static string Fingerprint(IEnumerable<Entry> files) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(files))));
    private static string Full(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw Error("ProtectedScope");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static bool Same(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string child, string parent) => Full(child).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void NoReparse(string path)
    {
        for (var cursor = Full(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) Fail("LinkedPath");
    }
    private static IOException Error(string code) => new("UnregisteredApp:" + code);
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code) => throw Error(code);
}
