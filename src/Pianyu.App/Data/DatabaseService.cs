using Microsoft.Data.Sqlite;

namespace Pianyu.App.Data;

public sealed class DatabaseService(AppPaths paths)
{
    private const int CurrentSchemaVersion = 1;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);

    public string DatabasePath => paths.DatabasePath;
    public bool Exists => File.Exists(paths.DatabasePath);

    public async Task<SqliteConnection?> OpenExistingAsync(CancellationToken cancellationToken = default)
    {
        if (!Exists) return null;
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        return connection;
    }

    public async Task<SqliteConnection> OpenWritableAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ConfigureAsync(connection, cancellationToken);
        return connection;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        await _migrationLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(paths.DatabasePath)!;
            Directory.CreateDirectory(directory);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await ConfigureAsync(connection, cancellationToken);

            var version = await GetSchemaVersionAsync(connection, cancellationToken);
            if (version > CurrentSchemaVersion)
            {
                throw new InvalidOperationException($"数据库版本 {version} 高于当前程序支持的版本 {CurrentSchemaVersion}。");
            }

            if (version < 1)
            {
                await ApplyVersion1Async(connection, cancellationToken);
            }
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    private SqliteConnection CreateConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
        ForeignKeys = true
    }.ToString());

    private static async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> GetSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_info';";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return 0;

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT version FROM schema_info LIMIT 1;";
        return Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task ApplyVersion1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE schema_info (
                version INTEGER NOT NULL,
                migrated_at TEXT NOT NULL
            );

            CREATE TABLE snippets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                content_hash TEXT NOT NULL UNIQUE,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                is_pinned INTEGER NOT NULL DEFAULT 0,
                copy_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_used_at TEXT,
                last_used_app TEXT,
                deleted_at TEXT
            );

            CREATE INDEX ix_snippets_active_recent ON snippets(deleted_at, last_used_at DESC);
            CREATE INDEX ix_snippets_created ON snippets(created_at DESC);

            CREATE TABLE tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE
            );

            CREATE TABLE snippet_tags (
                snippet_id INTEGER NOT NULL REFERENCES snippets(id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY(snippet_id, tag_id)
            );

            CREATE TABLE usage_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                snippet_id INTEGER NOT NULL REFERENCES snippets(id) ON DELETE CASCADE,
                used_at TEXT NOT NULL,
                foreground_app TEXT,
                action TEXT NOT NULL
            );
            CREATE INDEX ix_usage_snippet_app ON usage_events(snippet_id, foreground_app, used_at DESC);

            CREATE TABLE search_aliases (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                query TEXT NOT NULL COLLATE NOCASE,
                snippet_id INTEGER NOT NULL REFERENCES snippets(id) ON DELETE CASCADE,
                hit_count INTEGER NOT NULL DEFAULT 1,
                updated_at TEXT NOT NULL,
                UNIQUE(query, snippet_id)
            );

            CREATE TABLE shortcuts (
                action_id TEXT PRIMARY KEY,
                gesture TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE template_values (
                variable_name TEXT NOT NULL COLLATE NOCASE,
                value TEXT NOT NULL,
                used_at TEXT NOT NULL,
                PRIMARY KEY(variable_name, value)
            );

            CREATE VIRTUAL TABLE snippets_fts USING fts5(
                title,
                content,
                tags,
                tokenize='unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER snippets_ai AFTER INSERT ON snippets BEGIN
                INSERT INTO snippets_fts(rowid, title, content, tags) VALUES (new.id, new.title, new.content, '');
            END;
            CREATE TRIGGER snippets_ad AFTER DELETE ON snippets BEGIN
                DELETE FROM snippets_fts WHERE rowid=old.id;
            END;
            CREATE TRIGGER snippets_au AFTER UPDATE OF title, content ON snippets BEGIN
                UPDATE snippets_fts SET title=new.title, content=new.content WHERE rowid=new.id;
            END;

            INSERT INTO schema_info(version, migrated_at) VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
