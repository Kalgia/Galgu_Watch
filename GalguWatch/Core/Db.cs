using Microsoft.Data.Sqlite;

namespace GalguWatch.Core;

public class Db
{
    private readonly string _connString;

    public Db(string path)
    {
        _connString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Scalar<string>("PRAGMA journal_mode=WAL;");
        NonQuery("""
            CREATE TABLE IF NOT EXISTS sessions(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              date TEXT NOT NULL,
              started_at TEXT NOT NULL,
              ended_at TEXT NOT NULL,
              duration_sec INTEGER NOT NULL,
              tag_id INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_sessions_date ON sessions(date);
            CREATE TABLE IF NOT EXISTS screenshots(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              date TEXT NOT NULL,
              taken_at TEXT NOT NULL,
              kind TEXT NOT NULL,
              file_path TEXT NOT NULL,
              memo TEXT,
              session_id INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_shots_date ON screenshots(date);
            CREATE TABLE IF NOT EXISTS notes(
              date TEXT PRIMARY KEY,
              content_md TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tags(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL,
              color TEXT
            );
            CREATE TABLE IF NOT EXISTS goals(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              title TEXT NOT NULL,
              due_date TEXT,
              done INTEGER NOT NULL DEFAULT 0,
              done_at TEXT,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings(
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            """);
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=3000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public void NonQuery(string sql, params (string Name, object? Value)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] ps)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        if (r == null || r is DBNull) return default;
        return (T)Convert.ChangeType(r, typeof(T));
    }
}
