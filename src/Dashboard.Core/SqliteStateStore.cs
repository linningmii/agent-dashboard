using System.Text.Json;
using Dashboard.Contracts;
using Microsoft.Data.Sqlite;

namespace Dashboard.Core;

// One transactional aggregate document keeps the migration small while retaining durable SQLite commits.
// Schema versioning leaves room to normalize tables without changing the reporting protocol.
public sealed class SqliteStateStore<T> where T : new()
{
    private readonly string connectionString;
    private readonly object gate = new();
    public SqliteStateStore(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
        if (!File.Exists(file)) { using var empty = File.Create(file); }
        Credentials.Protect(file);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, json TEXT NOT NULL);";
        command.ExecuteNonQuery(); Credentials.Protect(file);
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public TResult Read<TResult>(Func<T, TResult> read) => Execute(read, false);
    public TResult Update<TResult>(Func<T, TResult> update) => Execute(update, true);
    private TResult Execute<TResult>(Func<T, TResult> action, bool write)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            using var query = db.CreateCommand(); query.Transaction = tx; query.CommandText = "SELECT json FROM state WHERE id=1";
            var state = query.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Protocol.Json) ?? throw new InvalidDataException("Invalid state") : new T();
            var result = action(state);
            if (write)
            {
                using var save = db.CreateCommand(); save.Transaction = tx;
                save.CommandText = "INSERT INTO state(id,version,json) VALUES(1,1,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
                save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, Protocol.Json)); save.ExecuteNonQuery();
            }
            tx.Commit(); return result;
        }
    }
}
