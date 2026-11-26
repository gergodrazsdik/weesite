using System.Globalization;
using System.Threading.RateLimiting;

namespace Weesite.Web;

/// <summary>
/// Per-address rate limits (brief, Security hardening; T-0053) on what costs the most or invites
/// guessing: search (every query runs the embedding model), the MCP endpoint, and sign-in (the
/// form post and the identity providers' callbacks). Fixed one-minute windows, counted per client
/// address (an IPv6 address by its /64, as the hourly limits count it) and per kind; everything
/// else is unlimited. Over the limit: 429 with Retry-After.
/// </summary>
internal static class RateLimits
{
    /// <summary>The window every limit counts in.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Adds the limiter; <c>RateLimits:SearchPerMinute</c>, <c>McpPerMinute</c>, <c>McpEditorPerMinute</c>, <c>McpUploadPerMinute</c>, <c>OAuthPerMinute</c>, <c>SignInPerMinute</c>, <c>PreviewPerMinute</c>, <c>VisitorPerMinute</c> and <c>VisitorReadPerMinute</c> change the limits.</summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["search"] = configuration.GetValue("RateLimits:SearchPerMinute", 30),
            ["mcp"] = configuration.GetValue("RateLimits:McpPerMinute", 60),
            ["mcp-editor"] = configuration.GetValue("RateLimits:McpEditorPerMinute", 300),
            ["mcp-upload"] = configuration.GetValue("RateLimits:McpUploadPerMinute", 30),
            ["sign-in"] = configuration.GetValue("RateLimits:SignInPerMinute", 10),
            ["oauth"] = configuration.GetValue("RateLimits:OAuthPerMinute", 30),
            ["preview"] = configuration.GetValue("RateLimits:PreviewPerMinute", 120),
            ["visitor"] = configuration.GetValue("RateLimits:VisitorPerMinute", 30),
            ["visitor-read"] = configuration.GetValue("RateLimits:VisitorReadPerMinute", 120),
        };

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                Kind(context) is { } kind && limits[kind] > 0
                    ? RateLimitPartition.GetFixedWindowLimiter($"{kind}|{Visitors.ClientHashes.Normalize(ClientAddress.Of(context))}", _ => new FixedWindowRateLimiterOptions
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
    /// <returns><c>search</c>, <c>mcp</c>, <c>mcp-editor</c>, <c>mcp-upload</c>, <c>oauth</c>, <c>sign-in</c>, <c>preview</c>, <c>visitor</c>, <c>visitor-read</c>, or null for none.</returns>
    public static string? Kind(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = context.Request.Path;
        if (path.StartsWithSegments("/search", StringComparison.OrdinalIgnoreCase))
        {
            return "search";
        }

        // Files sent to upload addresses (T-0137) count on their own, whatever their header, so
        // neither they nor the public /mcp can use up the other's limit (T-0177).
        if (path.StartsWithSegments("/mcp/editor/upload", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp-upload";
        }

        // An app working for an editor makes many calls in a row: requests to the editor endpoint
        // with a bearer token have a limit of their own, still per address (F-0068). The limiter
        // runs before sign-in, so it can't count per grant: made-up tokens would each get one.
        if (path.StartsWithSegments("/mcp/editor", StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp-editor";
        }

        if (path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp";
        }

        // The authorization server for the editor MCP endpoint (ADR 0007): registration, tokens,
        // consent decisions, and its metadata.
        if (path.StartsWithSegments("/oauth", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/.well-known/oauth-authorization-server", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/.well-known/oauth-protected-resource", StringComparison.OrdinalIgnoreCase))
        {
            return "oauth";
        }

        // The editor's live preview renders on every pause in typing; rendering is the costliest
        // thing an editor can ask for (T-0122).
        if (HttpMethods.IsPost(context.Request.Method) && IsPath(path, "/admin/preview"))
        {
            return "preview";
        }

        // An app's preview links (T-0187) open without signing in: each visit reads the app's grant.
        if (path.StartsWithSegments(Agents.PreviewLinks.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "preview";
        }

        // What visitors send (ADR 0011): challenges, what they answer, and the contact form's posts.
        // Not what every post page reads: its reaction counts, which a replica keeps in memory, and
        // the comment form's token and who's reading: a reader going through posts quickly would
        // otherwise use up what reacting and commenting need. Reading a comment, one's own or the
        // ones waiting on a post, has a higher limit of its own: each is a storage read.
        if (HttpMethods.IsGet(context.Request.Method)
            && (path.StartsWithSegments("/visitor/reactions", StringComparison.OrdinalIgnoreCase)
                || IsPath(path, "/visitor/comments/form")
                || IsPath(path, "/visitor/comments/me")))
        {
            return null;
        }

        if (HttpMethods.IsGet(context.Request.Method) && path.StartsWithSegments("/visitor/comments", StringComparison.OrdinalIgnoreCase))
        {
            return "visitor-read";
        }

        if (path.StartsWithSegments("/visitor", StringComparison.OrdinalIgnoreCase)
            || (HttpMethods.IsPost(context.Request.Method) && IsPath(path, "/contact")))
        {
            return "visitor";
        }

        var signInPost = HttpMethods.IsPost(context.Request.Method) && IsPath(path, "/admin/sign-in");
        return signInPost
            || path.StartsWithSegments("/signin-microsoft", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/signin-google", StringComparison.OrdinalIgnoreCase)
            ? "sign-in"
            : null;
    }

    /// <summary>
    /// Whether a request's path is a page's, as routing matches it: without regard to case or a
    /// trailing slash, so <c>/contact/</c> counts as <c>/contact</c>.
    /// </summary>
    /// <param name="path">The request's path.</param>
    /// <param name="page">The page's path, such as <c>/contact</c>.</param>
    /// <returns>True when they match.</returns>
    public static bool IsPath(PathString path, string page)
    {
        var value = path.Value.AsSpan().TrimEnd('/');
        return value.Equals(page, StringComparison.OrdinalIgnoreCase);
    }
}
