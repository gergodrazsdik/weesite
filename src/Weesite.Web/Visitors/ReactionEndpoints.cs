using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Weesite.Content;

namespace Weesite.Web.Visitors;

/// <summary>What the browser sends to react, to change its reaction, or to take it back.</summary>
/// <param name="Post">The post's slug.</param>
/// <param name="Kind">The reaction's name, such as <c>like</c>.</param>
/// <param name="Undo">True to take it back.</param>
/// <param name="Proof">The proof-of-work answer for <c>reaction</c>.</param>
/// <param name="Replaces">The reaction this one replaces, when the visitor changes theirs (T-0158).</param>
internal sealed record ReactionRequest(string? Post, string? Kind, bool Undo, string? Proof, string? Replaces = null);

/// <summary>What the reaction endpoints answer.</summary>
/// <param name="Counts">The post's counts by reaction name.</param>
/// <param name="Counted">Whether this request changed them.</param>
internal sealed record ReactionAnswer(IReadOnlyDictionary<string, int> Counts, bool Counted);

/// <summary>
/// The reactions to posts (ADR 0011), under <c>/visitor</c>, so the per-minute limit applies and
/// nothing is cached on the way:
/// <list type="bullet">
/// <item><c>GET /visitor/reactions?post=slug</c>: a published post's counts, kept 30 seconds per replica.</item>
/// <item><c>POST /visitor/reactions</c> (JSON <see cref="ReactionRequest"/>): gives, changes or
/// takes back a reaction. It needs a solved proof of work, so only the page's script can send it.
/// One reaction per post per day counts from an address and browser (their daily hash, T-0158), at
/// most <see cref="VisitorOptions.ReactionsPerHour"/> an hour from them and
/// <see cref="VisitorOptions.ReactionsPerHourPerAddress"/> from the address alone (F-0188); another
/// answers the counts unchanged. A change names the reaction it replaces, which is taken back first.
/// A take-back counts only on the day of the reaction, from the same hash; otherwise only the
/// browser forgets it.</item>
/// </list>
/// Both answer 404 when the post isn't published or reactions are off.
/// </summary>
internal static partial class ReactionEndpoints
{
    private enum Given
    {
        Counted,
        Already,
        TooMany,
    }

    /// <summary>How long a replica keeps a post's counts.</summary>
    public static readonly TimeSpan CountsFor = TimeSpan.FromSeconds(30);

    // Every answer has a body, so the status code pages leave it alone.
    private const string NotHere = "No published post with reactions has that slug.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.Strict };

    /// <summary>Adds the store (Table Storage, or memory with content from files) and the daily clean-up of old events.</summary>
    /// <param name="services">The services.</param>
    /// <param name="inMemory">Whether to keep reactions in memory.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddReactions(this IServiceCollection services, bool inMemory)
    {
        if (inMemory)
        {
            services.AddSingleton<MemoryReactionStore>();
            services.AddSingleton<IReactionStore>(s => s.GetRequiredService<MemoryReactionStore>());
        }
        else
        {
            services.AddSingleton<IReactionStore, TableReactionStore>();
        }

        services.AddHostedService<ReactionCleanUp>();
        return services;
    }

    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    /// <returns>The app.</returns>
    public static IEndpointRouteBuilder MapReactions(this IEndpointRouteBuilder app)
    {
        app.MapGet("/visitor/reactions", GetAsync);
        app.MapPost("/visitor/reactions", PostAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(HttpContext context, ContentCatalogProvider content, IReactionStore store, IMemoryCache cache, string? post)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (Find(content.Current, post) is not { } item)
        {
            return Results.NotFound(NotHere);
        }

        var counts = await cache.GetOrCreateAsync(CacheKey(item), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CountsFor;
            entry.Size = 1;
            return store.GetAsync(item.SourcePath, context.RequestAborted);
        });
        return Results.Ok(new ReactionAnswer((counts ?? ReactionCounts.None).ByName(), Counted: false));
    }

