using System.IO;
using Microsoft.Data.Sqlite;
using WinUninstaller.Models;

namespace WinUninstaller.Services;

public partial class DatabaseService
{
    private readonly string _connectionString;

    public DatabaseService(string? databasePath = null)
    {
        var defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUninstaller");
        var dbPath = databasePath ?? Path.Combine(defaultFolder, "winuninstaller.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        EnsureSchema();
    }

    partial void EnsureRetiredSchema(SqliteConnection connection, SqliteTransaction transaction);

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
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
        transaction.Commit();
    }

    public int AddRecord(UninstallRecord record)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UninstallHistory 
                (AppId, AppName, Publisher, Version, CachedIconPath, UninstallDate, ExitCode, Success, Notes, InstallerType, ResidualStatus, CleanedItemsCount, CleanedSizeBytes, Outcome)
            VALUES 
                ($appId, $name, $pub, $ver, $icon, $date, $exit, $success, $notes, $type, $resStatus, $resCount, $resSize, $outcome);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$appId", record.AppId);
        cmd.Parameters.AddWithValue("$name", record.AppName);
        cmd.Parameters.AddWithValue("$pub", record.Publisher ?? "");
        cmd.Parameters.AddWithValue("$ver", record.Version ?? "");
        cmd.Parameters.AddWithValue("$icon", record.CachedIconPath ?? "");
        cmd.Parameters.AddWithValue("$date", record.UninstallDate);
        cmd.Parameters.AddWithValue("$exit", record.ExitCode);
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

    public List<UninstallRecord> GetHistory(bool includeTestRecords = true)
    {
        var results = new List<UninstallRecord>();
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = includeTestRecords
            ? "SELECT * FROM UninstallHistory ORDER BY Id DESC;"
            : "SELECT * FROM UninstallHistory WHERE AppId <> 'DiagnosticTestApp' COLLATE NOCASE AND substr(AppId, 1, 8) <> 'TestApp_' COLLATE NOCASE AND AppName <> 'Automated SQLite Test App' COLLATE NOCASE ORDER BY Id DESC;";

        using var reader = cmd.ExecuteReader();
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
                Success = reader.GetInt32(8) == 1,
                Notes = reader.IsDBNull(9) ? "" : reader.GetString(9),
                InstallerType = reader.IsDBNull(10) ? "StandardExe" : reader.GetString(10),
                ResidualStatus = reader.IsDBNull(11) ? "Not Scanned" : reader.GetString(11),
                CleanedItemsCount = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                CleanedSizeBytes = reader.IsDBNull(13) ? 0 : reader.GetInt64(13),
                Outcome = reader.IsDBNull(reader.GetOrdinal("Outcome")) ? "" : reader.GetString(reader.GetOrdinal("Outcome"))
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
