using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pianyu.App.Data;
using Pianyu.App.Services;
using Pianyu.Core;

namespace Pianyu.Tests;

[TestClass]
public sealed class RepositoryTests
{
    private string _directory = null!;
    private AppPaths _paths = null!;
    private DatabaseService _database = null!;
    private SnippetRepository _repository = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pianyu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _paths = new AppPaths(_directory);
        _database = new DatabaseService(_paths);
        _repository = new SnippetRepository(_database);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestMethod]
    public async Task Database_IsCreatedOnlyOnFirstWrite()
    {
        Assert.IsFalse(File.Exists(_paths.DatabasePath));
        Assert.AreEqual(0, (await _repository.SearchAsync("")).Count);
        Assert.IsFalse(File.Exists(_paths.DatabasePath));
        await _repository.SaveAsync(new Snippet { Title = "第一条", Content = "真实正文" });
        Assert.IsTrue(File.Exists(_paths.DatabasePath));
    }

    [TestMethod]
    public async Task ClipboardCandidate_DoesNotWriteUntilExplicitConfirmation()
    {
        var service = new ClipboardCandidateService(_repository);
        var candidate = service.Create("第一行标题\n候选正文", fromListener: true);
        Assert.IsTrue(candidate.FromListener);
        Assert.AreEqual("第一行标题", candidate.Snippet.Title);
        Assert.IsFalse(File.Exists(_paths.DatabasePath));
        Assert.AreEqual(0, (await _repository.SearchAsync("")).Count);

        await service.ConfirmAsync(candidate.Snippet);
        Assert.IsTrue(File.Exists(_paths.DatabasePath));
        Assert.AreEqual(1, (await _repository.SearchAsync("")).Count);
    }

    [TestMethod]
    public async Task CrudFtsTagsDuplicateAndUndo_WorkTogether()
    {
        var saved = await _repository.SaveAsync(new Snippet { Title = "启动后端", Content = "pnpm backend dev", Tags = ["命令", "开发"] });
        Assert.IsFalse(saved.IsDuplicate);
        var id = saved.Snippet!.Id;

        var byBody = await _repository.SearchAsync("backend");
        var byTag = await _repository.SearchAsync("开发");
        Assert.IsTrue(byBody.Any(item => item.Id == id));
        Assert.IsTrue(byTag.Any(item => item.Id == id));

        var duplicate = await _repository.SaveAsync(new Snippet { Title = "重复", Content = "pnpm backend dev" });
        Assert.IsTrue(duplicate.IsDuplicate);

        var updated = saved.Snippet;
        updated.Content = "pnpm backend test";
        await _repository.SaveAsync(updated);
        Assert.IsTrue((await _repository.SearchAsync("test")).Any(item => item.Id == id));

        await _repository.DeleteAsync(id);
        Assert.IsFalse((await _repository.SearchAsync("test")).Any());
        Assert.IsTrue(await _repository.UndoDeleteAsync(id));
        Assert.IsTrue((await _repository.SearchAsync("test")).Any(item => item.Id == id));
    }

    [TestMethod]
    public async Task AliasLearning_MakesNonLiteralQueryFindSelection()
    {
        var item = (await _repository.SaveAsync(new Snippet { Title = "发布流程", Content = "合入 main 并推送" })).Snippet!;
        await _repository.LearnAliasAsync("fb", item);
        await _repository.LearnAliasAsync("fb", item);
        Assert.IsTrue((await _repository.SearchAsync("fb")).Any(result => result.Id == item.Id));
        var alias = (await _repository.GetAliasesAsync()).Single();
        Assert.AreEqual(2, alias.HitCount);
        await _repository.DeleteAliasAsync(alias.Id);
        Assert.AreEqual(0, (await _repository.GetAliasesAsync()).Count);
    }

    [TestMethod]
    public async Task SearchAndRanking_On5000Rows_CompletesResponsively()
    {
        await _database.EnsureInitializedAsync();
        await using var connection = await _database.OpenWritableAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO snippets(title,content,content_hash,created_at,updated_at) VALUES($title,$content,$hash,$time,$time);";
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var content = command.Parameters.Add("$content", SqliteType.Text);
        var hash = command.Parameters.Add("$hash", SqliteType.Text);
        var time = command.Parameters.Add("$time", SqliteType.Text);
        time.Value = DateTimeOffset.Now.ToString("O");
        for (var i = 0; i < 5000; i++)
        {
            title.Value = $"测试片段 {i}";
            content.Value = i == 4321 ? "唯一目标 cesium 光伏提取" : $"普通片段正文 {i}";
            hash.Value = $"HASH-{i:D8}";
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();

        var stopwatch = Stopwatch.StartNew();
        var candidates = await _repository.SearchAsync("cesium");
        var ranked = new RankingService().Rank(candidates, new RankingContext("cesium", null, DateTimeOffset.Now));
        stopwatch.Stop();
        Assert.IsTrue(ranked.Any(item => item.Content.Contains("光伏提取", StringComparison.Ordinal)));
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"搜索耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [TestMethod]
    public async Task JsonExportImportAndSqliteBackup_PreserveLocalData()
    {
        await _repository.SaveAsync(new Snippet { Title = "备份片段", Content = "backup-content", Tags = ["备份"] });
        var service = new BackupService(_paths, _database, _repository);
        var json = Path.Combine(_directory, "export.json");
        await service.ExportJsonAsync(json);
        Assert.IsTrue(File.Exists(json));
        var backup = await service.BackupAsync();
        Assert.IsTrue(File.Exists(backup));

        var importDirectory = Path.Combine(Path.GetTempPath(), "pianyu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(importDirectory);
        try
        {
            var importPaths = new AppPaths(importDirectory);
            var importRepository = new SnippetRepository(new DatabaseService(importPaths));
            var importService = new BackupService(importPaths, new DatabaseService(importPaths), importRepository);
            var summary = await importService.ImportJsonAsync(json);
            Assert.AreEqual(1, summary.Created);
            Assert.IsTrue((await importRepository.SearchAsync("backup-content")).Any());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(importDirectory, true);
        }
    }
}
