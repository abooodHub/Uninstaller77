using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public enum UninstallOperationState
{
    Idle, Preparing, Launching, Running, AwaitingCompletion, Verifying,
    ScanningResiduals, Completed, Failed, Cancelled, StopRequested, NeedsUserAttention, NotInstalled
}

public sealed record QueueJournalItem(string OperationId, InstalledApp App,
    UninstallOperationState State, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string Message = "", int? ExitCode = null, string FailureCode = "");

public sealed record QueueReviewEvent(DateTimeOffset At, string Action, string Evidence);
public sealed record QueueJournalSnapshot(string QueueId, DateTimeOffset CreatedAt,
    bool Active, bool StopRequested, IReadOnlyList<QueueJournalItem> Items,
    int SchemaVersion = 1, DateTimeOffset? InterruptionDetectedAt = null,
    DateTimeOffset? AcknowledgedUnresolvedAt = null, IReadOnlyList<QueueReviewEvent>? Reviews = null);

/// <summary>Atomic recovery evidence. Reading a journal never resumes an operation.</summary>
public sealed class QueueJournal
{
    internal const string CorruptJournalAction = "CorruptJournalArchived";
    internal const string InterruptedWriteAction = "InterruptedJournalWriteArchived";
    internal const string InterruptedWritePrimaryAction = "InterruptedJournalPrimaryArchived";
    private readonly string _path;
    private string TemporaryPath => _path + ".tmp";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public QueueJournal(string? directory = null)
    {
        var folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Queue");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "queue.json");
    }

    public QueueJournalSnapshot? Read()
    {
        string contents;
        try { contents = File.ReadAllText(_path); }
        catch (FileNotFoundException) { return null; }
        return DeserializeSnapshot(contents);
    }

