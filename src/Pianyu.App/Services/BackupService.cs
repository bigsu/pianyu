using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pianyu.App.Data;
using Pianyu.Core;

namespace Pianyu.App.Services;

public sealed record SnippetExport(int Version, DateTimeOffset ExportedAt, List<Snippet> Snippets);

public sealed class BackupService(AppPaths paths, DatabaseService database, SnippetRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task ExportJsonAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        var payload = new SnippetExport(1, DateTimeOffset.Now, (await repository.GetAllAsync(false, cancellationToken)).ToList());
        await using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
        await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, cancellationToken);
    }

    public async Task<ImportSummary> ImportJsonAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var payload = await JsonSerializer.DeserializeAsync<SnippetExport>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("JSON 文件没有可识别的数据。");
        var created = 0;
        var skipped = 0;
        var failed = 0;
        foreach (var item in payload.Snippets)
        {
            try
            {
                item.Id = 0;
                item.CopyCount = 0;
                item.DeletedAt = null;
                var result = await repository.SaveAsync(item, cancellationToken);
                if (result.IsDuplicate) skipped++; else created++;
            }
            catch
            {
                failed++;
            }
        }
        return new ImportSummary(created, skipped, failed);
    }

    public async Task<string> BackupAsync(string? targetPath = null, CancellationToken cancellationToken = default)
    {
        if (!database.Exists) throw new InvalidOperationException("当前还没有数据库可备份。");
        Directory.CreateDirectory(paths.BackupDirectory);
        targetPath ??= Path.Combine(paths.BackupDirectory, $"pianyu-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        await using var source = await database.OpenExistingAsync(cancellationToken) ?? throw new InvalidOperationException("数据库不存在。");
        await using var target = new SqliteConnection($"Data Source={targetPath};Mode=ReadWriteCreate");
        await target.OpenAsync(cancellationToken);
        await Task.Run(() => source.BackupDatabase(target), cancellationToken);
        return targetPath;
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("找不到备份文件。", backupPath);
        await using var verify = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly");
        await verify.OpenAsync(cancellationToken);
        await using var command = verify.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("备份数据库完整性检查失败。");
        }
        await verify.CloseAsync();

        SqliteConnection.ClearAllPools();
        if (database.Exists)
        {
            var safety = Path.Combine(paths.BackupDirectory, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            await BackupAsync(safety, cancellationToken);
        }
        File.Delete(paths.DatabasePath + "-wal");
        File.Delete(paths.DatabasePath + "-shm");
        File.Copy(backupPath, paths.DatabasePath, true);
        await database.EnsureInitializedAsync(cancellationToken);
    }

    public async Task<DatabaseStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        if (!database.Exists) return new DatabaseStats(0, 0, 0);
        await using var connection = await database.OpenExistingAsync(cancellationToken) ?? throw new InvalidOperationException();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM snippets WHERE deleted_at IS NULL),(SELECT count(*) FROM tags);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var size = new FileInfo(paths.DatabasePath).Length;
        return new DatabaseStats(reader.GetInt32(0), reader.GetInt32(1), size);
    }
}
