using System.Globalization;
using System.Text;
using Weesite.Content;
using Weesite.Web.Admin;
using Weesite.Web.Visitors;

namespace Weesite.Web.Analytics;

/// <summary>One post of the posts report (T-0163) and its numbers in the range.</summary>
/// <param name="Item">The post.</param>
/// <param name="Status">Its status key (<see cref="ItemStatus.Key"/>).</param>
/// <param name="Views">Page views by people.</param>
/// <param name="Visitors">Visitors, summed per day.</param>
/// <param name="AgentViews">Page views by AI agents.</param>
/// <param name="BotViews">Page views by bots.</param>
/// <param name="Comments">Approved comments written in the range.</param>
/// <param name="Reactions">Reactions given minus taken back in the range.</param>
internal sealed record PostReportRow(ContentItem Item, string Status, int Views, int Visitors, int AgentViews, int BotViews, int Comments, int Reactions);

/// <summary>
/// The posts report (T-0163): every post, published or not, with its page views, visitors, agent
/// and bot views, comments and reactions in a range of days in the site's time zone, sortable by
/// any column, and its CSV export.
/// </summary>
internal static class PostReport
{
    /// <summary>The ranges to choose from, in days.</summary>
    public static readonly IReadOnlyList<int> Ranges = [7, 30, 90];

    /// <summary>The columns to sort by, in the table's order.</summary>
    public static readonly IReadOnlyList<string> Columns = ["title", "status", "date", "views", "visitors", "agents", "bots", "comments", "reactions"];

    /// <summary>The column sorted by when none is asked for.</summary>
    public const string DefaultSort = "views";

    // The statuses in the order a sort by status puts them.
    private static readonly string[] StatusOrder = ["published", "scheduled", "draft", "archived"];

