using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pianyu.Core;

namespace Pianyu.App.Data;

public sealed class SnippetRepository(DatabaseService database)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<Snippet>> SearchAsync(string query, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenExistingAsync(cancellationToken);
        if (connection is null) return [];

        var trimmed = query.Trim();
        List<Snippet> results;
        if (string.IsNullOrEmpty(trimmed))
        {
            results = await QuerySnippetsAsync(connection, "WHERE s.deleted_at IS NULL", null, limit, cancellationToken);
        }
        else
        {
            try
            {
                var ftsQuery = BuildFtsQuery(trimmed);
                results = await QuerySnippetsAsync(connection,
                    "JOIN snippets_fts f ON f.rowid=s.id WHERE s.deleted_at IS NULL AND snippets_fts MATCH $query",
                    command => command.Parameters.AddWithValue("$query", ftsQuery), limit, cancellationToken, "(-bm25(snippets_fts) * 24.0) AS search_rank");
            }
            catch (SqliteException)
            {
                results = [];
            }

            if (results.Count < limit)
            {
                var missing = await QuerySnippetsAsync(connection,
                    "WHERE s.deleted_at IS NULL AND (s.title LIKE $like ESCAPE '\\' OR s.content LIKE $like ESCAPE '\\' OR EXISTS (SELECT 1 FROM snippet_tags st2 JOIN tags t2 ON t2.id=st2.tag_id WHERE st2.snippet_id=s.id AND t2.name LIKE $like ESCAPE '\\'))",
                    command => command.Parameters.AddWithValue("$like", $"%{EscapeLike(trimmed)}%"), limit, cancellationToken);
                foreach (var item in missing.Where(item => results.All(existing => existing.Id != item.Id)))
                {
                    item.SearchRank = 12;
                    results.Add(item);
                }
            }

            var aliases = await FindAliasSnippetIdsAsync(connection, trimmed, cancellationToken);
            foreach (var aliasId in aliases)
            {
                var existing = results.FirstOrDefault(item => item.Id == aliasId);
                if (existing is not null)
                {
                    existing.SearchRank += 38;
                    continue;
                }

                var aliasSnippet = (await QuerySnippetsAsync(connection, "WHERE s.id=$id AND s.deleted_at IS NULL",
                    command => command.Parameters.AddWithValue("$id", aliasId), 1, cancellationToken)).FirstOrDefault();
                if (aliasSnippet is not null)
                {
                    aliasSnippet.SearchRank = 38;
                    results.Add(aliasSnippet);
                }
            }

            foreach (var item in results)
            {
                var searchable = $"{item.Title} {item.Content} {string.Join(' ', item.Tags)}";
                if (SearchText.IsFuzzyMatch(trimmed, searchable)) item.SearchRank += 8;
                if (SearchText.GetPinyinInitials(item.Title).Contains(trimmed, StringComparison.OrdinalIgnoreCase)) item.SearchRank += 18;
            }
        }

        return results.DistinctBy(item => item.Id).Take(limit).ToList();
    }

    public async Task<IReadOnlyList<Snippet>> GetAllAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenExistingAsync(cancellationToken);
        if (connection is null) return [];
        return await QuerySnippetsAsync(connection, includeDeleted ? string.Empty : "WHERE s.deleted_at IS NULL", null, 100000, cancellationToken);
    }

    public async Task<Snippet?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenExistingAsync(cancellationToken);
        if (connection is null) return null;
        return (await QuerySnippetsAsync(connection, "WHERE s.id=$id", command => command.Parameters.AddWithValue("$id", id), 1, cancellationToken)).FirstOrDefault();
    }

    public async Task<(Snippet? Snippet, bool IsDuplicate)> SaveAsync(Snippet snippet, CancellationToken cancellationToken = default)
    {
        var normalizedContent = snippet.Content.Trim();
        if (string.IsNullOrWhiteSpace(normalizedContent)) throw new ArgumentException("片段正文不能为空。", nameof(snippet));
        var hash = ContentHash(normalizedContent);

        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = (SqliteTransaction)transaction;
            duplicate.CommandText = "SELECT id FROM snippets WHERE content_hash=$hash AND id<>$id AND deleted_at IS NULL LIMIT 1;";
            duplicate.Parameters.AddWithValue("$hash", hash);
            duplicate.Parameters.AddWithValue("$id", snippet.Id);
            var existingId = await duplicate.ExecuteScalarAsync(cancellationToken);
            if (existingId is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (await GetByIdAsync(Convert.ToInt64(existingId), cancellationToken), true);
            }
        }

        var now = DateTimeOffset.Now;
        if (snippet.Id == 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO snippets(title, content, content_hash, is_favorite, is_pinned, copy_count, created_at, updated_at)
                VALUES($title,$content,$hash,$favorite,$pinned,0,$now,$now);
                SELECT last_insert_rowid();
                """;
            AddSnippetParameters(insert, snippet, hash, now);
            snippet.Id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
            snippet.CreatedAt = now;
        }
        else
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE snippets SET title=$title, content=$content, content_hash=$hash, is_favorite=$favorite,
                    is_pinned=$pinned, updated_at=$now, deleted_at=NULL WHERE id=$id;
                """;
            AddSnippetParameters(update, snippet, hash, now);
            update.Parameters.AddWithValue("$id", snippet.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceTagsAsync(connection, (SqliteTransaction)transaction, snippet.Id, snippet.Tags, cancellationToken);
        await RefreshFtsTagsAsync(connection, (SqliteTransaction)transaction, snippet.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetByIdAsync(snippet.Id, cancellationToken), false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snippets SET deleted_at=$now, updated_at=$now WHERE id=$id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> UndoDeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE snippets SET deleted_at=NULL, updated_at=$now WHERE id=$id AND deleted_at IS NOT NULL;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task PermanentlyDeleteAsync(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in ids.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM snippets WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordUseAsync(long id, string? foregroundApp, string action, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.Now.ToString("O");
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE snippets SET copy_count=copy_count+1, last_used_at=$now, last_used_app=$app WHERE id=$id;";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$app", (object?)foregroundApp ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO usage_events(snippet_id,used_at,foreground_app,action) VALUES($id,$now,$app,$action);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$app", (object?)foregroundApp ?? DBNull.Value);
            insert.Parameters.AddWithValue("$action", action);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task LearnAliasAsync(string query, Snippet selected, CancellationToken cancellationToken = default)
    {
        query = query.Trim().ToLowerInvariant();
        if (query.Length < 2 || selected.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || selected.Content.Contains(query, StringComparison.OrdinalIgnoreCase)) return;

        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO search_aliases(query,snippet_id,hit_count,updated_at) VALUES($query,$id,1,$now)
            ON CONFLICT(query,snippet_id) DO UPDATE SET hit_count=hit_count+1, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$id", selected.Id);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchAlias>> GetAliasesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenExistingAsync(cancellationToken);
        if (connection is null) return [];
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id,a.query,a.snippet_id,s.title,a.hit_count,a.updated_at
            FROM search_aliases a JOIN snippets s ON s.id=a.snippet_id
            WHERE s.deleted_at IS NULL ORDER BY a.updated_at DESC;
            """;
        var result = new List<SearchAlias>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SearchAlias(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4), DateTimeOffset.Parse(reader.GetString(5))));
        }
        return result;
    }

    public async Task DeleteAliasAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM search_aliases WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenExistingAsync(cancellationToken);
        if (connection is null) return [];
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.name,count(s.id) FROM tags t
            LEFT JOIN snippet_tags st ON st.tag_id=t.id
            LEFT JOIN snippets s ON s.id=st.snippet_id AND s.deleted_at IS NULL
            GROUP BY t.id,t.name ORDER BY t.name COLLATE NOCASE;
            """;
        var result = new List<TagInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new TagInfo(reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    public async Task RenameTagAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        newName = newName.Trim().TrimStart('#');
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("标签名不能为空。");
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var find = connection.CreateCommand();
        find.Transaction = (SqliteTransaction)transaction;
        find.CommandText = "SELECT id FROM tags WHERE name=$name;";
        find.Parameters.AddWithValue("$name", oldName);
        var oldId = await find.ExecuteScalarAsync(cancellationToken);
        if (oldId is null) return;

        await using var ensure = connection.CreateCommand();
        ensure.Transaction = (SqliteTransaction)transaction;
        ensure.CommandText = "INSERT INTO tags(name) VALUES($name) ON CONFLICT(name) DO NOTHING; SELECT id FROM tags WHERE name=$name;";
        ensure.Parameters.AddWithValue("$name", newName);
        var newId = Convert.ToInt64(await ensure.ExecuteScalarAsync(cancellationToken));

        await using var move = connection.CreateCommand();
        move.Transaction = (SqliteTransaction)transaction;
        move.CommandText = "INSERT OR IGNORE INTO snippet_tags(snippet_id,tag_id) SELECT snippet_id,$new FROM snippet_tags WHERE tag_id=$old; DELETE FROM snippet_tags WHERE tag_id=$old; DELETE FROM tags WHERE id=$old;";
        move.Parameters.AddWithValue("$new", newId);
        move.Parameters.AddWithValue("$old", Convert.ToInt64(oldId));
        await move.ExecuteNonQueryAsync(cancellationToken);
        await RebuildAllFtsTagsAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteTagAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM tags WHERE name=$name;";
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await RebuildAllFtsTagsAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddTagsAsync(IEnumerable<long> snippetIds, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var id in snippetIds.Distinct())
        {
            var existing = await GetTagsForSnippetAsync(connection, (SqliteTransaction)transaction, id, cancellationToken);
            await ReplaceTagsAsync(connection, (SqliteTransaction)transaction, id, existing.Concat(tags), cancellationToken);
            await RefreshFtsTagsAsync(connection, (SqliteTransaction)transaction, id, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveTemplateValueAsync(string variable, string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO template_values(variable_name,value,used_at) VALUES($name,$value,$now) ON CONFLICT(variable_name,value) DO UPDATE SET used_at=excluded.used_at;";
        command.Parameters.AddWithValue("$name", variable);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRecentTemplateValuesAsync(string variable, int limit = 5, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenExistingAsync(cancellationToken);
        if (connection is null) return [];
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM template_values WHERE variable_name=$name ORDER BY used_at DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$name", variable);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO settings(key,value,updated_at) VALUES($key,$value,$now) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenExistingAsync(cancellationToken);
        if (connection is null) return null;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<AppSettings> GetAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetSettingAsync("app_settings", cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try { return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings(); }
        catch (JsonException) { return new AppSettings(); }
    }

    public Task SaveAppSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        SetSettingAsync("app_settings", JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetShortcutsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenExistingAsync(cancellationToken);
        if (connection is null) return new Dictionary<string, string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT action_id,gesture FROM shortcuts;";
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public async Task SaveShortcutAsync(string actionId, string gesture, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenWritableAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO shortcuts(action_id,gesture,updated_at) VALUES($id,$gesture,$now) ON CONFLICT(action_id) DO UPDATE SET gesture=excluded.gesture,updated_at=excluded.updated_at;";
        command.Parameters.AddWithValue("$id", actionId);
        command.Parameters.AddWithValue("$gesture", gesture);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<Snippet>> QuerySnippetsAsync(SqliteConnection connection, string where, Action<SqliteCommand>? configure, int limit, CancellationToken cancellationToken, string rankExpression = "0.0 AS search_rank")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id,s.title,s.content,s.is_favorite,s.is_pinned,s.copy_count,s.created_at,s.updated_at,
                   s.last_used_at,s.deleted_at,s.last_used_app,{rankExpression},
                   COALESCE(group_concat(DISTINCT t.name),'') AS tag_names
            FROM snippets s
            LEFT JOIN snippet_tags st ON st.snippet_id=s.id
            LEFT JOIN tags t ON t.id=st.tag_id
            {where}
            GROUP BY s.id
            ORDER BY s.is_pinned DESC, s.last_used_at DESC, s.created_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        configure?.Invoke(command);
        var result = new List<Snippet>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadSnippet(reader));
        return result;
    }

    private static Snippet ReadSnippet(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Title = reader.GetString(1),
        Content = reader.GetString(2),
        IsFavorite = reader.GetBoolean(3),
        IsPinned = reader.GetBoolean(4),
        CopyCount = reader.GetInt32(5),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(7)),
        LastUsedAt = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
        DeletedAt = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
        LastUsedApp = reader.IsDBNull(10) ? null : reader.GetString(10),
        SearchRank = reader.GetDouble(11),
        Tags = reader.GetString(12).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
    };

    private static async Task<List<long>> FindAliasSnippetIdsAsync(SqliteConnection connection, string query, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snippet_id FROM search_aliases WHERE hit_count>=2 AND (query=$query OR query LIKE $prefix) ORDER BY hit_count DESC LIMIT 20;";
        command.Parameters.AddWithValue("$query", query.ToLowerInvariant());
        command.Parameters.AddWithValue("$prefix", $"{EscapeLike(query.ToLowerInvariant())}%");
        var result = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt64(0));
        return result;
    }

    private static void AddSnippetParameters(SqliteCommand command, Snippet snippet, string hash, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(snippet.Title) ? FirstLine(snippet.Content) : snippet.Title.Trim());
        command.Parameters.AddWithValue("$content", snippet.Content.Trim());
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$favorite", snippet.IsFavorite);
        command.Parameters.AddWithValue("$pinned", snippet.IsPinned);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
    }

    private static async Task ReplaceTagsAsync(SqliteConnection connection, SqliteTransaction transaction, long snippetId, IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM snippet_tags WHERE snippet_id=$id;";
            clear.Parameters.AddWithValue("$id", snippetId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var tag in tags.Select(value => value.Trim().TrimStart('#')).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO tags(name) VALUES($name) ON CONFLICT(name) DO NOTHING; INSERT OR IGNORE INTO snippet_tags(snippet_id,tag_id) SELECT $id,id FROM tags WHERE name=$name;";
            command.Parameters.AddWithValue("$name", tag);
            command.Parameters.AddWithValue("$id", snippetId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<List<string>> GetTagsForSnippetAsync(SqliteConnection connection, SqliteTransaction transaction, long snippetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT t.name FROM tags t JOIN snippet_tags st ON st.tag_id=t.id WHERE st.snippet_id=$id;";
        command.Parameters.AddWithValue("$id", snippetId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task RefreshFtsTagsAsync(SqliteConnection connection, SqliteTransaction transaction, long snippetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE snippets_fts SET tags=COALESCE((SELECT group_concat(t.name,' ') FROM tags t JOIN snippet_tags st ON st.tag_id=t.id WHERE st.snippet_id=$id),'') WHERE rowid=$id;
            """;
        command.Parameters.AddWithValue("$id", snippetId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RebuildAllFtsTagsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var ids = connection.CreateCommand();
        ids.Transaction = transaction;
        ids.CommandText = "SELECT id FROM snippets;";
        var values = new List<long>();
        await using (var reader = await ids.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) values.Add(reader.GetInt64(0));
        foreach (var id in values) await RefreshFtsTagsAsync(connection, transaction, id, cancellationToken);
    }

    private static string BuildFtsQuery(string query) => string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(token => $"\"{token.Replace("\"", "\"\"")}\"*"));
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static string ContentHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ReplaceLineEndings("\n").Trim())));
    public static string FirstLine(string content) => content.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) is { Length: > 40 } value ? string.Concat(value.AsSpan(0, 40), "…") : content.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "未命名片段";
}