    private static async Task<IResult> PostAsync(
        HttpContext context,
        ContentCatalogProvider content,
        IReactionStore store,
        IVisitorStore limits,
        ClientHashes hashes,
        ProofOfWork proof,
        VisitorOptions options,
        IMemoryCache cache,
        TimeProvider time,
        ILogger<ReactionRequest> logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasJsonContentType())
        {
            return Results.Text("Send JSON: post, kind, undo and proof.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        ReactionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ReactionRequest>(Json, context.RequestAborted);
        }
        catch (JsonException)
        {
            return Results.BadRequest("Send JSON: post, kind, undo and proof.");
        }

        if (request is null || Reactions.Parse(request.Kind) is not { } kind)
        {
            return Results.BadRequest("Send a known reaction: like, love, thanks, insightful or funny.");
        }

        if (Find(content.Current, request.Post) is not { } item)
        {
            return Results.NotFound(NotHere);
        }

        if (!await proof.VerifyAsync("reaction", request.Proof, context.RequestAborted))
        {
            return Results.BadRequest("The reaction needs the page's script: reload the page and try again.");
        }

        var now = time.GetUtcNow();
        var cancel = context.RequestAborted;
        var hash = await hashes.OfAddressAndBrowserAsync(context, "reaction", cancel);
        var reacting = new Reacting(limits, store, item.SourcePath, hash, now, logger);

        ReactionCounts? changed;
        if (request.Undo)
        {
            changed = await reacting.TakeBackAsync(kind, cancel);
        }
        else
        {
            // A change takes back the reaction it replaces, once the limits allow the new one.
            var replaced = Reactions.Parse(request.Replaces) is { } old && old != kind ? old : (ReactionKind?)null;
            var address = await hashes.OfAddressAsync(context, "reaction", cancel);
            var (outcome, counts) = await reacting.GiveAsync(kind, replaced, address, options, cancel);
            if (outcome == Given.TooMany)
            {
                LogLimited(logger);
                context.Response.Headers.RetryAfter = "3600";
                return Results.Text("Too many reactions from your address. Please try again later.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            changed = counts;
        }

        // The store answered the counts it wrote; only a request that changed nothing reads them.
        var current = changed ?? await store.GetAsync(item.SourcePath, cancel);
        cache.Set(CacheKey(item), current, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CountsFor, Size = 1 });
        return Results.Ok(new ReactionAnswer(current.ByName(), changed is not null));
    }

    // A published post, when reactions are on.
    private static ContentItem? Find(ContentCatalog catalog, string? slug) =>
        catalog.Site.Reactions && !string.IsNullOrEmpty(slug) ? catalog.FindPost(slug) : null;

    private static string CacheKey(ContentItem item) => "reactions:" + item.SourcePath;

    [LoggerMessage(Level = LogLevel.Information, Message = "Reactions: refused one over the hourly limit")]
    private static partial void LogLimited(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reactions: a write failed, and putting a counter back failed too; the day's counters and the totals may differ until tomorrow")]
    private static partial void LogRevertFailed(ILogger logger, Exception exception);

    /// <summary>
    /// One visitor's reaction to a post: the day's counters (what this hash gave the post today)
    /// and the post's totals, which only make sense together (F-0215). Once the first counter has
    /// moved, the other writes run to their end whatever happens to the request (not with
    /// <see cref="HttpContext.RequestAborted"/>), and a write that fails puts back the counters
    /// moved before it, newest first, so a failure leaves them as they were. The Azure SDK's own
    /// timeouts and retries still bound each write.
    /// </summary>
    private sealed class Reacting(IVisitorStore limits, IReactionStore store, string post, string hash, DateTimeOffset now, ILogger logger)
    {
        private static readonly CancellationToken Writes = CancellationToken.None;

        private string OnPost => $"{post}|post";

        // Only a reaction this hash gave today can be taken back; otherwise only the browser
        // forgets it, and this answers null.
        public async Task<ReactionCounts?> TakeBackAsync(ReactionKind given, CancellationToken cancel)
        {
            if (!await limits.TryUncountAsync(Once(given), hash, now, CountWindow.Day, cancel))
            {
                return null;
            }

            var moved = new Stack<Func<Task>>();
            moved.Push(() => limits.TryCountAsync(Once(given), hash, now, CountWindow.Day, 1, Writes));
            try
            {
                if (await limits.TryUncountAsync(OnPost, hash, now, CountWindow.Day, Writes))
                {
                    moved.Push(() => limits.TryCountAsync(OnPost, hash, now, CountWindow.Day, 1, Writes));
                }

                // A store that throws changed no total (TableReactionStore writes the event first
                // and deletes it again when the total can't change), so putting back is right.
                return await store.AddAsync(post, given, -1, now, Writes);
            }
            catch
            {
                await PutBackAsync(moved);
                throw;
            }
        }

        // Counts a new reaction against the limits, in order: the hourly limits of the hash and of
        // the address alone, then (after taking back the reaction it replaces, if any) once per
        // reaction and once per post a day from the hash, then the total. A refusal puts back what
        // it had counted, so nothing is spent on it, and a change refused for the hour keeps the
        // reaction it would have replaced. A failure puts back the counters too; a replaced
        // reaction that was taken back stays taken back, which the counters and totals agree on.
        public async Task<(Given Outcome, ReactionCounts? Counts)> GiveAsync(ReactionKind kind, ReactionKind? replaced, string address, VisitorOptions options, CancellationToken cancel)
        {
            if (!await limits.TryCountAsync("reaction", hash, now, CountWindow.Hour, options.ReactionsPerHour, cancel))
            {
                return (Given.TooMany, null);
            }

            var moved = new Stack<Func<Task>>();
            moved.Push(() => limits.TryUncountAsync("reaction", hash, now, CountWindow.Hour, Writes));
            try
            {
                if (!await limits.TryCountAsync("reaction-address", address, now, CountWindow.Hour, options.ReactionsPerHourPerAddress, Writes))
                {
                    await PutBackAsync(moved);
                    return (Given.TooMany, null);
                }

                moved.Push(() => limits.TryUncountAsync("reaction-address", address, now, CountWindow.Hour, Writes));
                if (replaced is { } previous)
                {
                    await TakeBackAsync(previous, Writes);
                }

                if (!await limits.TryCountAsync(Once(kind), hash, now, CountWindow.Day, 1, Writes))
                {
                    await PutBackAsync(moved);
                    return (Given.Already, null);
                }

                moved.Push(() => limits.TryUncountAsync(Once(kind), hash, now, CountWindow.Day, Writes));
                if (!await limits.TryCountAsync(OnPost, hash, now, CountWindow.Day, 1, Writes))
                {
                    // Another reaction to this post today, not named as replaced: the browser forgot it.
                    await PutBackAsync(moved);
                    return (Given.Already, null);
                }

                moved.Push(() => limits.TryUncountAsync(OnPost, hash, now, CountWindow.Day, Writes));
                return (Given.Counted, await store.AddAsync(post, kind, 1, now, Writes));
            }
            catch
            {
                await PutBackAsync(moved);
                throw;
            }
        }

        // What this hash gave today: one counter per reaction, and one for the post (T-0158).
        private string Once(ReactionKind kind) => $"{post}|{Reactions.Name(kind)}";

        // Puts back what moved, newest first; a step that fails is logged, not thrown, so the
        // failure that started it is the one the request ends with.
        private async Task PutBackAsync(Stack<Func<Task>> moved)
        {
            while (moved.TryPop(out var step))
            {
                try
                {
                    await step();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    LogRevertFailed(logger, e);
                }
            }
        }
    }
}

/// <summary>Deletes reaction events older than the analytics keep page views (13 months), once a day.</summary>
/// <param name="store">The reactions.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class ReactionCleanUp(IReactionStore store, TimeProvider time, ILogger<ReactionCleanUp> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    /// <summary>Deletes what's older than the retention.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many events went.</returns>
    public Task<int> CleanUpAsync(CancellationToken cancellationToken) =>
        store.CleanUpAsync(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddMonths(-Analytics.PageViewWriter.RetentionMonths), cancellationToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(7), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deleted = await CleanUpAsync(stoppingToken);
                    LogCleanedUp(logger, deleted);
                }
                catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogCleanUpFailed(logger, e);
                }

                await Task.Delay(Every, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reactions: deleted {Count} old events")]
    private static partial void LogCleanedUp(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reactions: the clean-up failed; it runs again tomorrow")]
    private static partial void LogCleanUpFailed(ILogger logger, Exception exception);
}
