using Microsoft.Data.Sqlite;
using WinUninstaller.Models;
namespace WinUninstaller.Services;

public partial class DatabaseService
{
    partial void EnsureRetiredSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS LoggedInstallations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppName TEXT NOT NULL,
                InstallerPath TEXT,
                LogDate TEXT NOT NULL,
                TrackedFilesJson TEXT,
                TrackedRegistryJson TEXT,
                Notes TEXT
            );

            CREATE TABLE IF NOT EXISTS InstallMonitorSessions (
                SessionId TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                ScopePath TEXT NOT NULL,
                InstallerPath TEXT NOT NULL DEFAULT '',
                InstallerPublisher TEXT NOT NULL DEFAULT '',
                InstallerSignatureStatus TEXT NOT NULL DEFAULT 'Not inspected',
                State TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                EndedAtUtc TEXT,
                EventCount INTEGER NOT NULL DEFAULT 0,
                ErrorCount INTEGER NOT NULL DEFAULT 0,
                CoverageComplete INTEGER NOT NULL DEFAULT 1,
                WarningsJson TEXT NOT NULL DEFAULT '[]',
                SummaryJson TEXT NOT NULL DEFAULT '{}',
                BaselineJson TEXT NOT NULL DEFAULT '{}',
                CpuSeconds REAL NOT NULL DEFAULT 0,
                MemoryDeltaBytes INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS InstallMonitorEvents (
                EventId TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL,
                TimestampUtc TEXT NOT NULL,
                Category TEXT NOT NULL,
                ChangeType TEXT NOT NULL,
                Target TEXT NOT NULL,
                PreviousMetadata TEXT NOT NULL DEFAULT '',
                NewMetadata TEXT NOT NULL DEFAULT '',
                Association TEXT NOT NULL,
                Source TEXT NOT NULL,
                ProcessId INTEGER,
                Diagnostic TEXT NOT NULL DEFAULT '',
                FOREIGN KEY(SessionId) REFERENCES InstallMonitorSessions(SessionId) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_InstallMonitorEvents_SessionTime ON InstallMonitorEvents(SessionId, TimestampUtc, EventId);
            """;
        command.ExecuteNonQuery();
    }

    // --- Logged Installations API ---

    public sealed record LoggedInstallation(int Id, string AppName, string InstallerPath, string LogDate, string TrackedFilesJson, string TrackedRegistryJson, string Notes);

    public int AddLoggedInstallation(string appName, string installerPath, string filesJson, string regJson, string notes)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LoggedInstallations (AppName, InstallerPath, LogDate, TrackedFilesJson, TrackedRegistryJson, Notes)
            VALUES ($app, $path, $date, $files, $reg, $notes);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$app", appName);
        cmd.Parameters.AddWithValue("$path", installerPath ?? "");
        cmd.Parameters.AddWithValue("$date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$files", filesJson ?? "[]");
        cmd.Parameters.AddWithValue("$reg", regJson ?? "[]");
        cmd.Parameters.AddWithValue("$notes", notes ?? "");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<LoggedInstallation> GetLoggedInstallations()
    {
        var list = new List<LoggedInstallation>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, AppName, InstallerPath, LogDate, TrackedFilesJson, TrackedRegistryJson, Notes FROM LoggedInstallations ORDER BY Id DESC;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LoggedInstallation(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? "[]" : reader.GetString(4),
                reader.IsDBNull(5) ? "[]" : reader.GetString(5),
                reader.IsDBNull(6) ? "" : reader.GetString(6)
            ));
        }
        return list;
    }

    public void DeleteLoggedInstallation(int id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LoggedInstallations WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // --- Install Monitor Phase 13 persistence ---

    public void CreateMonitorSession(InstallMonitorSession session, string baselineJson)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using (var active = conn.CreateCommand())
        {
            active.Transaction = tx;
            active.CommandText = "SELECT COUNT(*) FROM InstallMonitorSessions WHERE State IN ('Preparing','CapturingBaseline','Monitoring','StopRequested','Stopping','CapturingFinalState','Comparing');";
            if (Convert.ToInt32(active.ExecuteScalar()) != 0)
                throw new InvalidOperationException("An earlier monitoring session is still active or needs recovery review.");
        }
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO InstallMonitorSessions
                (SessionId,Name,ScopePath,InstallerPath,InstallerPublisher,InstallerSignatureStatus,State,StartedAtUtc,EndedAtUtc,EventCount,ErrorCount,CoverageComplete,WarningsJson,SummaryJson,BaselineJson,CpuSeconds,MemoryDeltaBytes)
            VALUES ($id,$name,$scope,$installer,$publisher,$signature,$state,$start,$end,$events,$errors,$coverage,$warnings,$summary,$baseline,$cpu,$memory);
            """;
        BindMonitorSession(cmd, session);
        cmd.Parameters.AddWithValue("$baseline", baselineJson);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void UpdateMonitorSession(InstallMonitorSession session, string? baselineJson = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE InstallMonitorSessions SET Name=$name,ScopePath=$scope,InstallerPath=$installer,InstallerPublisher=$publisher,
                InstallerSignatureStatus=$signature,State=$state,StartedAtUtc=$start,EndedAtUtc=$end,EventCount=$events,
                ErrorCount=$errors,CoverageComplete=$coverage,WarningsJson=$warnings,SummaryJson=$summary,
                BaselineJson=COALESCE($baseline,BaselineJson),CpuSeconds=$cpu,MemoryDeltaBytes=$memory WHERE SessionId=$id;
            """;
        BindMonitorSession(cmd, session);
        cmd.Parameters.AddWithValue("$baseline", (object?)baselineJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void BindMonitorSession(SqliteCommand cmd, InstallMonitorSession s)
    {
        cmd.Parameters.AddWithValue("$id", s.SessionId); cmd.Parameters.AddWithValue("$name", s.Name);
        cmd.Parameters.AddWithValue("$scope", s.ScopePath); cmd.Parameters.AddWithValue("$installer", s.InstallerPath);
        cmd.Parameters.AddWithValue("$publisher", s.InstallerPublisher); cmd.Parameters.AddWithValue("$signature", s.InstallerSignatureStatus);
        cmd.Parameters.AddWithValue("$state", s.State); cmd.Parameters.AddWithValue("$start", s.StartedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$end", (object?)s.EndedAtUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$events", s.EventCount); cmd.Parameters.AddWithValue("$errors", s.ErrorCount);
        cmd.Parameters.AddWithValue("$coverage", s.CoverageComplete ? 1 : 0); cmd.Parameters.AddWithValue("$warnings", s.WarningsJson);
        cmd.Parameters.AddWithValue("$summary", s.SummaryJson); cmd.Parameters.AddWithValue("$cpu", s.CpuSeconds);
        cmd.Parameters.AddWithValue("$memory", s.MemoryDeltaBytes);
    }

    public void AddMonitorEvents(IReadOnlyList<InstallMonitorEvent> events)
    {
        if (events.Count == 0) return;
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        foreach (var e in events)
        {
            using var cmd = conn.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO InstallMonitorEvents(EventId,SessionId,TimestampUtc,Category,ChangeType,Target,PreviousMetadata,NewMetadata,Association,Source,ProcessId,Diagnostic)
                VALUES($event,$session,$time,$category,$kind,$target,$before,$after,$association,$source,$pid,$diagnostic)
                ON CONFLICT(EventId) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$event", e.EventId); cmd.Parameters.AddWithValue("$session", e.SessionId);
            cmd.Parameters.AddWithValue("$time", e.TimestampUtc.ToString("O")); cmd.Parameters.AddWithValue("$category", e.Category);
            cmd.Parameters.AddWithValue("$kind", e.ChangeType); cmd.Parameters.AddWithValue("$target", e.Target);
            cmd.Parameters.AddWithValue("$before", e.PreviousMetadata); cmd.Parameters.AddWithValue("$after", e.NewMetadata);
            cmd.Parameters.AddWithValue("$association", e.Association.ToString()); cmd.Parameters.AddWithValue("$source", e.Source);
            cmd.Parameters.AddWithValue("$pid", (object?)e.ProcessId ?? DBNull.Value); cmd.Parameters.AddWithValue("$diagnostic", e.Diagnostic ?? "");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<InstallMonitorSession> GetMonitorSessions(int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var result = new List<InstallMonitorSession>();
        using var conn = new SqliteConnection(_connectionString); conn.Open();
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT SessionId,Name,ScopePath,InstallerPath,InstallerPublisher,InstallerSignatureStatus,State,StartedAtUtc,EndedAtUtc,EventCount,ErrorCount,CoverageComplete,WarningsJson,SummaryJson,CpuSeconds,MemoryDeltaBytes FROM InstallMonitorSessions ORDER BY StartedAtUtc DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(ReadMonitorSession(reader));
        return result;
    }

    public InstallMonitorSession? GetMonitorSession(string sessionId)
    {
        using var conn = new SqliteConnection(_connectionString); conn.Open();
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT SessionId,Name,ScopePath,InstallerPath,InstallerPublisher,InstallerSignatureStatus,State,StartedAtUtc,EndedAtUtc,EventCount,ErrorCount,CoverageComplete,WarningsJson,SummaryJson,CpuSeconds,MemoryDeltaBytes FROM InstallMonitorSessions WHERE SessionId=$id;";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMonitorSession(reader) : null;
    }

    private static InstallMonitorSession ReadMonitorSession(SqliteDataReader r) => new(
        r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),
        DateTimeOffset.Parse(r.GetString(7)),r.IsDBNull(8)?null:DateTimeOffset.Parse(r.GetString(8)),r.GetInt32(9),r.GetInt32(10),
        r.GetInt32(11)==1,r.GetString(12),r.GetString(13),r.GetDouble(14),r.GetInt64(15));

    public List<InstallMonitorEvent> GetMonitorEvents(string sessionId, int offset = 0, int limit = 200)
    {
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 500);
        var result = new List<InstallMonitorEvent>();
        using var conn = new SqliteConnection(_connectionString); conn.Open();
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT EventId,SessionId,TimestampUtc,Category,ChangeType,Target,PreviousMetadata,NewMetadata,Association,Source,ProcessId,Diagnostic FROM InstallMonitorEvents WHERE SessionId=$id ORDER BY TimestampUtc DESC,EventId DESC LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$id", sessionId); cmd.Parameters.AddWithValue("$limit", limit); cmd.Parameters.AddWithValue("$offset", offset);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(new InstallMonitorEvent(reader.GetString(0),reader.GetString(1),DateTimeOffset.Parse(reader.GetString(2)),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetString(7),Enum.TryParse<MonitorAssociation>(reader.GetString(8),out var a)?a:MonitorAssociation.UnknownAssociation,reader.GetString(9),reader.IsDBNull(10)?null:reader.GetInt32(10),reader.GetString(11)));
        return result;
    }

    public void MarkInterruptedMonitorSessions()
    {
        using var conn = new SqliteConnection(_connectionString); conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE InstallMonitorSessions SET State='Interrupted',EndedAtUtc=$now,CoverageComplete=0,WarningsJson=CASE WHEN WarningsJson='[]' THEN '[\"Application exited before final comparison; captured evidence is retained.\"]' ELSE WarningsJson END WHERE State IN ('Preparing','CapturingBaseline','Monitoring','StopRequested','Stopping','CapturingFinalState','Comparing');";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); cmd.ExecuteNonQuery();
    }

    public string? GetMonitorBaseline(string sessionId)
    {
        using var conn = new SqliteConnection(_connectionString); conn.Open();
        using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT BaselineJson FROM InstallMonitorSessions WHERE SessionId=$id;"; cmd.Parameters.AddWithValue("$id", sessionId);
        return cmd.ExecuteScalar() as string;
    }

}
