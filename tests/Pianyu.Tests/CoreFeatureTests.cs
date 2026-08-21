using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Data.Sqlite;
using Pianyu.App.Data;
using Pianyu.App.Services;
using Pianyu.Core;

namespace Pianyu.Tests;

[TestClass]
public sealed class RankingServiceTests
{
    [TestMethod]
    public void SmartRanking_IsDeterministic_AndHonorsSignals()
    {
        var now = new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.FromHours(8));
        var ordinary = new Snippet { Id = 1, Title = "普通", SearchRank = 20, CreatedAt = now.AddDays(-30) };
        var preferred = new Snippet { Id = 2, Title = "常用", SearchRank = 20, IsFavorite = true, IsPinned = true, CopyCount = 30, LastUsedAt = now.AddHours(-1), LastUsedApp = "devenv", CreatedAt = now.AddDays(-30) };
        var service = new RankingService();
        var context = new RankingContext("test", "devenv", now);

        var first = service.Rank([ordinary, preferred], context);
        var second = service.Rank([ordinary, preferred], context);

        Assert.AreEqual(2L, first[0].Id);
        CollectionAssert.AreEqual(first.Select(x => x.Id).ToList(), second.Select(x => x.Id).ToList());
    }

    [TestMethod]
    public void AppAwareness_AdjustsOrder_ButDoesNotHideResults()
    {
        var now = DateTimeOffset.Parse("2026-08-21T12:00:00+08:00");
        var appMatch = new Snippet { Id = 1, Title = "IDE", SearchRank = 10, LastUsedApp = "code", CreatedAt = now.AddDays(-2) };
        var other = new Snippet { Id = 2, Title = "Other", SearchRank = 12, LastUsedApp = "notepad", CreatedAt = now.AddDays(-2) };
        var result = new RankingService().Rank([other, appMatch], new RankingContext("", "code", now));
        Assert.AreEqual(2, result.Count);
        Assert.AreEqual(1L, result[0].Id);
    }
}

[TestClass]
public sealed class TextFeatureTests
{
    [TestMethod]
    public void TemplateEngine_ParsesDefaults_AndRendersValues()
    {
        const string source = "启动 backend，端口为 {port=3001}，分支为 {branch=feature_demo}";
        var variables = TemplateEngine.Parse(source);
        Assert.AreEqual(2, variables.Count);
        Assert.AreEqual("3001", variables[0].DefaultValue);
        var rendered = TemplateEngine.Render(source, new Dictionary<string, string> { ["port"] = "3002", ["branch"] = "main" });
        Assert.AreEqual("启动 backend，端口为 3002，分支为 main", rendered);
    }

    [TestMethod]
    public void SearchText_SupportsPinyinInitials_AndTypos()
    {
        Assert.IsTrue(SearchText.GetPinyinInitials("启动后端").StartsWith("qdh", StringComparison.Ordinal));
        Assert.IsTrue(SearchText.IsFuzzyMatch("backedn", "backend"));
    }

    [TestMethod]
    public void ShortcutParser_RejectsBareModifier_AndFindsConflicts()
    {
        Assert.IsFalse(ShortcutService.TryParseGesture("Ctrl", out _, out _, out _));
        Assert.IsTrue(ShortcutService.TryParseGesture("Ctrl+Alt+S", out _, out _, out _));
        var definitions = ShortcutService.Defaults.ToList();
        var conflict = ShortcutService.FindInternalConflict("new", "Ctrl+F", definitions);
        StringAssert.Contains(conflict, "聚焦搜索");
        Assert.IsTrue(ShortcutService.GestureMatches("Enter", "Return"));
        Assert.IsTrue(ShortcutService.GestureMatches("Esc", "Escape"));
        Assert.IsTrue(ShortcutService.TryParseGesture("Del", out _, out var deleteKey, out _));
        Assert.AreEqual(System.Windows.Input.Key.Delete, deleteKey);
        Assert.IsTrue(ShortcutService.GestureMatches("Del", "Delete"));
    }

    [TestMethod]
    public void DirectPaste_UsesWin64InputStructureLayout()
    {
        Assert.AreEqual(40, DirectPasteService.NativeInputStructureSize);
    }

    [TestMethod]
    public async Task SaveAsync_ReusesContentAfterPermanentDeleteWithoutHashConflict()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pianyu-regression-{Guid.NewGuid():N}");
        try
        {
            var repository = new SnippetRepository(new DatabaseService(new AppPaths(directory)));
            var original = new Snippet { Title = "原始片段", Content = "相同正文用于唯一 hash 回归测试" };
            var created = await repository.SaveAsync(original);
            Assert.IsFalse(created.IsDuplicate);

            await repository.DeleteAsync(original.Id);

            var restored = await repository.SaveAsync(new Snippet { Title = "重新录入", Content = original.Content });
            Assert.IsFalse(restored.IsDuplicate);
            Assert.AreNotEqual(original.Id, restored.Snippet!.Id);
            Assert.AreEqual(1, (await repository.GetAllAsync()).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    try { Directory.Delete(directory, recursive: true); break; }
                    catch (IOException) when (attempt < 4) { Thread.Sleep(50); }
                }
            }
        }
    }
}
