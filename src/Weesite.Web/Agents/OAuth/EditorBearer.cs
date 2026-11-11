using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Weesite.Web.Admin;

namespace Weesite.Web.Agents.OAuth;

/// <summary>
/// The editor MCP endpoint's sign-in (ADR 0007): a bearer access token from Weesite's own
/// authorization server, whose editor is looked up again like a cookie's (every
/// <see cref="SignIn.RoleRecheck"/>). Without a valid token, a 401 points the client at the
/// protected resource metadata (RFC 9728), which names the authorization server.
/// </summary>
internal sealed class EditorBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    OAuthTokens tokens,
    AuthorizationRequests requests,
    IEditorDirectory editors,
    IMemoryCache cache)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The scheme's name.</summary>
    public const string SchemeName = "EditorBearer";

    /// <summary>The policy of the editor MCP endpoint: a bearer token of an editor.</summary>
    public const string Policy = "EditorMcp";

    /// <summary>The claim with the name of the app the token was issued to.</summary>
    public const string ClientClaim = "weesite:client";

    /// <summary>The claim with the token's grant.</summary>
    public const string GrantClaim = "weesite:grant";

    /// <summary>Gets the URL of the protected resource metadata for the editor MCP endpoint.</summary>
    /// <param name="request">The current request.</param>
    /// <param name="requests">The authorization requests (for the origin).</param>
    /// <returns>Such as <c>https://example.com/.well-known/oauth-protected-resource/mcp/editor</c>.</returns>
    public static string MetadataUrl(HttpRequest request, AuthorizationRequests requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        return requests.Issuer(request) + "/.well-known/oauth-protected-resource" + AuthorizationRequests.ResourcePath;
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var info = await tokens.ValidateAsync(header["Bearer ".Length..].Trim(), requests.Resource(Request), Context.RequestAborted);
        if (info is null)
        {
            return AuthenticateResult.Fail("invalid_token");
        }

        var role = await cache.GetOrCreateAsync(SignIn.RoleCacheKey(info.Email), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = SignIn.RoleRecheck;
            return editors.FindRoleAsync(info.Email, Context.RequestAborted);
        });
        if (role is null)
        {
            return AuthenticateResult.Fail("invalid_token");
        }

        var principal = EditorClaims.Principal("OAuth", info.GrantId, info.Email, info.Email, role.Value);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(ClientClaim, info.ClientName));
        identity.AddClaim(new Claim(GrantClaim, info.GrantId));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var error = Request.Headers.Authorization.Count > 0 ? ", error=\"invalid_token\"" : "";

        // The site's status code pages would turn the empty 401 into the not-found page; an MCP
        // client needs the 401 and its header to find the authorization server.
        if (Context.Features.Get<IStatusCodePagesFeature>() is { } statusPages)
        {
            statusPages.Enabled = false;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[HeaderNames.WWWAuthenticate] = $"Bearer resource_metadata=\"{MetadataUrl(Request, requests)}\"{error}";
        return Task.CompletedTask;
    }
}
