using System.Globalization;
using Microsoft.Data.Sqlite;
using QuanShuo.Core.Models;
using QuanShuo.Core.Storage;

namespace QuanShuo.Native.History;

public sealed class RecordIndex
{
    private readonly RecordStore store;
    private readonly string connectionString;
    private readonly SemaphoreSlim syncLock = new(1, 1);

    static RecordIndex() => SQLitePCL.Batteries_V2.Init();

    public RecordIndex(RecordStore store)
    {
        this.store = store;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(store.DataDirectory, ".quanshuo-index.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS records (
                id TEXT PRIMARY KEY,
                markdown_path TEXT NOT NULL UNIQUE,
                timestamp TEXT NOT NULL,
                app_name TEXT NOT NULL,
                window_title TEXT NOT NULL,
                image_path TEXT NOT NULL,
                audio_path TEXT NULL,
                text TEXT NOT NULL,
                status INTEGER NOT NULL,
                modified_ticks INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS records_timestamp ON records(timestamp DESC);
            CREATE VIRTUAL TABLE IF NOT EXISTS records_fts USING fts5(id UNINDEXED, text, app_name);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        await syncLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            IReadOnlyList<QuanShuoRecord> records = await store.ReadAllAsync(cancellationToken);
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var existing = new HashSet<string>(StringComparer.Ordinal);
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)transaction;
                select.CommandText = "SELECT id FROM records";
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));
            }

            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (QuanShuoRecord record in records)
            {
                present.Add(record.Id);
                await UpsertAsync(connection, (SqliteTransaction)transaction, record, cancellationToken);
            }

            foreach (string removed in existing.Except(present))
            {
                await DeleteAsync(connection, (SqliteTransaction)transaction, removed, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            syncLock.Release();
        }
    }

    public async Task<IReadOnlyList<QuanShuoRecord>> QueryAsync(string query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        string trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            command.CommandText = SelectColumns + " ORDER BY timestamp DESC, id DESC LIMIT 5000";
        }
        else
        {
            command.CommandText = SelectColumns + """
                 WHERE text LIKE $like OR app_name LIKE $like OR window_title LIKE $like OR
                       id IN (SELECT id FROM records_fts WHERE records_fts MATCH $fts)
                 ORDER BY timestamp DESC, id DESC LIMIT 5000
                """;
            string like = "%" + trimmed + "%";
            command.Parameters.AddWithValue("$like", like);
            command.Parameters.AddWithValue("$fts", ToFtsQuery(trimmed));
        }

        var results = new List<QuanShuoRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) results.Add(ReadRecord(reader));
        return results;
    }

    private const string SelectColumns = """
        SELECT id, markdown_path, timestamp, app_name, window_title, image_path, audio_path, text, status FROM records
        """;

    private static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction, QuanShuoRecord record, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO records(id, markdown_path, timestamp, app_name, window_title, image_path, audio_path, text, status, modified_ticks)
            VALUES($id, $markdown, $timestamp, $app, $window, $image, $audio, $text, $status, $modified)
            ON CONFLICT(id) DO UPDATE SET markdown_path=excluded.markdown_path, timestamp=excluded.timestamp,
                app_name=excluded.app_name, window_title=excluded.window_title, image_path=excluded.image_path,
                audio_path=excluded.audio_path, text=excluded.text, status=excluded.status, modified_ticks=excluded.modified_ticks;
            DELETE FROM records_fts WHERE id=$id;
            INSERT INTO records_fts(id, text, app_name) VALUES($id, $text, $app);
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$markdown", record.MarkdownPath);
        command.Parameters.AddWithValue("$timestamp", record.Timestamp.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$app", record.AppName);
        command.Parameters.AddWithValue("$window", record.WindowTitle);
        command.Parameters.AddWithValue("$image", record.ImagePath);
        command.Parameters.AddWithValue("$audio", (object?)record.AudioPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", record.Text);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$modified", File.GetLastWriteTimeUtc(record.MarkdownPath).Ticks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteAsync(SqliteConnection connection, SqliteTransaction transaction, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM records WHERE id=$id; DELETE FROM records_fts WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static QuanShuoRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        MarkdownPath = reader.GetString(1),
        Timestamp = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        AppName = reader.GetString(3),
        WindowTitle = reader.GetString(4),
        ImagePath = reader.GetString(5),
        AudioPath = reader.IsDBNull(6) ? null : reader.GetString(6),
        Text = reader.GetString(7),
        Status = (TranscriptionStatus)reader.GetInt32(8),
    };

    private static string ToFtsQuery(string query) => string.Join(" AND ", query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));
}
