using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Content;
using Weesite.Web.Analytics;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The full review's findings on analytics (T-0177, F-0250).</summary>
internal sealed class AnalyticsFullReviewTests
{
    [Test]
    public async Task Old_views_and_salts_go_also_with_counting_off()
    {
        // F-0250: the clean-up ran only after views were written, so with Analytics:Enabled false
        // old views stayed past the retention and the salts the visitor limits make never went.
        await using var root = new SiteFactory();
        await using var site = root.WithWebHostBuilder(b => b.UseSetting("Analytics:Enabled", "false"));
        var cleanUp = site.Services.GetServices<IHostedService>().OfType<PageViewCleanUp>().Single();
        var store = site.Services.GetRequiredService<MemoryPageViewStore>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var old = DateTimeOffset.UtcNow.AddMonths(-PageViewCleanUp.RetentionMonths).AddDays(-2);
        await store.AddAsync([new PageView(old, "/old", null, ClientKind.Browser, null, "0123456789abcdef"), new PageView(DateTimeOffset.UtcNow, "/new", null, ClientKind.Browser, null, "0123456789abcdef")], CancellationToken.None);
        var oldSalt = await store.SaltAsync(today.AddDays(-3), CancellationToken.None);
        var todaysSalt = await store.SaltAsync(today, CancellationToken.None);

        await cleanUp.CleanUpAsync(CancellationToken.None);

        var left = new List<string>();
        await foreach (var view in store.ReadAsync(DateOnly.FromDateTime(old.UtcDateTime).AddDays(-1), today, CancellationToken.None))
        {
            left.Add(view.Path);
        }

        await Assert.That(left).IsEquivalentTo(["/new"]);
        await Assert.That(await store.SaltAsync(today.AddDays(-3), CancellationToken.None)).IsNotEquivalentTo(oldSalt);
        await Assert.That(await store.SaltAsync(today, CancellationToken.None)).IsEquivalentTo(todaysSalt);
    }

    [Test]
    public async Task The_reaction_events_are_read_one_run_of_missing_days_at_a_time()
    {
        // F-0250: one query spanned the first missing day to the last, so the kept days between
        // them were read from storage again.
        var reactions = new RecordingReactionStore();
        var time = TimeProvider.System;
        using var comments = new CommentCatalog(new MemoryCommentStore(), new NoCache(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), time, NullLogger<CommentCatalog>.Instance);
        var data = new AnalyticsData(new MemoryPageViewStore(), reactions, comments, time);
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var kept = today.AddDays(-10);
        data.ReactionDays.Set(kept, [], 24, data.ReactionDays.Generation);

        await data.ReadAsync(SiteSettings.Empty, now, 30, CancellationToken.None);

        await Assert.That(reactions.Reads.Any(r => r.From <= kept && r.To >= kept)).IsFalse();
        await Assert.That(reactions.Reads).Contains((today.AddDays(-30), kept.AddDays(-1))).And.Contains((kept.AddDays(1), today.AddDays(-2)));
    }

    /// <summary>The memory store, recording the ranges of events read.</summary>
    private sealed class RecordingReactionStore : IReactionStore
    {
        private readonly MemoryReactionStore _inner = new();

        public List<(DateOnly From, DateOnly To)> Reads { get; } = [];

        public Task<ReactionCounts> GetAsync(string post, CancellationToken cancellationToken) => _inner.GetAsync(post, cancellationToken);

        public Task<ReactionCounts> AddAsync(string post, ReactionKind kind, int delta, DateTimeOffset at, CancellationToken cancellationToken) => _inner.AddAsync(post, kind, delta, at, cancellationToken);

        public async IAsyncEnumerable<ReactionEvent> ReadEventsAsync(DateOnly from, DateOnly to, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (Reads)
            {
                Reads.Add((from, to));
            }

            await foreach (var e in _inner.ReadEventsAsync(from, to, cancellationToken))
            {
                yield return e;
            }
        }

        public Task<int> CleanUpAsync(DateOnly from, CancellationToken cancellationToken) => _inner.CleanUpAsync(from, cancellationToken);

        public Task<int> ClearAsync(CancellationToken cancellationToken) => _inner.ClearAsync(cancellationToken);
    }

    private sealed class NoCache : Microsoft.AspNetCore.OutputCaching.IOutputCacheStore
    {
        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(null);

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