    private static QueueJournalSnapshot DeserializeSnapshot(string contents)
    {
        var snapshot = JsonSerializer.Deserialize<QueueJournalSnapshot>(contents, Options);
        if (snapshot?.SchemaVersion > 2)
            throw new InvalidDataException("This queue was written by a newer version.");
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.QueueId) || snapshot.Items is null ||
            snapshot.Items.Any(item => item?.App is null || string.IsNullOrWhiteSpace(item.OperationId)))
            throw new JsonException("The queue journal is missing required data.");
        return snapshot;
    }

    internal QueueJournalSnapshot? ReadForRecovery()
    {
        if (File.Exists(TemporaryPath))
        {
            using var lease = AcquireExecutionLease();
            if (File.Exists(TemporaryPath)) return RecoverInterruptedWrite();
        }
        try { return Read(); }
        catch (JsonException)
        {
            using var lease = AcquireExecutionLease();
            try { return Read(); }
            catch (JsonException)
            {
                var archive = ArchiveBytes(File.ReadAllBytes(_path));
                var now = DateTimeOffset.UtcNow;
                var recovery = new QueueJournalSnapshot(Guid.NewGuid().ToString("N"), now, true, true, [],
                    SchemaVersion: 2, InterruptionDetectedAt: now, Reviews: [new(now, CorruptJournalAction, archive)]);
                WriteSnapshot(recovery);
                return recovery;
            }
        }
    }

    private QueueJournalSnapshot RecoverInterruptedWrite()
    {
        var temporaryBytes = File.ReadAllBytes(TemporaryPath);
        var temporaryArchive = ArchiveBytes(temporaryBytes);
        QueueJournalSnapshot? temporary = null;
        try { temporary = DeserializeSnapshot(File.ReadAllText(TemporaryPath)); }
        catch (JsonException) { }

        QueueJournalSnapshot? primary = null;
        string? primaryArchive = null;
        string? corruptPrimaryArchive = null;
        try
        {
            primary = Read();
            if (primary is not null) primaryArchive = ArchiveBytes(File.ReadAllBytes(_path));
        }
        catch (JsonException)
        {
            if (File.Exists(_path)) corruptPrimaryArchive = ArchiveBytes(File.ReadAllBytes(_path));
        }

        var now = DateTimeOffset.UtcNow;
        var candidate = temporary ?? primary ?? new QueueJournalSnapshot(
            Guid.NewGuid().ToString("N"), now, true, true, [], SchemaVersion: 2);
        var reviews = (candidate.Reviews ?? []).AsEnumerable();
        if (corruptPrimaryArchive is not null)
            reviews = reviews.Append(new(now, CorruptJournalAction, corruptPrimaryArchive));
        else if (primaryArchive is not null)
            reviews = reviews.Append(new(now, InterruptedWritePrimaryAction, primaryArchive));
        reviews = reviews.Append(new(now, InterruptedWriteAction, temporaryArchive));
        var recovered = candidate with
        {
            SchemaVersion = 2,
            Active = true,
            StopRequested = true,
            InterruptionDetectedAt = candidate.InterruptionDetectedAt ?? now,
            Reviews = reviews.ToArray()
        };
        WriteSnapshot(recovered);
        return recovered;
    }

    public void Save(QueueJournalSnapshot snapshot)
    {
        if (File.Exists(TemporaryPath))
            throw new InvalidOperationException("An interrupted queue write must be reviewed before saving new evidence.");
        var previous = Read();
        if (previous is not null && previous.QueueId != snapshot.QueueId) ArchiveCurrentFile();
        WriteSnapshot(snapshot);
    }

    private void WriteSnapshot(QueueJournalSnapshot snapshot)
    {
        // UI-only WPF icon objects are excluded by InstalledApp's JsonIgnore attribute.
        var contents = JsonSerializer.Serialize(snapshot with { SchemaVersion = 2 }, Options);
        using (var stream = new FileStream(TemporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(TemporaryPath, _path, overwrite: true);
    }

    private void Archive(QueueJournalSnapshot snapshot)
    {
        ArchiveBytes(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, Options)));
    }
    private void ArchiveCurrentFile() => ArchiveBytes(File.ReadAllBytes(_path));
    private string ArchiveBytes(byte[] evidence)
    {
        var hash = Convert.ToHexString(SHA256.HashData(evidence));
        var folder = Path.Combine(Path.GetDirectoryName(_path)!, "Evidence");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, hash + ".json");
        if (!File.Exists(destination))
        {
            var temporary = destination + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(evidence);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination);
        }
        if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(evidence))
            throw new IOException("Queue evidence archive could not be verified.");
        return destination;
    }

    public QueueJournalSnapshot RecordReview(string expectedQueueId, string action, string evidence)
    {
        if (action is not ("RecoveryDetected" or "ReadOnlyVerification" or "KeepPending" or "AcknowledgedUnresolved"))
            throw new InvalidOperationException("Unknown review action.");
        using var lease = AcquireExecutionLease();
        var saved = Read() ?? throw new InvalidOperationException("Queue evidence is unavailable.");
        if (saved.QueueId != expectedQueueId || !NeedsReview(saved))
            throw new InvalidOperationException("The queue review is stale or already acknowledged.");
        ArchiveCurrentFile(); // Preserve original bytes, including fields unknown to this version.
        var now = DateTimeOffset.UtcNow;
        var updated = saved with
        {
            SchemaVersion = 2,
            InterruptionDetectedAt = saved.InterruptionDetectedAt ?? (action == "RecoveryDetected" ? now : null),
            Active = action == "AcknowledgedUnresolved" ? false : saved.Active,
            StopRequested = action == "AcknowledgedUnresolved" || saved.StopRequested,
            AcknowledgedUnresolvedAt = action == "AcknowledgedUnresolved" ? now : saved.AcknowledgedUnresolvedAt,
            Reviews = (saved.Reviews ?? []).Append(new(now, action, evidence)).ToArray()
        };
        Archive(updated); // Preserve the acknowledgment itself, even across a later legacy-version overwrite.
        Save(updated);
        return updated;
    }

    public FileStream AcquireExecutionLease() => new(_path + ".lock", FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.Read);

    public static bool NeedsReview(QueueJournalSnapshot? snapshot) => snapshot is not null &&
        !(snapshot.AcknowledgedUnresolvedAt is not null && !snapshot.Active) &&
        (snapshot.Active || snapshot.Items.Any(item => item.State == UninstallOperationState.NeedsUserAttention ||
            item.StartedAt is not null && item.FinishedAt is null));
}
