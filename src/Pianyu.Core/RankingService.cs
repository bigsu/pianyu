namespace Pianyu.Core;

public sealed record RankingContext(
    string Query,
    string? ForegroundApp,
    DateTimeOffset Now,
    bool SmartRanking = true,
    bool AppAwareness = true,
    SortMode SortMode = SortMode.Smart);

public sealed class RankingService
{
    public IReadOnlyList<Snippet> Rank(IEnumerable<Snippet> candidates, RankingContext context)
    {
        var items = candidates.ToList();
        if (context.SortMode != SortMode.Smart || !context.SmartRanking)
        {
            return context.SortMode switch
            {
                SortMode.Name => items.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
                SortMode.Created => items.OrderByDescending(x => x.CreatedAt).ToList(),
                _ => items.OrderByDescending(x => x.LastUsedAt ?? x.CreatedAt).ToList()
            };
        }

        return items
            .OrderByDescending(item => Score(item, context))
            .ThenByDescending(item => item.LastUsedAt ?? item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToList();
    }

    public double Score(Snippet item, RankingContext context)
    {
        var queryScore = Math.Max(0, item.SearchRank);
        var usageScore = Math.Log10(item.CopyCount + 1) * 7.5;
        var anchorScore = (item.IsPinned ? 22 : 0) + (item.IsFavorite ? 10 : 0);

        var ageDays = Math.Max(0, (context.Now - (item.LastUsedAt ?? item.CreatedAt)).TotalDays);
        var recencyScore = 18 * Math.Exp(-ageDays / 21d);

        var appScore = 0d;
        if (context.AppAwareness && !string.IsNullOrWhiteSpace(context.ForegroundApp) &&
            !string.IsNullOrWhiteSpace(item.LastUsedApp) &&
            string.Equals(context.ForegroundApp, item.LastUsedApp, StringComparison.OrdinalIgnoreCase))
        {
            appScore = 12;
        }

        return queryScore + usageScore + anchorScore + recencyScore + appScore;
    }
}
