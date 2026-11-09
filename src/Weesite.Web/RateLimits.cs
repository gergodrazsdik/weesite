using System.Globalization;
using System.Threading.RateLimiting;

namespace Weesite.Web;

/// <summary>
/// Per-address rate limits (brief, Security hardening; T-0053) on what costs the most or invites
/// guessing: search (every query runs the embedding model), the MCP endpoint, and sign-in (the
/// form post and the identity providers' callbacks). Fixed one-minute windows, counted per client
/// address and per kind; everything else is unlimited. Over the limit: 429 with Retry-After.
/// </summary>
internal static class RateLimits
{
    /// <summary>The window every limit counts in.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Adds the limiter; <c>RateLimits:SearchPerMinute</c>, <c>McpPerMinute</c> and <c>SignInPerMinute</c> change the limits.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["search"] = configuration.GetValue("RateLimits:SearchPerMinute", 30),
            ["mcp"] = configuration.GetValue("RateLimits:McpPerMinute", 60),
            ["sign-in"] = configuration.GetValue("RateLimits:SignInPerMinute", 10),
        };

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                Kind(context) is { } kind && limits[kind] > 0
                    ? RateLimitPartition.GetFixedWindowLimiter($"{kind}|{ClientAddress.Of(context)}", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits[kind],
                        Window = Window,
                        QueueLimit = 0,
                    })
                    : RateLimitPartition.GetNoLimiter(""));
            options.OnRejected = async (rejected, cancellationToken) =>
            {
                var response = rejected.HttpContext.Response;
                var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : Window;
                response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                response.ContentType = "text/plain; charset=utf-8";
                await response.WriteAsync("Too many requests from your address. Please wait a minute and try again.\n", cancellationToken);
            };
        });
    }

    /// <summary>Gets which limit a request counts against.</summary>
    /// <param name="context">The request.</param>
    /// <returns><c>search</c>, <c>mcp</c>, <c>sign-in</c>, or null for none.</returns>
    public static string? Kind(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/search", StringComparison.OrdinalIgnoreCase))
        {
            return "search";
        }

        if (path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp";
        }

        var signInPost = HttpMethods.IsPost(context.Request.Method) && path.Equals("/admin/sign-in", StringComparison.OrdinalIgnoreCase);
        return signInPost
            || path.StartsWithSegments("/signin-microsoft", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/signin-google", StringComparison.OrdinalIgnoreCase)
            ? "sign-in"
            : null;
    }
}
