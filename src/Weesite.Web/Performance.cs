using System.IO.Compression;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Weesite.Content;
using Weesite.Web.Theming;

namespace Weesite.Web;

/// <summary>
/// Output caching and response compression for the public site (T-0056; brief, Performance).
/// Public pages, feeds and agent texts are rendered once per content version and theme choice and
/// served from memory until the content reloads; dynamic responses are compressed with Brotli or
/// gzip. Static files are precompressed and fingerprinted by MapStaticAssets already.
/// </summary>
internal static partial class Performance
{
    /// <summary>The tag every cached response carries; a content reload evicts it.</summary>
    public const string ContentTag = "content";

    /// <summary>Adds the cache (16 MB at most) and compression.</summary>
    /// <param name="services">The services.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddPerformance(this IServiceCollection services)
    {
        services.AddOutputCache(options =>
        {
            // The replica has 0.5 GiB; a page is about 15 KB, so this holds about a thousand.
            options.SizeLimit = 16 * 1024 * 1024;
            options.MaximumBodySize = 1024 * 1024;
            options.AddBasePolicy(new PublicResponsePolicy());
        });
        services.AddHostedService<CacheEviction>();

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
            [
                "application/atom+xml",
                "application/rss+xml",
                "application/feed+json",
                "text/markdown",
                "image/svg+xml",
            ]);
        });
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        return services;
    }

    /// <summary>
    /// Compresses responses outside <c>/admin</c> and <c>/mcp</c>. Admin pages carry antiforgery
    /// tokens beside text an attacker may influence, which is what BREACH needs; public pages carry
    /// no secrets. MCP answers may stream.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void UsePublicCompression(this IApplicationBuilder app) =>
        app.UseWhen(
            context => !IsUnder(context, "/admin") && !IsUnder(context, "/mcp"),
            branch => branch.UseResponseCompression());

    /// <summary>
    /// Caches the fonts for a year. Their folders carry the font version
    /// (<c>/fonts/plex-sans-1.1.0/</c>), so a new version gets new addresses; MapStaticAssets
    /// would otherwise have browsers revalidate them every hour, because site.css can't refer to
    /// fingerprinted addresses.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void UseImmutableFonts(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsUnder(context, "/fonts") && context.Request.Path.Value!.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.OnStarting(() =>
                {
                    if (context.Response.StatusCode == StatusCodes.Status200OK)
                    {
                        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                    }

                    return Task.CompletedTask;
                });
            }

            await next(context);
        });

    private static bool IsUnder(HttpContext context, string segment) =>
        context.Request.Path.StartsWithSegments(segment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Caches anonymous GETs without a query string, outside the admin, search, MCP, media,
    /// sign-in callbacks and health checks: the same for every visitor except for the theme
    /// cookie, which picks the page's colour mode. Only 200 responses that set no cookie and
    /// don't ask not to be stored are kept. Ten minutes at most, so anything that depends on the
    /// time (a scheduled post coming due reloads the content anyway) is never far behind.
    /// </summary>
    internal sealed class PublicResponsePolicy : IOutputCachePolicy
    {
        private static readonly string[] Uncached = ["/admin", "/search", "/mcp", "/media", "/theme", "/signin-microsoft", "/signin-google", "/health", "/alive", "/error", "/not-found"];

        /// <summary>Gets whether a request may be answered from, and stored in, the cache.</summary>
        /// <param name="context">The request.</param>
        /// <returns>True when it may.</returns>
        public static bool IsCacheable(HttpContext context)
        {
            var request = context.Request;
            return (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                && !request.QueryString.HasValue
                && context.User.Identity?.IsAuthenticated != true
                && request.Headers.Authorization.Count == 0
                && !Uncached.Any(path => IsUnder(context, path));
        }

        /// <inheritdoc />
        public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            ArgumentNullException.ThrowIfNull(context);
            var cacheable = IsCacheable(context.HttpContext);
            context.EnableOutputCaching = true;
            context.AllowCacheLookup = cacheable;
            context.AllowCacheStorage = cacheable;
            context.AllowLocking = true;
            context.ResponseExpirationTimeSpan = TimeSpan.FromMinutes(10);
            context.CacheVaryByRules.QueryKeys = default;
            context.CacheVaryByRules.VaryByValues["theme"] = ThemePreference.Read(context.HttpContext) ?? "system";

            // A page that started rendering just before a reload is stored under the old version,
            // which nothing asks for again; the eviction then frees it.
            var version = context.HttpContext.RequestServices.GetRequiredService<ContentCatalogProvider>().Version;
            context.CacheVaryByRules.VaryByValues["content"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Tags.Add(ContentTag);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

        /// <inheritdoc />
        public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
        {
            ArgumentNullException.ThrowIfNull(context);
            var response = context.HttpContext.Response;
            var cacheControl = response.Headers.CacheControl.ToString();
            if (response.StatusCode != StatusCodes.Status200OK
                || response.Headers.SetCookie.Count > 0
                || cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase)
                || cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase))
            {
                context.AllowCacheStorage = false;
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Empties the cache when the content reloads: a save, a scheduled post coming due, another replica's change.</summary>
    private sealed partial class CacheEviction(ContentCatalogProvider content, IOutputCacheStore store, ILogger<CacheEviction> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            content.Changed += Evict;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            content.Changed -= Evict;
            return Task.CompletedTask;
        }

        private void Evict(object? sender, EventArgs e) =>
            _ = Task.Run(async () =>
            {
                try
                {
                    await store.EvictByTagAsync(ContentTag, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogEvictFailed(ex);
                }
            });

        [LoggerMessage(Level = LogLevel.Error, Message = "Emptying the output cache after a content reload failed")]
        private partial void LogEvictFailed(Exception exception);
    }
}
