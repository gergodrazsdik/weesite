using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Weesite.Web.Admin;

namespace Weesite.Web.Agents.OAuth;

/// <summary>The tokens a successful exchange or refresh gives the client.</summary>
/// <param name="AccessToken">The access token.</param>
/// <param name="ExpiresIn">Its lifetime in seconds.</param>
/// <param name="RefreshToken">The new refresh token.</param>
internal sealed record TokenResponse(string AccessToken, int ExpiresIn, string RefreshToken);

/// <summary>An OAuth error (RFC 6749, section 5.2).</summary>
/// <param name="Error">The error code, such as <c>invalid_grant</c>.</param>
/// <param name="Description">What went wrong, for the client's developer.</param>
internal sealed record OAuthError(string Error, string Description);

/// <summary>What a valid access token says.</summary>
/// <param name="GrantId">The grant it belongs to.</param>
/// <param name="Email">The editor it acts for.</param>
/// <param name="ClientId">The client it was issued to.</param>
/// <param name="ClientName">The client's name.</param>
/// <param name="Resource">The resource it's for.</param>
/// <param name="Access">What the editor let the app do.</param>
/// <param name="CanPublish">
/// Whether the app may publish: all that tokens issued before F-0070 say, and still written for an
/// older version of the app after a rollback.
/// </param>
internal sealed record AccessTokenInfo(string GrantId, string Email, string ClientId, string ClientName, string Resource, AppAccess Access = AppAccess.Drafts, bool CanPublish = false)
{
    /// <summary>What the token allows, whichever version of the app issued it.</summary>
    public AppAccess Allowed => CanPublish ? AppAccess.Publish : Enum.IsDefined(Access) ? Access : AppAccess.Drafts;
}

