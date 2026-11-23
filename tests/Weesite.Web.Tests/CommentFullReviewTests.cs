using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The full review's findings on comments (T-0177): F-0245, F-0246, F-0250.</summary>
internal sealed partial class CommentTests
{
    [Test]
    public async Task The_comment_count_offers_to_write_one_only_where_the_form_can_show()
    {
        // F-0246: a closed post, or a page without JavaScript, has no form to write in.
        using var copy = new SiteCopy();
        Close(copy, "posts/notes-on-dialing-in-espresso.md");
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        var now = DateTimeOffset.UtcNow;
        var store = site.Services.GetRequiredService<ICommentStore>();
        await store.SaveAsync(new Comment(Comment.NewId(now), "posts/notes-on-dialing-in-espresso.md", "Ada", "On a closed post.", null, CommentStatus.Approved, now, null, now, "x"), CancellationToken.None);
        await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Bob", "On an open post.", null, CommentStatus.Approved, now, null, now, "y"), CancellationToken.None);
        await site.Services.GetRequiredService<CommentCatalog>().RefreshAsync(CancellationToken.None);
        using var client = site.CreateClient();

        var closed = await client.GetStringAsync(new Uri("/blog/notes-on-dialing-in-espresso", UriKind.Relative));
        var open = await client.GetStringAsync(new Uri($"/blog/{Post}", UriKind.Relative));

        await Assert.That(closed).Contains("aria-label=\"1 comment: read it\">");
        await Assert.That(closed).DoesNotContain("write one");
        await Assert.That(open).Contains("aria-label=\"1 comment: read it\" data-write-label=\"1 comment: read it or write one\"");
    }

    [Test]
    public async Task A_page_rendered_while_the_comments_reload_isnt_kept_in_the_cache()
    {
        // F-0245: a page that read the comments before a reload and was stored after its eviction
        // stayed in the output cache for its ten minutes, with the old comments.
        var store = new MemoryCommentStore();
        var cache = new CountingOutputCache();
        using var catalog = new CommentCatalog(store, cache, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<CommentCatalog>.Instance);
        await catalog.RefreshAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        // A render reads the comments; a published comment reloads them before the response is done.
        var during = Request();
        catalog.Guard(during.Context);
        catalog.Guard(during.Context);
        var shown = catalog.For(PostPath);
        await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Ada", "Shown", null, CommentStatus.Approved, now, null, now, "x"), CancellationToken.None);
        await catalog.RefreshAsync(CancellationToken.None);
        var afterReload = cache.Evicted;
        await during.CompleteAsync();

        // A render with nothing reloaded meanwhile leaves the cache alone.
        var quiet = Request();
        catalog.Guard(quiet.Context);
        await quiet.CompleteAsync();

        await Assert.That(shown).IsEmpty();
        await Assert.That(afterReload).IsEqualTo(1);
        await Assert.That(during.Callbacks).IsEqualTo(1);
        await Assert.That(cache.Evicted).IsEqualTo(2);
    }

    [Test]
    public async Task A_reply_may_quote_the_links_of_the_posts_comments()
    {
        // F-0250: Reply quotes a comment; its links counted against the reply's own three.
        await using var root = new SiteFactory();
        await using var site = Site(root);
        var now = DateTimeOffset.UtcNow;
        const string Linked = "See https://a.example/1, https://b.example/2 and www.c.example/3.";
        await site.Services.GetRequiredService<ICommentStore>().SaveAsync(new Comment(Comment.NewId(now), PostPath, "Ada", Linked, null, CommentStatus.Approved, now, null, now, "x"), CancellationToken.None);
        await site.Services.GetRequiredService<CommentCatalog>().RefreshAsync(CancellationToken.None);
        using var client = site.CreateClient();

        using var reply = await Submit(client, site, "Bob", $"Ada wrote:\n> {Linked}\n\nAnd https://d.example/4 too.");
        using var invented = await Submit(client, site, "Cy", "> https://x.example/1 https://x.example/2\n> https://x.example/3\n\nAnd https://d.example/4 too.");
        using var unquoted = await Submit(client, site, "Di", $"{Linked}\n\nAnd https://d.example/4 too.");

        await Assert.That(reply.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(invented.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unquoted.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(VisitorText.Check($"> {Linked}\nhttps://d.example/4", 1000, quotable: new HashSet<string>(["https://a.example/1", "https://b.example/2", "https://www.c.example/3"]))).IsEqualTo(TextProblem.None);
    }

    [Test]
    public async Task A_comments_time_is_taken_just_before_it_is_kept()
    {
        // F-0250: its time was taken before the limit's storage calls; a slow one put it in a range
        // a digest had already covered, and no moderator was mailed about it.
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        await using var site = Site(root, clock: clock, more: b => b.ConfigureServices(s =>
            s.AddSingleton<IVisitorStore>(p => new SlowLimits(p.GetRequiredService<MemoryVisitorStore>(), clock))));
        using var client = site.CreateClient();
        var before = clock.GetUtcNow();

        await SendAsync(client, site, "Ada", "Slowly counted.");

        await Assert.That(Stored(site).Single().Created).IsGreaterThanOrEqualTo(before + SlowLimits.Delay);
    }

    private static TrackedRequest Request() => new();

    /// <summary>A request whose completion callbacks the test runs.</summary>
    private sealed class TrackedRequest
    {
        private readonly CompletingResponse _response = new();

        public TrackedRequest()
        {
            Context = new DefaultHttpContext();
            Context.Features.Set<IHttpResponseFeature>(_response);
        }

        public HttpContext Context { get; }

        public int Callbacks => _response.Callbacks.Count;

        public async Task CompleteAsync()
        {
            foreach (var (callback, state) in _response.Callbacks)
            {
                await callback(state);
            }
        }

        private sealed class CompletingResponse : HttpResponseFeature
        {
            public List<(Func<object, Task> Callback, object State)> Callbacks { get; } = [];

            public override void OnCompleted(Func<object, Task> callback, object state) => Callbacks.Add((callback, state));
        }
    }

    /// <summary>The counters, each count taking a minute of the test clock, as a throttled storage call might.</summary>
    private sealed class SlowLimits(IVisitorStore inner, OAuthTests.TestClock clock) : IVisitorStore
    {
        public static readonly TimeSpan Delay = TimeSpan.FromMinutes(1);

        public Task<bool> TryCountAsync(string name, string hash, DateTimeOffset at, CountWindow window, int limit, CancellationToken cancellationToken)
        {
            clock.Advance(Delay);
            return inner.TryCountAsync(name, hash, at, window, limit, cancellationToken);
        }

        public Task<bool> TryUncountAsync(string name, string hash, DateTimeOffset at, CountWindow window, CancellationToken cancellationToken) =>
            inner.TryUncountAsync(name, hash, at, window, cancellationToken);

        public Task<bool> TryUseChallengeAsync(string challenge, DateTimeOffset expires, CancellationToken cancellationToken) =>
            inner.TryUseChallengeAsync(challenge, expires, cancellationToken);

        public Task<int> CleanUpAsync(DateOnly before, CancellationToken cancellationToken) => inner.CleanUpAsync(before, cancellationToken);
    }
}