    /// <summary>Reads the range from the query: 7, 30 or 90; 30 otherwise.</summary>
    /// <param name="value">The query value.</param>
    /// <returns>The days.</returns>
    // A string, parsed here: a typed int? turns ?days=abc into an error page (F-0080).
    public static int Days(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && Ranges.Contains(days) ? days : 30;

    /// <summary>Reads the sort from the query: a column, and whether it runs from the most.</summary>
    /// <param name="sort">The column; <see cref="DefaultSort"/> when it isn't one.</param>
    /// <param name="dir"><c>asc</c> or <c>desc</c>; the column's own default otherwise.</param>
    /// <returns>The column and the direction.</returns>
    public static (string Column, bool Descending) Order(string? sort, string? dir)
    {
        var column = sort is not null && Columns.Contains(sort) ? sort : DefaultSort;
        return (column, dir switch
        {
            "asc" => false,
            "desc" => true,
            _ => DescendingFirst(column),
        });
    }

    /// <summary>Whether a column sorts from the most (numbers, the newest date) when first chosen.</summary>
    /// <param name="column">The column.</param>
    /// <returns>True for numbers and the date; false for the title and status.</returns>
    public static bool DescendingFirst(string column) => column is not ("title" or "status");

    /// <summary>Builds the rows, newest post first.</summary>
    /// <param name="catalog">The content: its posts, published or not.</param>
    /// <param name="views">Page views of the range (and the days either side; filtered here).</param>
    /// <param name="reactions">Reaction events of the range (and the days either side).</param>
    /// <param name="comments">The comments.</param>
    /// <param name="from">The first day.</param>
    /// <param name="to">The last day, included.</param>
    /// <param name="now">The current time, for the scheduled posts' status.</param>
    /// <returns>A row per post.</returns>
    public static IReadOnlyList<PostReportRow> Build(
        ContentCatalog catalog,
        IEnumerable<PageView> views,
        IEnumerable<ReactionEvent> reactions,
        IEnumerable<Comment> comments,
        DateOnly from,
        DateOnly to,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(views);
        ArgumentNullException.ThrowIfNull(reactions);
        ArgumentNullException.ThrowIfNull(comments);
        var zone = catalog.Site.Zone;
        bool InRange(DateTimeOffset at) => SiteTime.Day(at, zone) is var day && day >= from && day <= to;

        var posts = catalog.Posts.Concat(catalog.Unpublished.Where(i => i.Kind == ContentKind.Post))
            .DistinctBy(i => i.SourcePath, StringComparer.Ordinal)
            .ToList();

        // Each post's addresses: its slug, then its earlier ones (views from before a rename), as
        // the recorder stores them (decoded) and as links write them (percent-encoded). A slug in
        // use belongs to its post; an earlier one only to the first post that had it.
        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var post in posts)
        {
            AddPaths(byPath, post.Slug, post.SourcePath);
        }

        foreach (var post in posts)
        {
            foreach (var previous in post.PreviousSlugs)
            {
                AddPaths(byPath, previous, post.SourcePath);
            }
        }

        var postViews = views.Where(v => InRange(v.At))
            .Select(v => (Post: byPath.GetValueOrDefault(Normalize(v.Path)), View: v))
            .Where(p => p.Post is not null)
            .ToLookup(p => p.Post!, p => p.View, StringComparer.Ordinal);
        var postReactions = reactions.Where(e => InRange(e.At))
            .GroupBy(e => e.Post, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Delta), StringComparer.Ordinal);
        var postComments = comments.Where(c => c.Status == CommentStatus.Approved && InRange(c.Created))
            .GroupBy(c => c.Post, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return [.. posts
            .Select(post =>
            {
                var seen = postViews[post.SourcePath].ToList();
                var people = seen.Where(v => v.Kind == ClientKind.Browser).ToList();
                return new PostReportRow(
                    post,
                    ItemStatus.Key(post, now),
                    people.Count,
                    people.GroupBy(v => SiteTime.Day(v.At, zone)).Sum(d => d.Select(v => v.Visitor).Distinct(StringComparer.Ordinal).Count()),
                    seen.Count(v => v.Kind == ClientKind.Agent),
                    seen.Count(v => v.Kind == ClientKind.Bot),
                    postComments.GetValueOrDefault(post.SourcePath),
                    postReactions.GetValueOrDefault(post.SourcePath));
            })
            .OrderByDescending(r => r.Item.Date)
            .ThenBy(r => r.Item.Title, StringComparer.CurrentCulture)];
    }

    /// <summary>Sorts the rows by a column; ties newest post first, then by title.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="column">One of <see cref="Columns"/>.</param>
    /// <param name="descending">Whether from the most (or Z, or the newest).</param>
    /// <returns>The rows in order.</returns>
    public static IReadOnlyList<PostReportRow> Sort(IEnumerable<PostReportRow> rows, string column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sorted = column switch
        {
            "title" => By(rows, r => r.Item.Title, descending, StringComparer.CurrentCultureIgnoreCase),
            "status" => By(rows, r => Array.IndexOf(StatusOrder, r.Status), descending),
            "date" => By(rows, r => r.Item.Date, descending),
            "visitors" => By(rows, r => r.Visitors, descending),
            "agents" => By(rows, r => r.AgentViews, descending),
            "bots" => By(rows, r => r.BotViews, descending),
            "comments" => By(rows, r => r.Comments, descending),
            "reactions" => By(rows, r => r.Reactions, descending),
            _ => By(rows, r => r.Views, descending),
        };
        return [.. sorted.ThenByDescending(r => r.Item.Date).ThenBy(r => r.Item.Title, StringComparer.CurrentCulture)];
    }

