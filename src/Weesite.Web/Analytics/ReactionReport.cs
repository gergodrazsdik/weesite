using Weesite.Content;
using Weesite.Web.Visitors;

namespace Weesite.Web.Analytics;

/// <summary>
/// The reactions in the analytics' range of days (ADR 0011): given minus taken back, by day, by
/// reaction and by post. Built from the events, which say nothing about who reacted.
/// </summary>
/// <param name="Days">The net reactions of every day of the range, oldest first, empty days included.</param>
/// <param name="ByKind">The net count of each reaction, in the buttons' order.</param>
/// <param name="Posts">The posts with the most, most first: their titles (or source paths, for posts no longer on the site), each reaction's net count, their total (T-0158) and the comments written in the range (T-0162).</param>
internal sealed record ReactionReport(
    IReadOnlyList<(DateOnly Day, int Count)> Days,
    IReadOnlyList<(ReactionKind Kind, int Count)> ByKind,
    IReadOnlyList<(string Post, ReactionCounts Counts, int Count, int Comments)> Posts)
{
    /// <summary>Gets the net reactions of the range.</summary>
    public int Total => Days.Sum(d => d.Count);

    /// <summary>Builds the report.</summary>
    /// <param name="events">The range's events.</param>
    /// <param name="from">The first day.</param>
    /// <param name="to">The last day, included.</param>
    /// <param name="catalog">The content, for the posts' titles.</param>
    /// <param name="zone">The time zone whose days these are (T-0157); UTC when null.</param>
    /// <param name="comments">The comments published in the range, by post source path (T-0162).</param>
    /// <returns>The report.</returns>
    public static ReactionReport Build(IEnumerable<ReactionEvent> events, DateOnly from, DateOnly to, ContentCatalog catalog, TimeZoneInfo? zone = null, IReadOnlyDictionary<string, int>? comments = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(catalog);
        zone ??= TimeZoneInfo.Utc;
        DateOnly DayOf(ReactionEvent e) => SiteTime.Day(e.At, zone);
        var list = events.Where(e => DayOf(e) >= from && DayOf(e) <= to).ToList();
        var byDay = list.ToLookup(DayOf);
        var days = new List<(DateOnly, int)>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            days.Add((day, byDay[day].Sum(e => e.Delta)));
        }

        var titles = catalog.Posts.Concat(catalog.Unpublished)
            .GroupBy(i => i.SourcePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        // Grouped once, not filtered again for each post (F-0222).
        var byPost = list.ToLookup(e => e.Post, StringComparer.Ordinal);
        return new ReactionReport(
            days,
            [.. Reactions.All.Select(kind => (kind, list.Where(e => e.Kind == kind).Sum(e => e.Delta)))],
            [.. byPost.Select(g => g.Key).Concat(comments?.Keys ?? []).Distinct(StringComparer.Ordinal)
                .Select(post =>
                {
                    var those = byPost[post].ToList();
                    return (
                        Post: titles.GetValueOrDefault(post) ?? post,
                        Counts: new ReactionCounts(Net(those, ReactionKind.Like), Net(those, ReactionKind.Love), Net(those, ReactionKind.Thanks), Net(those, ReactionKind.Insightful), Net(those, ReactionKind.Funny)),
                        Count: those.Sum(e => e.Delta),
                        Comments: comments?.GetValueOrDefault(post) ?? 0);
                })
                .Where(p => p.Count > 0 || p.Comments > 0)
                .OrderByDescending(p => p.Count)
                .ThenByDescending(p => p.Comments)
                .ThenBy(p => p.Post, StringComparer.CurrentCulture)
                .Take(AnalyticsReport.TopCount)]);
    }

    // A reaction's given minus taken back in the range: negative when more were taken back than given.
    private static int Net(IEnumerable<ReactionEvent> events, ReactionKind kind) => events.Where(e => e.Kind == kind).Sum(e => e.Delta);
}
