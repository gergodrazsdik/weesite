using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Interfaces;
using Weesite.Web.Analytics;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The real app on a free Kestrel port with the sample site from seed/, shared by every test in
/// the session. Later it gets fakes for embeddings and sign-in (see the brief's testing section).
/// </summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class SiteServer : IAsyncInitializer, IAsyncDisposable
{
    private readonly SiteFactory _factory = new();

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        BaseAddress = _factory.ClientOptions.BaseAddress;
        return AddSampleViewsAsync();
    }

    /// <summary>Gets the app's services, for tests that set up state the browser then shows.</summary>
    public IServiceProvider Services => _factory.Services;

    /// <summary>Gets the absolute address of a path on the site, as Playwright takes it.</summary>
    /// <param name="path">A path such as <c>/about</c>.</param>
    /// <returns>The absolute address.</returns>
    public string At(string path) => new Uri(BaseAddress, path).ToString();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    // A month of made-up page views, so the analytics page has something to show: more on
    // weekdays, a few referrers, an agent and a crawler. Browsers in the tests themselves are
    // HeadlessChrome, which counts as a bot.
    private Task AddSampleViewsAsync()
    {
        var store = _factory.Services.GetRequiredService<MemoryPageViewStore>();
        var today = DateTimeOffset.UtcNow.Date;
        string[] paths = ["/", "/blog", "/blog/notes-on-static-rendering", "/blog/hello-world", "/about", "/blog/walking-the-south-coast-of-crete"];
        string?[] referrers = [null, null, null, "news.example", "social.example"];
        var views = new List<PageView>();
        for (var day = 29; day >= 0; day--)
        {
            var date = new DateTimeOffset(today.AddDays(-day), TimeSpan.Zero);
            var count = 6 + (day * 7 % 11) + (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0 : 8);
            for (var i = 0; i < count; i++)
            {
                views.Add(new PageView(date.AddMinutes(i * 37), paths[i % paths.Length], referrers[i % referrers.Length], ClientKind.Browser, null, $"v{day}-{i % 5}"));
            }

            views.Add(new PageView(date.AddHours(3), "/llms.txt", null, ClientKind.Agent, "ClaudeBot", $"a{day}"));
            views.Add(new PageView(date.AddHours(4), "/", null, ClientKind.Bot, "Googlebot", $"b{day}"));
        }

        return store.AddAsync(views, CancellationToken.None);
    }
}
