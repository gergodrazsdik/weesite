using Microsoft.AspNetCore.Http.Features;

namespace Weesite.Web.Agents;

/// <summary>
/// Request body limits for what anyone may send to the public MCP endpoint and the authorization
/// server (T-0177), well below the server's own 30 MB: a JSON-RPC call to <c>/mcp</c> or a form or
/// registration to <c>/oauth</c> is small. A body over the limit is refused with 413 before it's
/// read: by its <c>Content-Length</c>, or, without one, by the server as it reads. The editor
/// endpoint (<c>/mcp/editor</c>) is left to its own limits: editors send whole files there.
/// </summary>
internal static class ProtocolBody
{
    /// <summary>The largest body for the public <c>/mcp</c>: 256 KB.</summary>
    public const int McpMaxBytes = 256 * 1024;

    /// <summary>The largest body for <c>/oauth</c>: 64 KB.</summary>
    public const int OAuthMaxBytes = 64 * 1024;

    /// <summary>Adds the limits, after the rate limiter, so a refused request still counts.</summary>
    /// <param name="app">The app.</param>
    /// <returns>The app.</returns>
    public static IApplicationBuilder UseProtocolBodyLimit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (LimitFor(context.Request) is { } limit)
            {
                if (context.Request.ContentLength > limit)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsync("The request is too large.\n", context.RequestAborted);
                    return;
                }

                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                {
                    size.MaxRequestBodySize = limit;
                }
            }

            await next(context);
        });

    /// <summary>The body limit for a request, or null where these limits don't apply.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The most bytes, or null.</returns>
    public static int? LimitFor(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.Path;
        return path.StartsWithSegments("/mcp/editor", StringComparison.OrdinalIgnoreCase) ? null
            : path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) ? McpMaxBytes
            : path.StartsWithSegments("/oauth", StringComparison.OrdinalIgnoreCase) ? OAuthMaxBytes
            : null;
    }
}
