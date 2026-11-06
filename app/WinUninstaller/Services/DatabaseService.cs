using System.IO;
using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class DatabaseService
{
    internal const int CurrentSchemaVersion = 1;
    private static readonly ConcurrentDictionary<string, object> SchemaLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] RequiredHistoryColumns =
    [
        "Id", "AppId", "AppName", "Publisher", "Version", "CachedIconPath", "UninstallDate", "ExitCode", "Success",
        "Notes", "InstallerType", "ResidualStatus", "CleanedItemsCount", "CleanedSizeBytes", "Outcome", "ExitObserved"
    ];
    private readonly string _connectionString;
    private readonly string _databasePath;
    internal string DataDirectory { get; }
#if ENGINE_TESTS
    internal static Action? BeforeSchemaCommitForTests;
#endif
    private static string DefaultDatabasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinUninstaller", "winuninstaller.db");

    public DatabaseService(string? databasePath = null)
    {
        var dbPath = databasePath ?? DefaultDatabasePath;
        _databasePath = Path.GetFullPath(dbPath);
        DataDirectory = Path.GetDirectoryName(_databasePath)!;
        Directory.CreateDirectory(DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();
        lock (SchemaLocks.GetOrAdd(_databasePath, static _ => new object())) EnsureSchema();
    }

    internal static DatabaseService OpenWithRecovery(string? databasePath, out string? preservedDatabase)
    {
        var path = Path.GetFullPath(databasePath ?? DefaultDatabasePath);
        preservedDatabase = null;
        try { return new DatabaseService(path); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            // Keep the original database and its sidecars in place for recovery.
            var recovered = new DatabaseService(Path.ChangeExtension(path, ".recovery.db"));
            preservedDatabase = path;
            return recovered;
        }
    }

    partial void EnsureRetiredSchema(SqliteConnection connection, SqliteTransaction transaction);

    private void EnsureSchema()
    {
        if (File.Exists(_databasePath) && new FileInfo(_databasePath).Length > 0)
        {
            using var inspection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            inspection.Open();
            var version = ReadSchemaVersion(inspection);
            if (version > CurrentSchemaVersion)
                throw new InvalidDataException($"Database schema {version} is newer than supported schema {CurrentSchemaVersion}.");
            if (NeedsMigration(inspection, version)) CreateMigrationBackup(version);
        }

        // A failed startup must release the original file for backup or repair.
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { Pooling = false }.ToString());
        conn.Open();
        using var transaction = conn.BeginTransaction(deferred: false);
        var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS UninstallHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AppId TEXT NOT NULL,
                AppName TEXT NOT NULL,
                Publisher TEXT,
                Version TEXT,
                CachedIconPath TEXT,
                UninstallDate TEXT NOT NULL,
                ExitCode INTEGER NOT NULL DEFAULT 0,
                Success INTEGER NOT NULL DEFAULT 0,
                Notes TEXT,
                InstallerType TEXT DEFAULT 'StandardExe',
                ResidualStatus TEXT DEFAULT 'Not Scanned',
                CleanedItemsCount INTEGER DEFAULT 0,
                CleanedSizeBytes INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS UserSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            """;
        cmd.ExecuteNonQuery();
        EnsureRetiredSchema(conn, transaction);

        // Migrate any missing columns if needed
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var colCmd = conn.CreateCommand())
        {
            colCmd.Transaction = transaction;
            colCmd.CommandText = "PRAGMA table_info(UninstallHistory);";
            using var reader = colCmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        void AddColIfNotExists(string colName, string colType)
        {
            if (!existingCols.Contains(colName))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.Transaction = transaction;
                alterCmd.CommandText = $"ALTER TABLE UninstallHistory ADD COLUMN {colName} {colType};";
                alterCmd.ExecuteNonQuery();
            }
        }

        AddColIfNotExists("InstallerType", "TEXT DEFAULT 'StandardExe'");
        AddColIfNotExists("ResidualStatus", "TEXT DEFAULT 'Not Scanned'");
        AddColIfNotExists("CleanedItemsCount", "INTEGER DEFAULT 0");
        AddColIfNotExists("CleanedSizeBytes", "INTEGER DEFAULT 0");
        AddColIfNotExists("Outcome", "TEXT DEFAULT ''");
        AddColIfNotExists("ExitObserved", "INTEGER");
        using (var version = conn.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            version.ExecuteNonQuery();
        }
#if ENGINE_TESTS
        BeforeSchemaCommitForTests?.Invoke();
#endif
        transaction.Commit();
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool NeedsMigration(SqliteConnection connection, int version)
    {
        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = tables.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        if (names.Count == 0) return false;
        if (version < CurrentSchemaVersion || !names.Contains("UserSettings") || !names.Contains("UninstallHistory")) return true;
        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(UninstallHistory);";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = columns.ExecuteReader()) while (reader.Read()) existing.Add(reader.GetString(1));
        return RequiredHistoryColumns.Any(column => !existing.Contains(column));
    }

    private void CreateMigrationBackup(int previousVersion)
    {
        var temporary = Path.Combine(DataDirectory, "." + Path.GetFileName(_databasePath) + ".schema-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        var backup = _databasePath + $".pre-schema-v{previousVersion}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString()))
            {
                source.Open(); destination.Open(); source.BackupDatabase(destination);
            }
            using (var durable = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.Read)) durable.Flush(flushToDisk: true);
            File.Move(temporary, backup);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public int AddRecord(UninstallRecord record)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UninstallHistory 
                (AppId, AppName, Publisher, Version, CachedIconPath, UninstallDate, ExitCode, Success, Notes, InstallerType, ResidualStatus, CleanedItemsCount, CleanedSizeBytes, Outcome, ExitObserved)
            VALUES 
                ($appId, $name, $pub, $ver, $icon, $date, $exit, $success, $notes, $type, $resStatus, $resCount, $resSize, $outcome, $exitObserved);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$appId", record.AppId);
        cmd.Parameters.AddWithValue("$name", record.AppName);
        cmd.Parameters.AddWithValue("$pub", record.Publisher ?? "");
        cmd.Parameters.AddWithValue("$ver", record.Version ?? "");
        cmd.Parameters.AddWithValue("$icon", record.CachedIconPath ?? "");
        cmd.Parameters.AddWithValue("$date", record.UninstallDate);
        cmd.Parameters.AddWithValue("$exit", record.ExitCode);
        cmd.Parameters.AddWithValue("$exitObserved", (object?)record.ExitObserved ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$success", record.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$notes", record.Notes ?? "");
        cmd.Parameters.AddWithValue("$type", record.InstallerType ?? "StandardExe");
        cmd.Parameters.AddWithValue("$resStatus", record.ResidualStatus ?? "Not Scanned");
        cmd.Parameters.AddWithValue("$resCount", record.CleanedItemsCount);
        cmd.Parameters.AddWithValue("$resSize", record.CleanedSizeBytes);
        cmd.Parameters.AddWithValue("$outcome", record.Outcome ?? "");

        var id = Convert.ToInt32(cmd.ExecuteScalar());
        record.Id = id;
        return id;
    }

    public void AppendRecordNote(int recordId, string note)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE UninstallHistory SET Notes = COALESCE(Notes, '') || char(10) || $note WHERE Id = $id";
        cmd.Parameters.AddWithValue("$note", note); cmd.Parameters.AddWithValue("$id", recordId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateResidualInfo(int recordId, string status, int cleanedCount, long cleanedBytes)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE UninstallHistory 
            SET ResidualStatus = $status, CleanedItemsCount = $count, CleanedSizeBytes = $bytes
            WHERE Id = $id;
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$count", cleanedCount);
        cmd.Parameters.AddWithValue("$bytes", cleanedBytes);
        cmd.Parameters.AddWithValue("$id", recordId);
        cmd.ExecuteNonQuery();
    }

    private const string NonTestHistory = "AppId <> 'DiagnosticTestApp' COLLATE NOCASE AND substr(AppId, 1, 8) <> 'TestApp_' COLLATE NOCASE AND AppName <> 'Automated SQLite Test App' COLLATE NOCASE";

    public int CountHistory(bool includeTestRecords = true, long? beforeId = null)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM UninstallHistory" + HistoryConditions(cmd, includeTestRecords, beforeId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<UninstallRecord> GetHistory(bool includeTestRecords = true) =>
        ReadHistory(includeTestRecords, beforeId: null, limit: null);

    internal List<UninstallRecord> GetHistoryPage(bool includeTestRecords, long? beforeId, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadHistory(includeTestRecords, beforeId, limit);
    }

    private static string HistoryConditions(SqliteCommand command, bool includeTestRecords, long? beforeId)
    {
        if (beforeId is null) return includeTestRecords ? "" : " WHERE " + NonTestHistory;
        command.Parameters.AddWithValue("$beforeId", beforeId.Value);
        return includeTestRecords ? " WHERE Id < $beforeId" : " WHERE Id < $beforeId AND " + NonTestHistory;
    }

    private List<UninstallRecord> ReadHistory(bool includeTestRecords, long? beforeId, int? limit)
    {
        var results = new List<UninstallRecord>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, AppId, AppName, Publisher, Version, CachedIconPath, UninstallDate, ExitCode, Success,
                   Notes, InstallerType, ResidualStatus, CleanedItemsCount, CleanedSizeBytes, Outcome, ExitObserved
            FROM UninstallHistory
            """ + HistoryConditions(cmd, includeTestRecords, beforeId) + " ORDER BY Id DESC";
        if (limit is int pageSize)
        {
            cmd.CommandText += " LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", pageSize);
        }

        using var reader = cmd.ExecuteReader();
        var outcomeColumn = reader.GetOrdinal("Outcome");
        var exitObservedColumn = reader.GetOrdinal("ExitObserved");
        while (reader.Read())
        {
            results.Add(new UninstallRecord
            {
                Id = reader.GetInt32(0),
                AppId = reader.GetString(1),
                AppName = reader.GetString(2),
                Publisher = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Version = reader.IsDBNull(4) ? "" : reader.GetString(4),
                CachedIconPath = reader.IsDBNull(5) ? "" : reader.GetString(5),
                UninstallDate = reader.GetString(6),
                ExitCode = reader.GetInt32(7),
                ExitObserved = reader.IsDBNull(exitObservedColumn) ? null : reader.GetBoolean(exitObservedColumn),
                Success = reader.GetInt32(8) == 1,
                Notes = reader.IsDBNull(9) ? "" : reader.GetString(9),
                InstallerType = reader.IsDBNull(10) ? "StandardExe" : reader.GetString(10),
                ResidualStatus = reader.IsDBNull(11) ? "Not Scanned" : reader.GetString(11),
                CleanedItemsCount = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                CleanedSizeBytes = reader.IsDBNull(13) ? 0 : reader.GetInt64(13),
                Outcome = reader.IsDBNull(outcomeColumn) ? "" : reader.GetString(outcomeColumn)
            });
        }
        return results;
    }

    public void ClearAll()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM UninstallHistory;";
        cmd.ExecuteNonQuery();
    }

    public void ClearHistory() => ClearAll();

    // --- User Settings API ---

    public string GetSetting(string key, string defaultValue = "")
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM UserSettings WHERE Key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        var val = cmd.ExecuteScalar();
        return val is string s ? s : defaultValue;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UserSettings (Key, Value) VALUES ($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value = $v;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
