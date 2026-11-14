using System.Globalization;
using Microsoft.Data.Sqlite;
using MyUninstaller.Core.Abstractions;
using MyUninstaller.Core.Safety;

namespace MyUninstaller.Infrastructure.Safety;

/// <summary>
/// Jurnal sesi dan riwayat di SQLite (%ProgramData%\MyUninstaller\riwayat.db). Setiap operasi membuka
/// koneksi sendiri (aman dipakai dari banyak utas); mode WAL supaya catatan tidak hilang saat crash.
/// </summary>
public sealed class SqliteSessionJournal : ISessionJournal
{
    private const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly IClock _clock;
    private readonly Lock _gate = new();

    public SqliteSessionJournal(AppPaths paths, IClock clock)
        : this(paths.DatabaseFile, clock)
    {
    }

    public SqliteSessionJournal(string databaseFile, IClock clock)
    {
        _clock = clock;
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false, // berkas bisa dihapus/dipindah tanpa koneksi tertahan (penting juga untuk tes)
        }.ToString();
        EnsureSchema();
    }

    public string StartSession(string mode, string title)
    {
        DateTimeOffset now = _clock.Now;
        string id = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        Execute(
            "INSERT INTO sessions (id, started_at, mode, title, status) VALUES ($id, $at, $mode, $title, $status)",
            ("$id", id), ("$at", Format(now)), ("$mode", mode), ("$title", title), ("$status", JournalSessionStatus.Running.ToString()));
        return id;
    }

    public void FinishSession(string sessionId, JournalSessionStatus status, string? note = null) =>
        Execute(
            "UPDATE sessions SET finished_at = $at, status = $status, note = COALESCE($note, note) WHERE id = $id",
            ("$at", Format(_clock.Now)), ("$status", status.ToString()), ("$note", note), ("$id", sessionId));

    public long LogPending(string sessionId, JournalActionKind kind, string target, string? backupPath = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO actions (session_id, kind, target, backup_path, status, created_at)
                VALUES ($session, $kind, $target, $backup, $status, $at);
                SELECT last_insert_rowid();
                """;
            Add(command, ("$session", sessionId), ("$kind", kind.ToString()), ("$target", target),
                ("$backup", backupPath), ("$status", JournalActionStatus.Pending.ToString()), ("$at", Format(_clock.Now)));
            return (long)command.ExecuteScalar()!;
        }
    }

    public void Complete(long actionId, JournalActionStatus status, string? detail = null, string? backupPath = null) =>
        Execute(
            "UPDATE actions SET status = $status, detail = COALESCE($detail, detail), backup_path = COALESCE($backup, backup_path), updated_at = $at WHERE id = $id",
            ("$status", status.ToString()), ("$detail", detail), ("$backup", backupPath), ("$at", Format(_clock.Now)), ("$id", actionId));

    public IReadOnlyList<JournalSession> GetSessions(int limit = 200) =>
        QuerySessions("SELECT * FROM sessions ORDER BY started_at DESC LIMIT $limit", ("$limit", limit));

    public IReadOnlyList<JournalSession> GetUnfinishedSessions() =>
        QuerySessions("SELECT * FROM sessions WHERE status = $status ORDER BY started_at", ("$status", JournalSessionStatus.Running.ToString()));

    public IReadOnlyList<JournalAction> GetActions(string sessionId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM actions WHERE session_id = $session ORDER BY id";
            Add(command, ("$session", sessionId));

            var list = new List<JournalAction>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new JournalAction(
                    reader.GetInt64(reader.GetOrdinal("id")),
                    reader.GetString(reader.GetOrdinal("session_id")),
                    Enum.Parse<JournalActionKind>(reader.GetString(reader.GetOrdinal("kind"))),
                    reader.GetString(reader.GetOrdinal("target")),
                    GetNullableString(reader, "backup_path"),
                    Enum.Parse<JournalActionStatus>(reader.GetString(reader.GetOrdinal("status"))),
                    GetNullableString(reader, "detail"),
                    Parse(reader.GetString(reader.GetOrdinal("created_at")))));
            }

            return list;
        }
    }

    private IReadOnlyList<JournalSession> QuerySessions(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            Add(command, parameters);

            var list = new List<JournalSession>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? finished = GetNullableString(reader, "finished_at");
                list.Add(new JournalSession(
                    reader.GetString(reader.GetOrdinal("id")),
                    Parse(reader.GetString(reader.GetOrdinal("started_at"))),
                    finished is null ? null : Parse(finished),
                    reader.GetString(reader.GetOrdinal("mode")),
                    reader.GetString(reader.GetOrdinal("title")),
                    Enum.Parse<JournalSessionStatus>(reader.GetString(reader.GetOrdinal("status"))),
                    GetNullableString(reader, "note")));
            }

            return list;
        }
    }

    private void EnsureSchema()
    {
        lock (_gate)
        {
            using var connection = Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL;";
                pragma.ExecuteNonQuery();
            }

            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            long current = (long)version.ExecuteScalar()!;
            if (current >= SchemaVersion)
            {
                return;
            }

            using var create = connection.CreateCommand();
            create.CommandText = $"""
                CREATE TABLE IF NOT EXISTS sessions (
                    id          TEXT PRIMARY KEY,
                    started_at  TEXT NOT NULL,
                    finished_at TEXT,
                    mode        TEXT NOT NULL,
                    title       TEXT NOT NULL,
                    status      TEXT NOT NULL,
                    note        TEXT
                );
                CREATE TABLE IF NOT EXISTS actions (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id  TEXT NOT NULL REFERENCES sessions(id),
                    kind        TEXT NOT NULL,
                    target      TEXT NOT NULL,
                    backup_path TEXT,
                    status      TEXT NOT NULL,
                    detail      TEXT,
                    created_at  TEXT NOT NULL,
                    updated_at  TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_actions_session ON actions(session_id);
                PRAGMA user_version = {SchemaVersion};
                """;
            create.ExecuteNonQuery();
        }
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            Add(command, parameters);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Add(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
