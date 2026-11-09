using Microsoft.AspNetCore.HttpOverrides;

namespace Weesite.Web;

/// <summary>The address a request came from, as the analytics hash and the rate limits see it.</summary>
internal static class ClientAddress
{
    /// <summary>
    /// Gets the client's address. Behind the Container Apps ingress the connection comes from the
    /// proxy, which appends the client's address to X-Forwarded-For: the last entry is the one it
    /// added (earlier ones come from the client and can be anything).
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The address, or an empty string when there's none.</returns>
    public static string Of(HttpContext context)
    {
        var forwarded = context.Request.Headers[ForwardedHeadersDefaults.XForwardedForHeaderName].ToString();
        var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return last ?? context.Connection.RemoteIpAddress?.ToString() ?? "";
    }
}