    /// <summary>Reads a range's numbers from the stores and builds the rows.</summary>
    /// <param name="services">The request's services: the page view, reaction and comment stores.</param>
    /// <param name="catalog">The content.</param>
    /// <param name="now">The current time.</param>
    /// <param name="days">The range, in days up to today in the site's time zone.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <returns>The rows, newest post first.</returns>
    public static async Task<IReadOnlyList<PostReportRow>> ReadAsync(IServiceProvider services, ContentCatalog catalog, DateTimeOffset now, int days, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        // Days in the site's time zone (T-0157): stored by UTC day, so a day either side is read
        // for the hours a zone's day reaches into them.
        var today = catalog.Site.DayOf(now);
        var from = today.AddDays(1 - days);
        var views = new List<PageView>();
        await foreach (var view in services.GetRequiredService<IPageViewStore>().ReadAsync(from.AddDays(-1), today.AddDays(1), cancellationToken))
        {
            views.Add(view);
        }

        var reactions = new List<ReactionEvent>();
        await foreach (var reaction in services.GetRequiredService<IReactionStore>().ReadEventsAsync(from.AddDays(-1), today.AddDays(1), cancellationToken))
        {
            reactions.Add(reaction);
        }

        var comments = await services.GetRequiredService<ICommentStore>().ListAsync(cancellationToken);
        return Build(catalog, views, reactions, comments, from, today, now);
    }

    /// <summary>Gets a CSV field (RFC 4180): quoted when it holds a comma, quote or line break.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The field.</returns>
    public static string Field(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Titles are typed by editors: "=…" mustn't run in a spreadsheet (CWE-1236), nor " =…",
        // which some spreadsheets read as a formula once they trim the spaces.
        if (value.TrimStart(' ') is { Length: > 0 } trimmed && trimmed[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>Gets the CSV header line, with a line break.</summary>
    /// <returns>The line.</returns>
    public static string HeaderLine() =>
        "Title,Status,Date,Path,Page views,Visitors,AI agent views,Bot views,Comments,Reactions\r\n";

    /// <summary>Gets a row as a CSV line, with a line break. Numbers are written as they are.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The line.</returns>
    public static string Line(PostReportRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.Join(
            ',',
            Field(row.Item.Title),
            Field(row.Status),
            row.Item.Date is { } date ? ContentDates.Iso(date) : "",
            Field(Seo.ContentUrls.PathOf(row.Item)),
            Number(row.Views),
            Number(row.Visitors),
            Number(row.AgentViews),
            Number(row.BotViews),
            Number(row.Comments),
            Number(row.Reactions)) + "\r\n";
    }

    /// <summary>
    /// Maps <c>GET /admin/analytics/posts.csv?days=30&amp;sort=…&amp;dir=…</c> (admins): the posts
    /// report of the range as CSV, in the page's order.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapPostReport(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/analytics/posts.csv", async (string? days, string? sort, string? dir, HttpContext context, ContentCatalogProvider content, TimeProvider time) =>
        {
            var catalog = content.Current;
            var now = time.GetUtcNow();
            var range = Days(days);
            var (column, descending) = Order(sort, dir);
            var rows = Sort(await ReadAsync(context.RequestServices, catalog, now, range, context.RequestAborted), column, descending);
            var name = $"posts-{ContentDates.Iso(catalog.Site.DayOf(now))}-{range.ToString(CultureInfo.InvariantCulture)}d.csv";

            context.Response.ContentType = "text/csv; charset=utf-8";
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            await using var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
            await writer.WriteAsync(HeaderLine().AsMemory(), context.RequestAborted);
            foreach (var row in rows)
            {
                await writer.WriteAsync(Line(row).AsMemory(), context.RequestAborted);
            }
        }).RequireAuthorization(EditorClaims.AdminPolicy);

    private static IOrderedEnumerable<PostReportRow> By<TKey>(IEnumerable<PostReportRow> rows, Func<PostReportRow, TKey> key, bool descending, IComparer<TKey>? comparer = null) =>
        descending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    private static void AddPaths(Dictionary<string, string> byPath, string slug, string post)
    {
        byPath.TryAdd("/blog/" + slug, post);
        byPath.TryAdd(Seo.ContentUrls.PostPath(slug), post);
    }

    // A view's path as a post's: without a trailing slash, and the Markdown copy (".md") as the page.
    private static string Normalize(string path)
    {
        path = path.TrimEnd('/');
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
