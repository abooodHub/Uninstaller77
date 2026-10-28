using System.IO;
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
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public QueueJournal(string? directory = null)
    {
        var folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinUninstaller", "Queue");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "queue.json");
    }

    public QueueJournalSnapshot? Read() => File.Exists(_path)
        ? JsonSerializer.Deserialize<QueueJournalSnapshot>(File.ReadAllText(_path), Options) : null;

    public void Save(QueueJournalSnapshot snapshot)
    {
        var previous = Read();
        if (previous is not null && previous.QueueId != snapshot.QueueId) ArchiveCurrentFile();
        // UI-only WPF icon objects are excluded by InstalledApp's JsonIgnore attribute.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot with { SchemaVersion = 2 }, Options));
        File.Move(temporary, _path, overwrite: true);
    }

    private void Archive(QueueJournalSnapshot snapshot)
    {
        ArchiveBytes(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, Options)));
    }
    private void ArchiveCurrentFile() => ArchiveBytes(File.ReadAllBytes(_path));
    private void ArchiveBytes(byte[] evidence)
    {
        var hash = Convert.ToHexString(SHA256.HashData(evidence));
        var folder = Path.Combine(Path.GetDirectoryName(_path)!, "Evidence");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, hash + ".json");
        if (!File.Exists(destination)) File.WriteAllBytes(destination, evidence);
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
