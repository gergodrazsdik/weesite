using System.Threading.Channels;
using Weesite.Content;

namespace Weesite.Web.Seo;

/// <summary>A changed address to tell IndexNow about.</summary>
/// <param name="Origin">The site's origin, such as <c>https://example.com</c>: the key file's host.</param>
/// <param name="Key">The site's key.</param>
/// <param name="Url">The changed page's absolute address.</param>
internal sealed record IndexNowUrl(string Origin, string Key, string Url);

/// <summary>
/// IndexNow (T-0146): when a save or delete changes what the public site shows, the search engines
/// that take part (Bing, Yandex, Naver, Seznam, Yep; Google doesn't) hear about the page's address
/// at once instead of at their next crawl. Only the changed post or page: a new or changed public
/// one, and one that was public and isn't any more or moved (the old address, so it's dropped).
/// Off unless the site settings turn it on; the save never waits for it.
/// </summary>
internal sealed partial class IndexNowChanges(
    ContentCatalogProvider content,
    SiteUrls urls,
    IHttpContextAccessor http,
    TimeProvider time,
    IndexNowQueue queue,
    ILogger<IndexNowChanges> logger) : IContentChanges
{
    /// <inheritdoc />
    public void Changed(ContentItem? before, ContentItem? after)
    {
        if (content.Current.Site.IndexNowKey is not { } key)
        {
            return;
        }

        var now = time.GetUtcNow();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (after is not null && after.IsPublicAt(now))
        {
            paths.Add(ContentUrls.PathOf(after));
        }

        if (before is not null && before.IsPublicAt(now))
        {
            paths.Add(ContentUrls.PathOf(before));
        }

        // A special page that Site settings show nowhere has no address (T-0159).
        paths.Remove("");
        if (paths.Count == 0)
        {
            return;
        }

        // The request's origin, as the key file is served from; outside one, Site:BaseUrl.
        var origin = http.HttpContext?.Request is { } request ? urls.Origin(request) : urls.BaseUrl;
        if (origin is null)
        {
            LogNoOrigin(logger);
            return;
        }

        foreach (var path in paths)
        {
            queue.Add(new IndexNowUrl(origin, key, origin + path));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "IndexNow: a change outside a request, and no Site:BaseUrl to name its address; not sent")]
    private static partial void LogNoOrigin(ILogger logger);
}

/// <summary>The changed addresses waiting to be sent: bounded, the oldest dropped when full.</summary>
internal sealed class IndexNowQueue
{
    private readonly Channel<IndexNowUrl> _channel = Channel.CreateBounded<IndexNowUrl>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Gets the reader the sender takes from.</summary>
    public ChannelReader<IndexNowUrl> Reader => _channel.Reader;

    /// <summary>Queues an address.</summary>
    /// <param name="url">The address.</param>
    public void Add(IndexNowUrl url) => _channel.Writer.TryWrite(url);
}

/// <summary>
/// Sends the queued addresses to IndexNow (<c>IndexNow:Endpoint</c>, api.indexnow.org by default,
/// which shares them with every engine that takes part): a few seconds after the first, so a burst
/// of saves goes as one request a host. Retries of 429 and 5xx come from the HTTP client's
/// standard resilience handler; a refusal is logged, never thrown.
/// </summary>
internal sealed partial class IndexNowSender(
    IndexNowQueue queue,
    IHttpClientFactory clients,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<IndexNowSender> logger) : BackgroundService
{
    /// <summary>The HTTP client's name.</summary>
    public const string ClientName = "indexnow";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = new Uri(configuration["IndexNow:Endpoint"] ?? "https://api.indexnow.org/indexnow");
        var wait = TimeSpan.FromSeconds(configuration.GetValue("IndexNow:BatchSeconds", 5));
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, stoppingToken);
                }

                var batch = new List<IndexNowUrl>();
                while (batch.Count < 10_000 && queue.Reader.TryRead(out var url))
                {
                    batch.Add(url);
                }

                foreach (var site in batch.GroupBy(u => (u.Origin, u.Key)))
                {
                    await SendAsync(endpoint, site.Key.Origin, site.Key.Key, [.. site.Select(u => u.Url).Distinct(StringComparer.Ordinal)], stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: what's queued is lost; the next crawl finds it.
        }
    }

    private async Task SendAsync(Uri endpoint, string origin, string key, IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        try
        {
            var body = new { host = new Uri(origin).Host, key, keyLocation = $"{origin}/{key}.txt", urlList = urls };
            using var response = await clients.CreateClient(ClientName).PostAsJsonAsync(endpoint, body, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                LogSent(logger, urls.Count, (int)response.StatusCode);
            }
            else
            {
                // 403: the key file isn't served at keyLocation; 422: the addresses aren't on its host.
                LogRefused(logger, urls.Count, (int)response.StatusCode);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Any failure but shutting down: the resilience handler's timeout and circuit breaker
            // throw their own exceptions, and one escaping here would stop the app (F-0208).
            LogFailed(logger, urls.Count, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IndexNow: sent {Count} addresses ({Status})")]
    private static partial void LogSent(ILogger logger, int count, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IndexNow: {Count} addresses refused ({Status}); check that the key file is served on the site's address")]
    private static partial void LogRefused(ILogger logger, int count, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "IndexNow: {Count} addresses not sent")]
    private static partial void LogFailed(ILogger logger, int count, Exception exception);
}

/// <summary>The IndexNow services and the key file.</summary>
internal static class IndexNowEndpoints
{
    /// <summary>Adds the listener, the queue and the sender.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddIndexNow(this IServiceCollection services)
    {
        services.AddSingleton<IndexNowQueue>();
        services.AddSingleton<IContentChanges, IndexNowChanges>();
        services.AddHttpClient(IndexNowSender.ClientName);
        services.AddHostedService<IndexNowSender>();
        return services;
    }

    /// <summary>
    /// Maps <c>/{key}.txt</c>: the key, as the engines fetch it to check the site sent it. Only the
    /// key in use, only while IndexNow is on; anything else is not found.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapIndexNowKey(this IEndpointRouteBuilder app) =>
        app.MapGetAndHead("/{name}.txt", (string name, ContentCatalogProvider content) =>
            content.Current.Site.IndexNowKey is { } key && string.Equals(name, key, StringComparison.Ordinal)
                ? Results.Text(key, "text/plain; charset=utf-8")
                : Results.NotFound());
}
