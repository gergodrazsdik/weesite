using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Weesite.Web.Agents.OAuth;

/// <summary>What a client sent to register itself, or what its metadata document says (RFC 7591 fields).</summary>
internal sealed class ClientMetadata
{
    /// <summary>Gets or sets the client ID (metadata documents only: their own URL).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    /// <summary>Gets or sets the client's name.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("client_name")]
    public string? ClientName { get; set; }

    /// <summary>Gets or sets where it may receive codes.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("redirect_uris")]
    public IReadOnlyList<string>? RedirectUris { get; set; }

    /// <summary>Gets or sets how it authenticates at the token endpoint: only <c>none</c> is supported.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; set; }

    /// <summary>Gets or sets the grant types it uses.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("grant_types")]
    public IReadOnlyList<string>? GrantTypes { get; set; }

    /// <summary>Gets or sets the response types it uses.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("response_types")]
    public IReadOnlyList<string>? ResponseTypes { get; set; }
}

/// <summary>
/// The clients the authorization server knows (ADR 0007): those described by a Client ID Metadata
/// Document, whose client ID is the HTTPS URL of a JSON document Weesite fetches, and those that
/// registered themselves at <c>/oauth/register</c> (RFC 7591). All are public clients with PKCE.
/// </summary>
internal sealed partial class ClientRegistry(IOAuthStore store, IHttpClientFactory http, ILogger<ClientRegistry> logger) : IDisposable
{
    /// <summary>The name of the HTTP client that fetches metadata documents.</summary>
    public const string HttpClientName = "oauth-client-metadata";

    /// <summary>The largest metadata document read.</summary>
    public const int MaxDocumentBytes = 5 * 1024;

    private const int MaxRedirectUris = 10;
    private const int MaxNameLength = 100;
    private static readonly TimeSpan DocumentLifetime = TimeSpan.FromMinutes(10);

    // A failed fetch is retried soon: the client may have fixed its document (F-0065).
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(1);

    // Documents in a cache of their own, with a size limit: anyone can choose the URLs (F-0065).
    private readonly MemoryCache _documents = new(new MemoryCacheOptions { SizeLimit = 1000 });

    /// <inheritdoc />
    public void Dispose() => _documents.Dispose();

    /// <summary>Finds a client by its ID: fetches its metadata document, or looks up its registration.</summary>
    /// <param name="clientId">The client ID.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The client, or null when it's unknown or its document isn't valid.</returns>
    public async Task<OAuthClient?> FindAsync(string? clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 512)
        {
            return null;
        }

