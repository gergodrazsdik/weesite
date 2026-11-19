using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Interfaces;
using Weesite.Web.Admin;
using Weesite.Web.Analytics;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The real app on a free Kestrel port with the sample site from seed/, shared by every test in
/// the session that leaves the site as it found it. Tests that change content or settings use a
/// keyed site of their own (EditorTests, F-0071). Later it gets fakes for embeddings and sign-in
/// (see the brief's testing section).
/// </summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class SiteServer : IAsyncInitializer, IAsyncDisposable
{
    private readonly SiteFactory _factory = new();

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        BaseAddress = await _factory.StartAsync();
        await AddSampleViewsAsync();
        await AddSampleReactionsAsync();

        // Two contact messages, one read and one unverified, so the Messages page has rows (ADR 0011).
        var messages = _factory.Services.GetRequiredService<Weesite.Web.Visitors.IMessageStore>();
        var now = DateTimeOffset.UtcNow;
        await messages.AddAsync(new(Weesite.Web.Visitors.ContactMessage.NewId(now.AddDays(-2)), "Ada Example", "ada@example.org", "Thanks for the post on static rendering. Is the sample code on GitHub?", now.AddDays(-2), Read: true, Unverified: false), CancellationToken.None);
        await messages.AddAsync(new(Weesite.Web.Visitors.ContactMessage.NewId(now.AddHours(-3)), "Bob Example", "bob@example.org", "Hello,\nI'd like to ask about the photos from Crete.\nBob", now.AddHours(-3), Read: false, Unverified: true), CancellationToken.None);

        // An editor added earlier, so the editors page has a row with its invitation (ADR 0010).
        await _factory.Services.GetRequiredService<IEditorStore>().SetAsync(
            new Editor("writer@example.com", EditorRole.Author, "admin@example.com", DateTimeOffset.UtcNow.AddDays(-3)), CancellationToken.None);
    }

    /// <summary>Gets the app's services, for tests that set up state the browser then shows.</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>Gets the absolute address of a path on the site, as Playwright takes it.</summary>
    /// <param name="path">A path such as <c>/about</c>.</param>
    /// <returns>The absolute address.</returns>
    public string At(string path) => new Uri(BaseAddress, path).ToString();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    // Reactions to two posts over the month (ADR 0011), for the post's counts and the analytics.
    private async Task AddSampleReactionsAsync()
    {
        var store = _factory.Services.GetRequiredService<Weesite.Web.Visitors.IReactionStore>();
        var today = DateTimeOffset.UtcNow.Date;
        (string Post, Weesite.Web.Visitors.ReactionKind Kind, int Count)[] given =
        [
            ("posts/notes-on-static-rendering.md", Weesite.Web.Visitors.ReactionKind.Like, 7),
            ("posts/notes-on-static-rendering.md", Weesite.Web.Visitors.ReactionKind.Insightful, 4),
            ("posts/notes-on-static-rendering.md", Weesite.Web.Visitors.ReactionKind.Thanks, 2),
            ("posts/walking-the-south-coast-of-crete.md", Weesite.Web.Visitors.ReactionKind.Love, 5),
        ];
        foreach (var (post, kind, count) in given)
        {
            for (var i = 0; i < count; i++)
            {
                await store.AddAsync(post, kind, 1, new DateTimeOffset(today.AddDays(-(i * 4 % 25)), TimeSpan.Zero).AddHours(9 + i), CancellationToken.None);
            }
        }
    }

    // A month of made-up page views, so the analytics page has something to show: more on
    // weekdays, a few referrers and countries, an agent and a crawler. Browsers in the tests themselves are
    // HeadlessChrome, which counts as a bot.
    private Task AddSampleViewsAsync()
    {
        var store = _factory.Services.GetRequiredService<MemoryPageViewStore>();
        var today = DateTimeOffset.UtcNow.Date;
        string[] paths = ["/", "/blog", "/blog/notes-on-static-rendering", "/blog/hello-world", "/about", "/blog/walking-the-south-coast-of-crete"];
        string?[] referrers = [null, null, null, "news.example", "social.example"];
        string?[] countries = ["DE", "NL", "DE", null, "FR", "PL", "DE"];
        var views = new List<PageView>();
        for (var day = 29; day >= 0; day--)
        {
            var date = new DateTimeOffset(today.AddDays(-day), TimeSpan.Zero);
            var count = 6 + (day * 7 % 11) + (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0 : 8);
            for (var i = 0; i < count; i++)
            {
                views.Add(new PageView(date.AddMinutes(i * 37), paths[i % paths.Length], referrers[i % referrers.Length], ClientKind.Browser, null, $"v{day}-{i % 5}", countries[i % countries.Length]));
            }

            views.Add(new PageView(date.AddHours(3), "/llms.txt", null, ClientKind.Agent, "ClaudeBot", $"a{day}"));
            views.Add(new PageView(date.AddHours(4), "/", null, ClientKind.Bot, "Googlebot", $"b{day}"));
        }

        return store.AddAsync(views, CancellationToken.None);
    }
}
