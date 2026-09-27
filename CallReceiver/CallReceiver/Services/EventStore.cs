using Microsoft.Data.Sqlite;
using System.Text.Json;
using CallReceiver.Models;

namespace CallReceiver.Services;

public sealed class EventStore
{
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1, 1);
    public EventStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
    }
    private async Task<SqliteConnection> OpenAsync()
    {
        var db = new SqliteConnection(connectionString);
        await db.OpenAsync();
        return db;
    }
    public async Task InitializeAsync()
    {
        await using var db = await OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events(
              sequence INTEGER PRIMARY KEY AUTOINCREMENT,
              event_id TEXT NOT NULL UNIQUE COLLATE NOCASE,
              payload TEXT NOT NULL,
              shown_at INTEGER NULL);
            CREATE INDEX IF NOT EXISTS pending_events ON events(shown_at, sequence);
            """;
        await command.ExecuteNonQueryAsync();
        await PruneAsync();
    }
    public async Task<bool> AcceptAsync(CallEvent value)
    {
        await gate.WaitAsync();
        try
        {
            await using var db = await OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO events(event_id,payload) VALUES ($id,$payload)";
            command.Parameters.AddWithValue("$id", value.EventId);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value, JsonDefaults.Options));
            return await command.ExecuteNonQueryAsync() == 1;
        }
        finally { gate.Release(); }
    }
    public async Task<CallEvent?> NextAsync()
    {
        await using var db = await OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT payload FROM events WHERE shown_at IS NULL ORDER BY sequence LIMIT 1";
        var json = await command.ExecuteScalarAsync() as string;
        return json is null ? null : JsonSerializer.Deserialize<CallEvent>(json, JsonDefaults.Options);
    }
    public async Task MarkShownAsync(string id)
    {
        await using var db = await OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE events SET shown_at=$time WHERE event_id=$id";
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }
    public async Task<int> PendingCountAsync()
    {
        await using var db = await OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM events WHERE shown_at IS NULL";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
    public async Task PruneAsync()
    {
        await using var db = await OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM events WHERE shown_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync();
    }
}
