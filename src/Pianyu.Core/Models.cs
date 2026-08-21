namespace Pianyu.Core;

public sealed class Snippet
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public bool IsFavorite { get; set; }
    public bool IsPinned { get; set; }
    public int CopyCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? LastUsedApp { get; set; }
    public double SearchRank { get; set; }

    public string Summary => Content.ReplaceLineEndings(" ").Trim() is { Length: > 96 } value
        ? string.Concat(value.AsSpan(0, 96), "…")
        : Content.ReplaceLineEndings(" ").Trim();

    public string TagsText => string.Join("  ", Tags.Select(tag => $"#{tag}"));
}

public sealed record SearchAlias(long Id, string Query, long SnippetId, string SnippetTitle, int HitCount, DateTimeOffset UpdatedAt);

public sealed record TagInfo(string Name, int Count);

public enum SortMode
{
    Smart,
    Recent,
    Created,
    Name
}

public enum ThemeMode
{
    System,
    Dark,
    Light
}

public enum ShortcutScope
{
    Local,
    Global
}

public sealed record ShortcutDefinition(
    string ActionId,
    string DisplayName,
    string Gesture,
    string DefaultGesture,
    ShortcutScope Scope);

public sealed record DatabaseStats(int SnippetCount, int TagCount, long SizeBytes);

public sealed record ImportSummary(int Created, int Skipped, int Failed);

public sealed record ModelSuggestion(string Kind, string Value, string? Detail = null);

public sealed class AppSettings
{
    public bool CloseAfterCopy { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public int ClipboardListenMinutes { get; set; } = 10;
    public SortMode DefaultSort { get; set; } = SortMode.Smart;
    public bool SmartRanking { get; set; } = true;
    public bool AppAwareness { get; set; } = true;
    public double FontScale { get; set; } = 1.0;
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public bool ModelEnabled { get; set; }
    public string ModelEndpoint { get; set; } = "https://ark.cn-beijing.volces.com/api/coding/v3";
    public string ModelName { get; set; } = "deepseek-v4-flash";
    public string FallbackModelName { get; set; } = "doubao-seed-2.1-turbo";
    public int ModelTimeoutSeconds { get; set; } = 15;
    public Dictionary<string, bool> ModelFeatures { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = true,
        ["tags"] = true,
        ["summary"] = false,
        ["semantic"] = false,
        ["rewrite"] = false,
        ["merge"] = false,
        ["variables"] = true
    };
}
