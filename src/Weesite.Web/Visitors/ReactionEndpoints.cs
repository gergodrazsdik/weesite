using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Weesite.Content;

namespace Weesite.Web.Visitors;

/// <summary>What the browser sends to react, or to take a reaction back.</summary>
/// <param name="Post">The post's slug.</param>
/// <param name="Kind">The reaction's name, such as <c>like</c>.</param>
/// <param name="Undo">True to take it back.</param>
/// <param name="Proof">The proof-of-work answer for <c>reaction</c>.</param>
internal sealed record ReactionRequest(string? Post, string? Kind, bool Undo, string? Proof);

/// <summary>What the reaction endpoints answer.</summary>
/// <param name="Counts">The post's counts by reaction name.</param>
/// <param name="Counted">Whether this request changed them.</param>
internal sealed record ReactionAnswer(IReadOnlyDictionary<string, int> Counts, bool Counted);

/// <summary>
/// The reactions to posts (ADR 0011), under <c>/visitor</c>, so the per-minute limit applies and
/// nothing is cached on the way:
/// <list type="bullet">
/// <item><c>GET /visitor/reactions?post=slug</c>: a published post's counts, kept 30 seconds per replica.</item>
/// <item><c>POST /visitor/reactions</c> (JSON <see cref="ReactionRequest"/>): gives or takes back a
/// reaction. It needs a solved proof of work, so only the page's script can send it. One of each
/// reaction per post per day counts from an address and browser (their daily hash), at most
/// <see cref="VisitorOptions.ReactionsPerHour"/> an hour; a second one answers the counts unchanged.
/// A take-back counts only on the day of the reaction, from the same hash; otherwise only the
/// browser forgets it.</item>
/// </list>
/// Both answer 404 when the post isn't published or reactions are off.
/// </summary>
internal static partial class ReactionEndpoints
{
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
            return Results.BadRequest("Send a known reaction: like, love, thanks or insightful.");
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
        var hash = await hashes.OfAddressAndBrowserAsync(context, "reaction", context.RequestAborted);
        var once = $"{item.SourcePath}|{Reactions.Name(kind)}";
        ReactionCounts counts;
        bool counted;
        if (request.Undo)
        {
            // Only a reaction this hash gave today can be taken back; otherwise only the browser forgets.
            counted = await limits.TryUncountAsync(once, hash, now, CountWindow.Day, context.RequestAborted);
            counts = counted
                ? await store.AddAsync(item.SourcePath, kind, -1, now, context.RequestAborted)
                : await store.GetAsync(item.SourcePath, context.RequestAborted);
        }
        else if (!await limits.TryCountAsync(once, hash, now, CountWindow.Day, 1, context.RequestAborted))
        {
            // Given today already, from this address and browser.
            (counts, counted) = (await store.GetAsync(item.SourcePath, context.RequestAborted), false);
        }
        else if (!await limits.TryCountAsync("reaction", hash, now, CountWindow.Hour, options.ReactionsPerHour, context.RequestAborted))
        {
            await limits.TryUncountAsync(once, hash, now, CountWindow.Day, context.RequestAborted);
            LogLimited(logger);
            context.Response.Headers.RetryAfter = "3600";
            return Results.Text("Too many reactions from your address. Please try again later.", statusCode: StatusCodes.Status429TooManyRequests);
        }
        else
        {
            (counts, counted) = (await store.AddAsync(item.SourcePath, kind, 1, now, context.RequestAborted), true);
        }

        cache.Set(CacheKey(item), counts, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CountsFor, Size = 1 });
        return Results.Ok(new ReactionAnswer(counts.ByName(), counted));
    }

    // A published post, when reactions are on.
    private static ContentItem? Find(ContentCatalog catalog, string? slug) =>
        catalog.Site.Reactions && !string.IsNullOrEmpty(slug) ? catalog.FindPost(slug) : null;

    private static string CacheKey(ContentItem item) => "reactions:" + item.SourcePath;

    [LoggerMessage(Level = LogLevel.Information, Message = "Reactions: refused one over the hourly limit")]
    private static partial void LogLimited(ILogger logger);
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
                catch (Exception e) when (e is not OperationCanceledException)
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
