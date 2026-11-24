using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Weesite.Web;

/// <summary>
/// The address a request came from, as the analytics hash and the rate limits see it, and which
/// proxies in front of the app are trusted to say so.
/// </summary>
internal static class ClientAddress
{
    /// <summary>
    /// Gets the client's address: the connection's, as the forwarded headers set it (see
    /// <see cref="AddClientAddress"/>). An IPv4 address that came in over IPv6 is given as IPv4.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The address, or an empty string when there's none.</returns>
    public static string Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress is { } address
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString()
            : "";
    }

    /// <summary>
    /// Sets which proxies the app trusts for the client's address (<c>X-Forwarded-For</c>) and the
    /// scheme (<c>X-Forwarded-Proto</c>), from <c>ForwardedHeaders:*</c>:
    /// <list type="bullet">
    /// <item><c>Proxies</c> (1): how many proxies in front of the app each add their entry. Behind
    /// the Container Apps ingress, one: the last entry is the one it added, and earlier ones come
    /// from the client. 0 when nothing is in front: the headers are ignored and the connection's
    /// own address and scheme count.</item>
    /// <item><c>KnownProxies</c> and <c>KnownNetworks</c> (empty): the proxies' addresses, or
    /// networks in CIDR notation such as <c>10.0.0.0/8</c>, comma-separated. Set, an entry counts
    /// only when the connection it came over is from one of them. Empty, any connection's does,
    /// which is right when the proxies are the only way in.</item>
    /// </list>
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddClientAddress(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var proxies = configuration.GetValue("ForwardedHeaders:Proxies", 1);
        if (proxies < 0)
        {
            throw new InvalidOperationException($"ForwardedHeaders:Proxies must be 0 or more; got {proxies}.");
        }

        var known = List(configuration["ForwardedHeaders:KnownProxies"])
            .Select(a => IPAddress.TryParse(a, out var address)
                ? address
                : throw new InvalidOperationException($"ForwardedHeaders:KnownProxies must be IP addresses; got '{a}'."))
            .ToList();
        var networks = List(configuration["ForwardedHeaders:KnownNetworks"])
            .Select(n => System.Net.IPNetwork.TryParse(n, out var network)
                ? network
                : throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks must be networks such as 10.0.0.0/8; got '{n}'."))
            .ToList();

        return services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = proxies == 0 ? ForwardedHeaders.None : ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = Math.Max(proxies, 1);
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var address in known)
            {
                options.KnownProxies.Add(address);
            }

            foreach (var network in networks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
    }

    private static string[] List(string? value) =>
        (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
