using System.Globalization;
using Weesite.Content;
using Weesite.Web.Visitors;

namespace Weesite.Web.Analytics;

/// <summary>What the analytics' reports are built from, for a range of days in the site's time zone.</summary>
/// <param name="From">The first day.</param>
/// <param name="To">The last day, included: today.</param>
/// <param name="Views">The page views of the range and of the UTC day either side, which the reports filter.</param>
/// <param name="Reactions">The reaction events of the range and of the UTC day either side.</param>
/// <param name="Comments">The approved comments written in the range, by post source path.</param>
internal sealed record AnalyticsRange(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<PageView> Views,
    IReadOnlyList<ReactionEvent> Reactions,
    IReadOnlyDictionary<string, int> Comments);

/// <summary>
/// Reads what the analytics page and the posts report show (F-0227: one implementation for both):
/// the page views, the reaction events and the comment counts of a range. The page views' store
/// keeps finished days in memory; the reaction events' finished days are kept here the same way,
/// and the comments come from <see cref="CommentCatalog"/>'s memory, so the refresh every 30 s
/// reads only today and yesterday from storage (F-0222).
/// </summary>
/// <param name="views">The page views.</param>
/// <param name="reactions">The reactions.</param>
/// <param name="comments">The approved comments, in memory.</param>
/// <param name="time">The clock, for the reaction days' expiry.</param>
internal sealed class AnalyticsData(IPageViewStore views, IReactionStore reactions, CommentCatalog comments, TimeProvider time)
{
    /// <summary>The bytes the reaction events kept in memory may take, as estimated.</summary>
    public const long ReactionBudget = 4L * 1024 * 1024;

    // A kept event besides its post's path: the record (headers 16, the time 16, the path 8, the
    // kind and delta 8) and its place in the day's array.
    private const long EventBytes = 56;

    /// <summary>The ranges to choose from, in days.</summary>
    public static readonly IReadOnlyList<int> Ranges = [7, 30, 90];

    /// <summary>Gets the reaction events' finished days kept in memory.</summary>
    internal DayCache<ReactionEvent[]> ReactionDays { get; } = new(time, ReactionBudget, TimeSpan.FromMinutes(5));

    /// <summary>Reads a range from the query: 7, 30 or 90 days, 30 for anything else (F-0080).</summary>
    /// <param name="value">The query's value.</param>
    /// <returns>The days.</returns>
    // A string, parsed here: a typed int? turns ?days=abc into an error page.
    public static int ParseDays(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && Ranges.Contains(days) ? days : 30;

    /// <summary>Reads a range's page views, reaction events and comment counts.</summary>
    /// <param name="site">The site's settings, for its time zone.</param>
    /// <param name="now">The current time.</param>
    /// <param name="days">The range, in days up to today in the site's time zone.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>The range.</returns>
    public async Task<AnalyticsRange> ReadAsync(SiteSettings site, DateTimeOffset now, int days, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);

        // Days in the site's time zone (T-0157): stored by UTC day, so a day either side is read
        // for the hours a zone's day reaches into them. The two reads go at once (F-0202).
        var today = site.DayOf(now);
        var from = today.AddDays(1 - days);
        var viewList = ListAsync(views.ReadAsync(from.AddDays(-1), today.AddDays(1), cancellationToken));
        var eventList = ReadReactionsAsync(from.AddDays(-1), today.AddDays(1), cancellationToken);
        await Task.WhenAll(viewList, eventList);

        // The approved comments written in the range (T-0162), counted from memory.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (post, thread) in comments.All)
        {
            var count = thread.Count(c => !c.Deleted && site.DayOf(c.Created) is var day && day >= from && day <= today);
            if (count > 0)
            {
                counts[post] = count;
            }
        }

        return new AnalyticsRange(from, today, await viewList, await eventList, counts);
    }

    /// <summary>Forgets the reaction events kept in memory: the reset, when it deleted them.</summary>
    public void ForgetReactions() => ReactionDays.Clear();

    // The events of some UTC days: the finished ones (before yesterday) from memory where they're
    // kept, the rest from the store, in one query for each run of days (most days have none).
    private async Task<List<ReactionEvent>> ReadReactionsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var finished = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1);
        var kept = new Dictionary<DateOnly, ReactionEvent[]>();
        DateOnly? firstMissing = null;
        DateOnly? lastMissing = null;
        for (var day = from; day <= to && day < finished; day = day.AddDays(1))
        {
            if (ReactionDays.TryGet(day, out var events))
            {
                kept[day] = events;
            }
            else
            {
                firstMissing ??= day;
                lastMissing = day;
            }
        }

        if (firstMissing is { } first && lastMissing is { } last)
        {
            var generation = ReactionDays.Generation;
            var strings = new MemorySize.Strings();
            var read = (await ListAsync(reactions.ReadEventsAsync(first, last, cancellationToken)))
                .Select(e => e with { Post = strings.Share(e.Post) })
                .ToLookup(e => DateOnly.FromDateTime(e.At.UtcDateTime));
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (!kept.ContainsKey(day))
                {
                    var events = read[day].ToArray();
                    kept[day] = events;

                    // The paths are shared within the read; each day counts them once, which
                    // overestimates a little.
                    ReactionDays.Set(day, events, 24 + (events.Length * EventBytes) + events.Select(e => e.Post).Distinct(StringComparer.Ordinal).Sum(MemorySize.Of), generation);
                }
            }
        }

        var list = new List<ReactionEvent>();
        for (var day = from; day <= to && day < finished; day = day.AddDays(1))
        {
            list.AddRange(kept[day]);
        }

        var fresh = from > finished ? from : finished;
        if (fresh <= to)
        {
            list.AddRange(await ListAsync(reactions.ReadEventsAsync(fresh, to, cancellationToken)));
        }

        return list;
    }

    private static async Task<List<TItem>> ListAsync<TItem>(IAsyncEnumerable<TItem> items)
    {
        var list = new List<TItem>();
        await foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }
}
