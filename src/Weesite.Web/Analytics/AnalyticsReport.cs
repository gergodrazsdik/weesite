namespace Weesite.Web.Analytics;

/// <summary>One day of the report.</summary>
/// <param name="Day">The day (UTC).</param>
/// <param name="Views">Page views by people (browsers).</param>
/// <param name="Visitors">Different visitors (browsers) that day.</param>
/// <param name="BotViews">Page views by bots.</param>
/// <param name="AgentViews">Page views by AI agents.</param>
internal sealed record ReportDay(DateOnly Day, int Views, int Visitors, int BotViews, int AgentViews);

/// <summary>A row of a top list.</summary>
/// <param name="Name">The path, host or client name.</param>
/// <param name="Views">Page views.</param>
/// <param name="Visitors">Visitors, summed per day (0 where it doesn't apply).</param>
internal sealed record ReportRow(string Name, int Views, int Visitors);

/// <summary>A bot or agent and its page views.</summary>
/// <param name="Kind">Bot or agent.</param>
/// <param name="Name">Its name, or null for unnamed bots.</param>
/// <param name="Views">Page views.</param>
internal sealed record ReportClient(ClientKind Kind, string? Name, int Views);

/// <summary>
/// The analytics dashboard's numbers for a range of days (brief, Analytics). Visitors are counted
/// per day, since the visitor hash changes every day: someone who comes back on another day counts
/// again, so totals are sums of daily visitors. Pages and referrers count people only; bots and
/// agents are listed by name.
/// </summary>
/// <param name="Days">Every day of the range, oldest first, empty days included.</param>
/// <param name="Pages">The most viewed pages, most first.</param>
/// <param name="Referrers">The sites that sent the most visits, most first.</param>
/// <param name="Clients">Bots and agents, most views first.</param>
internal sealed record AnalyticsReport(
    IReadOnlyList<ReportDay> Days,
    IReadOnlyList<ReportRow> Pages,
    IReadOnlyList<ReportRow> Referrers,
    IReadOnlyList<ReportClient> Clients)
{
    /// <summary>The most rows in each top list.</summary>
    public const int TopCount = 15;

    /// <summary>Gets the page views by people.</summary>
    public int Views => Days.Sum(d => d.Views);

    /// <summary>Gets the visitors, summed per day.</summary>
    public int Visitors => Days.Sum(d => d.Visitors);

    /// <summary>Gets the page views by bots.</summary>
    public int BotViews => Days.Sum(d => d.BotViews);

    /// <summary>Gets the page views by AI agents.</summary>
    public int AgentViews => Days.Sum(d => d.AgentViews);

    /// <summary>Builds the report from a range's page views.</summary>
    /// <param name="views">The views.</param>
    /// <param name="from">The first day.</param>
    /// <param name="to">The last day, included.</param>
    /// <returns>The report.</returns>
    public static AnalyticsReport Build(IEnumerable<PageView> views, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(views);
        var list = views.Where(v => DayOf(v) >= from && DayOf(v) <= to).ToList();
        var people = list.Where(v => v.Kind == ClientKind.Browser).ToList();
        var byDay = list.ToLookup(DayOf);

        var days = new List<ReportDay>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var those = byDay[day].ToList();
            var browsers = those.Where(v => v.Kind == ClientKind.Browser).ToList();
            days.Add(new ReportDay(
                day,
                browsers.Count,
                browsers.Select(v => v.Visitor).Distinct(StringComparer.Ordinal).Count(),
                those.Count(v => v.Kind == ClientKind.Bot),
                those.Count(v => v.Kind == ClientKind.Agent)));
        }

        return new AnalyticsReport(
            days,
            Top(people, v => v.Path),
            Top(people.Where(v => v.ReferrerHost is not null), v => v.ReferrerHost!),
            [.. list.Where(v => v.Kind != ClientKind.Browser)
                .GroupBy(v => (v.Kind, v.Client))
                .Select(g => new ReportClient(g.Key.Kind, g.Key.Client, g.Count()))
                .OrderByDescending(c => c.Views)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Take(TopCount)]);
    }

    private static List<ReportRow> Top(IEnumerable<PageView> views, Func<PageView, string> key) =>
        [.. views.GroupBy(key, StringComparer.Ordinal)
            .Select(g => new ReportRow(
                g.Key,
                g.Count(),
                g.GroupBy(DayOf).Sum(d => d.Select(v => v.Visitor).Distinct(StringComparer.Ordinal).Count())))
            .OrderByDescending(r => r.Views)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(TopCount)];

    private static DateOnly DayOf(PageView view) => DateOnly.FromDateTime(view.At.UtcDateTime);
}