/// <summary>
/// Codes and tokens (ADR 0007). A code is random, stored as its hash, valid for a minute and
/// exchanged once with its PKCE verifier. An access token is a Data Protection payload (the keys
/// every replica shares) that names its grant, valid for an hour. A refresh token is the grant's ID
/// and a random secret, stored as a hash; each refresh replaces it, and presenting the previous one
/// again revokes the grant, since only a copy can do that. Exchanges and refreshes check that the
/// editor is still an editor.
/// </summary>
internal sealed partial class OAuthTokens(
    IOAuthStore store,
    IDataProtectionProvider protection,
    IEditorDirectory editors,
    IAuditLog audit,
    IMemoryCache cache,
    TimeProvider time,
    ILogger<OAuthTokens> logger)
{
    /// <summary>How long a code works.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>How long an access token works.</summary>
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);

    /// <summary>How long a refresh token works without being used.</summary>
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    /// <summary>How long a grant's existence is trusted before an access token's check looks again.</summary>
    public static readonly TimeSpan GrantRecheck = TimeSpan.FromSeconds(30);

    private readonly ITimeLimitedDataProtector _access =
        protection.CreateProtector("Weesite.OAuth.AccessToken.v1").ToTimeLimitedDataProtector();

    /// <summary>Issues a code after the editor consented.</summary>
    /// <param name="client">The client.</param>
    /// <param name="redirectUri">The request's redirect URI.</param>
    /// <param name="codeChallenge">The PKCE challenge (S256).</param>
    /// <param name="email">The editor.</param>
    /// <param name="resource">The resource.</param>
    /// <param name="access">What the editor let the app do.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The code to send to the redirect URI.</returns>
    public async Task<string> IssueCodeAsync(OAuthClient client, string redirectUri, string codeChallenge, string email, string resource, AppAccess access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var code = Random();
        await store.SaveCodeAsync(
            new AuthorizationCode(Hash(code), client.Id, client.Name, redirectUri, codeChallenge, email, resource, time.GetUtcNow() + CodeLifetime, access),
            cancellationToken);
        return code;
    }

    /// <summary>Exchanges a code for tokens (<c>grant_type=authorization_code</c>), creating the grant.</summary>
    /// <param name="code">The code.</param>
    /// <param name="clientId">The client's ID.</param>
    /// <param name="redirectUri">The redirect URI, as in the authorization request.</param>
    /// <param name="codeVerifier">The PKCE verifier.</param>
    /// <param name="resource">The resource, if the client names it.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The tokens, or an error.</returns>
    public async Task<(TokenResponse? Tokens, OAuthError? Error)> ExchangeCodeAsync(
        string? code, string? clientId, string? redirectUri, string? codeVerifier, string? resource, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(codeVerifier))
        {
            return (null, new OAuthError("invalid_request", "code, client_id and code_verifier are required."));
        }

        // Taken first, so a code works once even when the rest fails.
        var stored = await store.TakeCodeAsync(Hash(code), cancellationToken);
        if (stored is null || stored.ExpiresAt <= time.GetUtcNow())
        {
            return (null, InvalidGrant("The code is unknown, used or expired."));
        }

        if (stored.ClientId != clientId || stored.RedirectUri != redirectUri)
        {
            return (null, InvalidGrant("The code was issued to another client or redirect URI."));
        }

        if (!VerifierMatches(codeVerifier, stored.CodeChallenge))
        {
            return (null, InvalidGrant("The code verifier doesn't match the code challenge."));
        }

        if (resource is { Length: > 0 } && resource != stored.Resource)
        {
            return (null, new OAuthError("invalid_target", "The resource isn't the one the code was issued for."));
        }

        if (await editors.FindRoleAsync(stored.Email, cancellationToken) is null)
        {
            return (null, InvalidGrant("The editor who consented is no longer an editor."));
        }

        var now = time.GetUtcNow();
        var grantId = Random();
        var secret = Random();
        var grant = new OAuthGrant(grantId, stored.Email, stored.ClientId, stored.ClientName, stored.Resource, now, now, Hash(secret), null, now + RefreshLifetime, stored.Access);
        await store.AddGrantAsync(grant, cancellationToken);
        await store.MarkClientGrantedAsync(stored.ClientId, cancellationToken);
        await audit.WriteAsync(new AuditEntry(now, stored.Email, "oauth-grant", $"{stored.ClientName} ({Describe(stored.Access)})"), cancellationToken);
        LogGranted(logger, stored.ClientName);
        return (Tokens(grant, secret), null);
    }

    /// <summary>Exchanges a refresh token for new tokens (<c>grant_type=refresh_token</c>).</summary>
    /// <param name="refreshToken">The refresh token.</param>
    /// <param name="clientId">The client's ID.</param>
    /// <param name="resource">The resource, if the client names it.</param>
    /// <param name="cancellationToken">Cancels the refresh.</param>
    /// <returns>The tokens, or an error.</returns>
    public async Task<(TokenResponse? Tokens, OAuthError? Error)> RefreshAsync(string? refreshToken, string? clientId, string? resource, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(clientId))
        {
            return (null, new OAuthError("invalid_request", "refresh_token and client_id are required."));
        }

        if (Split(refreshToken) is not var (grantId, secret) || await store.FindGrantAsync(grantId, cancellationToken) is not { } grant
            || grant.ClientId != clientId)
        {
            return (null, InvalidGrant("The refresh token is unknown or revoked."));
        }

        var hash = Hash(secret);
        var now = time.GetUtcNow();
        if (grant.PreviousRefreshHash is { } previous && FixedEquals(previous, hash))
        {
            // The token was used before: one of two holders is not the client. End the grant.
            await RevokeGrantAsync(grant, "oauth-refresh-reuse", cancellationToken);
            return (null, InvalidGrant("The refresh token was used before; the grant is revoked."));
        }

        if (!FixedEquals(grant.RefreshHash, hash) || grant.RefreshExpiresAt <= now)
        {
            return (null, InvalidGrant("The refresh token is unknown, replaced or expired."));
        }

        if (resource is { Length: > 0 } && resource != grant.Resource)
        {
            return (null, new OAuthError("invalid_target", "The resource isn't the one the grant is for."));
        }

        if (await editors.FindRoleAsync(grant.Email, cancellationToken) is null)
        {
            await RevokeGrantAsync(grant, "oauth-revoke", cancellationToken);
            return (null, InvalidGrant("The editor is no longer an editor."));
        }

        var newSecret = Random();
        var renewed = grant with
        {
            UsedAt = now,
            RefreshHash = Hash(newSecret),
            PreviousRefreshHash = grant.RefreshHash,
            RefreshExpiresAt = now + RefreshLifetime,
        };
        return await store.ReplaceGrantAsync(renewed, cancellationToken)
            ? (Tokens(renewed, newSecret), null)
            : (null, InvalidGrant("The refresh token was replaced at the same moment; sign in again."));
    }

    /// <summary>Revokes the grant a refresh or access token belongs to (RFC 7009); unknown tokens are ignored.</summary>
    /// <param name="token">The token.</param>
    /// <param name="clientId">The client's ID: only its own tokens.</param>
    /// <param name="cancellationToken">Cancels the revocation.</param>
    /// <returns>A task.</returns>
    public async Task RevokeAsync(string? token, string? clientId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        var grantId = Split(token) is var (id, _) ? id : Read(token)?.GrantId;
        if (grantId is not null && await store.FindGrantAsync(grantId, cancellationToken) is { } grant
            && (clientId is null || grant.ClientId == clientId))
        {
            await RevokeGrantAsync(grant, "oauth-revoke", cancellationToken);
        }
    }

    /// <summary>Lists the connected apps: an editor's grants, or everyone's, newest first.</summary>
    /// <param name="email">The editor, or null for all.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The grants.</returns>
    public async Task<IReadOnlyList<Components.Pages.Admin.ConnectedApps.App>> ListGrantsAsync(string? email, CancellationToken cancellationToken) =>
        [.. (await store.ListGrantsAsync(email, cancellationToken))
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new Components.Pages.Admin.ConnectedApps.App(g.Id, g.ClientName, g.Email, g.Access switch
            {
                AppAccess.Publish => "ConnectedAppPublishing",
                AppAccess.Edit => "ConnectedAppEdits",
                _ => "ConnectedAppDraftsOnly",
            }, g.CreatedAt, g.UsedAt))];

    /// <summary>Revokes a grant from the admin (connected apps): the editor's own, or anyone's for an admin.</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="user">Who revokes it.</param>
    /// <param name="cancellationToken">Cancels the revocation.</param>
    /// <returns>Whether it was revoked; false when it's gone or not theirs.</returns>
    public async Task<bool> RevokeAsync(string grantId, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var email = user.FindFirstValue(ClaimTypes.Email) ?? "";
        if (await store.FindGrantAsync(grantId, cancellationToken) is not { } grant
            || !(user.IsInRole(nameof(EditorRole.Admin)) || string.Equals(grant.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return await RevokeGrantAsync(grant, "oauth-revoke", cancellationToken, actor: email);
    }

    /// <summary>Revokes every grant of an editor, when they're removed.</summary>
    /// <param name="email">The editor.</param>
    /// <param name="actor">Who removed them.</param>
    /// <param name="cancellationToken">Cancels the revocation.</param>
    /// <returns>A task.</returns>
    public async Task RevokeAllAsync(string email, string actor, CancellationToken cancellationToken)
    {
        foreach (var grant in await store.ListGrantsAsync(email, cancellationToken))
        {
            await RevokeGrantAsync(grant, "oauth-revoke", cancellationToken, actor);
        }
    }

    /// <summary>
    /// Checks an access token: its signature and lifetime, its resource, and that its grant still
    /// exists (looked up again after <see cref="GrantRecheck"/>).
    /// </summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="resource">The resource being called.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What the token says, or null when it's not valid here.</returns>
    public async Task<AccessTokenInfo?> ValidateAsync(string? token, string resource, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token) || Read(token) is not { } info || info.Resource != resource)
        {
            return null;
        }

        var exists = await cache.GetOrCreateAsync("oauth-grant:" + info.GrantId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = GrantRecheck;
            return await store.FindGrantAsync(info.GrantId, cancellationToken) is not null;
        });
        return exists ? info : null;
    }

    /// <summary>What an access level is called in the audit log.</summary>
    /// <param name="access">The access.</param>
    /// <returns>Its name.</returns>
    public static string Describe(AppAccess access) => access switch
    {
        AppAccess.Publish => "drafts, edits and publishing",
        AppAccess.Edit => "drafts and edits",
        _ => "drafts only",
    };

    /// <summary>Whether a PKCE verifier matches its S256 challenge (RFC 7636).</summary>
    /// <param name="verifier">The verifier.</param>
    /// <param name="challenge">The challenge.</param>
    /// <returns>Whether they match.</returns>
    public static bool VerifierMatches(string verifier, string challenge)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(challenge);
        return verifier.Length is >= 43 and <= 128
            && verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')
            && FixedEquals(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), challenge);
    }

    /// <summary>Whether a PKCE challenge has the S256 form: 43 base64url characters.</summary>
    /// <param name="challenge">The challenge.</param>
    /// <returns>Whether it's well-formed.</returns>
    public static bool IsChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static OAuthError InvalidGrant(string description) => new("invalid_grant", description);

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // A refresh token is "<grant ID>.<secret>"; an access token has no dot.
    private static (string GrantId, string Secret)? Split(string token) =>
        token.Split('.') is [{ Length: 43 } id, { Length: 43 } secret] ? (id, secret) : null;

    private TokenResponse Tokens(OAuthGrant grant, string secret)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new AccessTokenInfo(grant.Id, grant.Email, grant.ClientId, grant.ClientName, grant.Resource, grant.Access, grant.Access == AppAccess.Publish));
        var token = Base64Url(_access.Protect(payload, time.GetUtcNow() + AccessLifetime));
        return new TokenResponse(token, (int)AccessLifetime.TotalSeconds, $"{grant.Id}.{secret}");
    }

    private AccessTokenInfo? Read(string token)
    {
        try
        {
            var padded = token.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            var payload = _access.Unprotect(Convert.FromBase64String(padded), out var expiration);
            return expiration > time.GetUtcNow() ? JsonSerializer.Deserialize<AccessTokenInfo>(payload) : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    // The audit entry's actor is the editor whose grant it was, unless someone else (an admin)
    // revoked it; then its target names the editor too.
    private async Task<bool> RevokeGrantAsync(OAuthGrant grant, string action, CancellationToken cancellationToken, string? actor = null)
    {
        if (!await store.DeleteGrantAsync(grant.Id, cancellationToken))
        {
            return false;
        }

        cache.Remove("oauth-grant:" + grant.Id);
        var other = actor is not null && !string.Equals(actor.Trim(), grant.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor ?? grant.Email, action, other ? $"{grant.ClientName} ({grant.Email})" : grant.ClientName), cancellationToken);
        LogRevoked(logger, grant.ClientName, action);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OAuth grant to {Client}")]
    private static partial void LogGranted(ILogger logger, string client);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OAuth grant to {Client} revoked ({Action})")]
    private static partial void LogRevoked(ILogger logger, string client, string action);
}
