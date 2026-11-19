using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Weesite.Web.Analytics;

namespace Weesite.Web.Visitors;

/// <summary>
/// Hashes of a visitor's address, for the limits on what visitors send (ADR 0011): never the address
/// itself. HMAC-SHA256 with the day's salt, the one the page views use, which every replica shares
/// and the analytics clean-up deletes after a day, so a hash can't be followed into the next day.
/// Each purpose hashes differently, so a contact hash can't be matched with a page view's visitor.
/// </summary>
/// <param name="salts">Where the day's salt comes from.</param>
/// <param name="time">The clock.</param>
internal sealed class ClientHashes(IPageViewStore salts, TimeProvider time)
{
    // The day's salt, cached; swapped as a whole, so concurrent requests see a matching pair.
    private DaySalt? _salt;

    /// <summary>Hashes the request's address for a purpose.</summary>
    /// <param name="context">The request.</param>
    /// <param name="purpose">What the hash counts, such as <c>contact</c>.</param>
    /// <param name="cancellationToken">Stops the salt's read.</param>
    /// <returns>16 hex digits.</returns>
    public async Task<string> OfAddressAsync(HttpContext context, string purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Hash(await SaltAsync(cancellationToken), purpose, Normalize(ClientAddress.Of(context)), userAgent: null);
    }

    /// <summary>Hashes the request's address and User-Agent for a purpose: a browser rather than a network.</summary>
    /// <param name="context">The request.</param>
    /// <param name="purpose">What the hash counts, such as <c>reaction</c>.</param>
    /// <param name="cancellationToken">Stops the salt's read.</param>
    /// <returns>16 hex digits.</returns>
    public async Task<string> OfAddressAndBrowserAsync(HttpContext context, string purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Hash(await SaltAsync(cancellationToken), purpose, Normalize(ClientAddress.Of(context)), context.Request.Headers.UserAgent.ToString());
    }

    /// <summary>The hash: HMAC-SHA256 of the purpose, the address and the User-Agent, if any, with the salt.</summary>
    /// <param name="salt">The day's salt.</param>
    /// <param name="purpose">What the hash counts.</param>
    /// <param name="address">The normalized address.</param>
    /// <param name="userAgent">The User-Agent, or null to hash the address alone.</param>
    /// <returns>16 hex digits.</returns>
    public static string Hash(byte[] salt, string purpose, string address, string? userAgent)
    {
        ArgumentNullException.ThrowIfNull(salt);
        var text = userAgent is null ? $"{purpose}\n{address}" : $"{purpose}\n{address}\n{userAgent}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(text)).AsSpan(0, 8));
    }

    /// <summary>
    /// The address as the limits count it: an IPv4 address as it is (without a port), an IPv6
    /// address cut to its /64, since one client can take any address within its /64.
    /// </summary>
    /// <param name="address">The address, as <see cref="ClientAddress.Of"/> gives it.</param>
    /// <returns>The address or network, or the input when it isn't an address.</returns>
    public static string Normalize(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IPAddress ip;
        if (IPAddress.TryParse(address, out var parsed))
        {
            ip = parsed;
        }
        else if (IPEndPoint.TryParse(address, out var endPoint))
        {
            ip = endPoint.Address;
        }
        else
        {
            return address;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return ip.ToString();
        }

        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    private async Task<byte[]> SaltAsync(CancellationToken cancellationToken)
    {
        var day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (Volatile.Read(ref _salt) is { } cached && cached.Day == day)
        {
            return cached.Salt;
        }

        var salt = await salts.SaltAsync(day, cancellationToken);
        Volatile.Write(ref _salt, new DaySalt(day, salt));
        return salt;
    }

    private sealed record DaySalt(DateOnly Day, byte[] Salt);
}