        return IsDocumentUrl(clientId)
            ? await DocumentAsync(clientId, cancellationToken)
            : await store.FindClientAsync(clientId, cancellationToken);
    }

    /// <summary>Registers a client (RFC 7591).</summary>
    /// <param name="metadata">What it sent.</param>
    /// <param name="cancellationToken">Cancels the registration.</param>
    /// <returns>The client, or why it was refused (RFC 7591 error codes).</returns>
    public async Task<(OAuthClient? Client, OAuthError? Error)> RegisterAsync(ClientMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (Validate(metadata) is { } error)
        {
            return (null, error);
        }

        var client = new OAuthClient(
            Base64Url(RandomNumberGenerator.GetBytes(16)),
            Name(metadata.ClientName),
            [.. metadata.RedirectUris!],
            FromDocument: false);
        await store.SaveClientAsync(client, cancellationToken);
        return (client, null);
    }

    /// <summary>Whether a redirect URI is one of the client's (RFC 8252: a loopback URI with any port).</summary>
    /// <param name="client">The client.</param>
    /// <param name="redirectUri">The redirect URI of a request.</param>
    /// <returns>Whether it may receive the code.</returns>
    public static bool IsRegisteredRedirect(OAuthClient client, string? redirectUri)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrEmpty(redirectUri) || !IsAllowedRedirect(redirectUri))
        {
            return false;
        }

        foreach (var registered in client.RedirectUris)
        {
            if (string.Equals(registered, redirectUri, StringComparison.Ordinal))
            {
                return true;
            }

            // Native apps listen on a port the system gives them, so a loopback URI matches on
            // everything but the port.
            if (Uri.TryCreate(registered, UriKind.Absolute, out var a) && Uri.TryCreate(redirectUri, UriKind.Absolute, out var b)
                && IsLoopbackHttp(a) && IsLoopbackHttp(b)
                && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.PathAndQuery, b.PathAndQuery, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a redirect URI is one Weesite sends codes to at all: HTTPS, loopback HTTP, or a
    /// native app's private-use scheme; never a fragment, credentials or a scheme a browser runs.
    /// </summary>
    /// <param name="redirectUri">The URI.</param>
    /// <returns>Whether it's allowed.</returns>
    public static bool IsAllowedRedirect(string redirectUri)
    {
        if (redirectUri.Length > 512 || !Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || redirectUri.Contains('#', StringComparison.Ordinal) || uri.UserInfo.Length > 0)
        {
            return false;
        }

        return uri.Scheme switch
        {
            "https" => uri.Host.Length > 0,
            "http" => IsLoopbackHttp(uri),
            "javascript" or "data" or "vbscript" or "file" or "about" or "blob" or "ftp" or "ws" or "wss" or "mailto" or "tel" => false,
            _ => SchemeRegex().IsMatch(uri.Scheme),
        };
    }

    /// <summary>Whether a client ID is a metadata document's URL: HTTPS with a path, nothing that could hide where it points.</summary>
    /// <param name="clientId">The client ID.</param>
    /// <returns>Whether to fetch it.</returns>
    public static bool IsDocumentUrl(string clientId) =>
        Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0
        && !clientId.Contains('#', StringComparison.Ordinal)
        && uri.AbsolutePath.Length > 1
        && uri.Segments.All(s => s.TrimEnd('/') is not ("." or ".."))
        && uri.IsDefaultPort;

    /// <summary>Whether an address is one a metadata fetch may connect to: public unicast only.</summary>
    /// <param name="address">The address.</param>
    /// <returns>Whether it's public.</returns>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return false;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => false,
            100 => b[1] is < 64 or > 127,          // 100.64.0.0/10, shared address space
            169 => b[1] != 254,                    // link-local, cloud metadata endpoints
            172 => b[1] is < 16 or > 31,
            192 => !(b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2) || (b[1] == 88 && b[2] == 99)),
            198 => b[1] is not (18 or 19) && !(b[1] == 51 && b[2] == 100),
            203 => !(b[1] == 0 && b[2] == 113),
            >= 224 => false,                       // multicast and reserved
            _ => true,
        };
    }

    /// <summary>
    /// Connects only to public addresses: the check runs on the address actually used, so a name
    /// that resolves differently on the second lookup (DNS rebinding) can't reach inside.
    /// </summary>
    /// <param name="context">The connection to make.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <returns>The connected stream.</returns>
    public static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken)).Where(IsPublicAddress).ToList();
        if (addresses.Count == 0)
        {
            throw new HttpRequestException($"{context.DnsEndPoint.Host} has no public address.");
        }

        // Each public address in turn: the first may be IPv6 on a network without it.
        SocketException? last = null;
        foreach (var address in addresses)
        {
            Socket? socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                var stream = new NetworkStream(socket, ownsSocket: true);
                socket = null;
                return stream;
            }
            catch (SocketException ex)
            {
                last = ex;
            }
            finally
            {
                socket?.Dispose();
            }
        }

        throw new HttpRequestException($"Couldn't connect to {context.DnsEndPoint.Host}.", last);
    }

    private async Task<OAuthClient?> DocumentAsync(string url, CancellationToken cancellationToken)
    {
        if (_documents.TryGetValue(url, out OAuthClient? cached))
        {
            return cached;
        }

        var client = await FetchDocumentAsync(url, cancellationToken);
        _documents.Set(url, client, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = client is null ? FailureLifetime : DocumentLifetime,
        });
        return client;
    }

    private static OAuthError? Validate(ClientMetadata metadata)
    {
        if (metadata.RedirectUris is not { Count: > 0 and <= MaxRedirectUris } uris)
        {
            return new("invalid_redirect_uri", $"redirect_uris needs 1 to {MaxRedirectUris} URIs.");
        }

        if (uris.Any(u => u is null))
        {
            return new("invalid_redirect_uri", "redirect_uris has an empty entry.");
        }

        if (uris.FirstOrDefault(u => !IsAllowedRedirect(u)) is { } bad)
        {
            return new("invalid_redirect_uri", $"Redirect URI not allowed: {bad}. Use HTTPS, http://localhost or http://127.0.0.1, or an app's own scheme.");
        }

        if (metadata.TokenEndpointAuthMethod is { } method && method != "none")
        {
            return new("invalid_client_metadata", "Only public clients are supported: token_endpoint_auth_method must be none.");
        }

        // Grant and response types Weesite doesn't use are ignored, as RFC 7591 allows: VS Code's
        // document also lists the device code grant. The client needs the ones Weesite uses.
        if (metadata.GrantTypes is { } grants && !grants.Contains("authorization_code"))
        {
            return new("invalid_client_metadata", "The client must use the authorization_code grant type; Weesite supports it and refresh_token.");
        }

        return metadata.ResponseTypes is { } responses && !responses.Contains("code")
            ? new("invalid_client_metadata", "The client must use the code response type.")
            : null;
    }

    private static string Name(string? name)
    {
        var trimmed = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return trimmed.Length == 0 ? "Unnamed app" : trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static bool IsLoopbackHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && (uri.Host is "127.0.0.1" or "[::1]" || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // A metadata document: HTTPS, no redirects, at most MaxDocumentBytes, a public address, its
    // client_id its own URL, and the same rules as a registration.
    private async Task<OAuthClient?> FetchDocumentAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDocumentBytes)
            {
                LogDocumentRefused(logger, url, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxDocumentBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            {
                read += n;
            }

            if (read > MaxDocumentBytes)
            {
                LogDocumentRefused(logger, url, "too large");
                return null;
            }

            var metadata = JsonSerializer.Deserialize<ClientMetadata>(buffer.AsSpan(0, read));
            if (metadata is null || !string.Equals(metadata.ClientId, url, StringComparison.Ordinal) || Validate(metadata) is not null)
            {
                LogDocumentRefused(logger, url, "not a valid client metadata document");
                return null;
            }

            return new OAuthClient(url, Name(metadata.ClientName), [.. metadata.RedirectUris!], FromDocument: true);
        }
        // A request the caller gave up on isn't a failed document: it isn't cached (F-0115).
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or TaskCanceledException or IOException) && !cancellationToken.IsCancellationRequested)
        {
            LogDocumentRefused(logger, url, ex.GetType().Name);
            return null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z][a-z0-9+.-]*$")]
    private static partial System.Text.RegularExpressions.Regex SchemeRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Client metadata document {Url} refused: {Reason}")]
    private static partial void LogDocumentRefused(ILogger logger, string url, string reason);
}
