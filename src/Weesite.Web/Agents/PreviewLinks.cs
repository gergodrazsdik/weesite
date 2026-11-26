using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Weesite.Content;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;

namespace Weesite.Web.Agents;

/// <summary>A preview link from <c>create_preview_link</c> (T-0187).</summary>
/// <param name="Url">The address to open: the preview link, or the public address of an item that's public.</param>
/// <param name="ExpiresAt">When the link stops working (UTC, ISO 8601); null for a public address.</param>
/// <param name="Note">What the link shows.</param>
internal sealed record EditorPreviewLink(string Url, string? ExpiresAt, string Note);

/// <summary>
/// Preview links for the editor MCP endpoint (T-0187, ADR 0007 amendment of 2026-11-26): the
/// preview page of a saved draft or scheduled item, for an app to open in its own browser or fetch,
/// without signing in to <c>/admin</c>. The token is a Data Protection payload with the item's path,
/// the app's grant, the editor and when it expires: nothing is stored. Each visit checks it again:
/// the grant must still exist and belong to the editor, who must still be an editor who may edit
/// the item, so revoking the app or removing the editor ends its links. A link shows the last saved
/// version, so the app can reload it after each save, until it expires.
/// </summary>
internal sealed class PreviewLinks(
    IDataProtectionProvider protection,
    IOAuthStore store,
    IEditorDirectory editors,
    IMemoryCache cache,
    ContentCatalogProvider content,
    TimeProvider time)
{
    /// <summary>The path the links start with, followed by the token.</summary>
    public const string Prefix = "/admin/preview/link";

    /// <summary>How long a link works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly IDataProtector _protector = protection.CreateProtector("Weesite.PreviewLinks.v1");

    /// <summary>Makes a link to an item's preview for the app and editor of the request.</summary>
    /// <param name="user">The editor, signed in with the app's access token.</param>
    /// <param name="path">The item's path.</param>
    /// <returns>The link's path, without the site's address, and when it expires.</returns>
    public (string Path, DateTimeOffset ExpiresAt) Create(ClaimsPrincipal user, string path)
    {
        ArgumentNullException.ThrowIfNull(user);
        var grantId = user.FindFirstValue(EditorBearerHandler.GrantClaim) ?? throw new InvalidOperationException("Not an app's request.");
        var expiresAt = time.GetUtcNow() + Lifetime;
        var payload = string.Join('\n', expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), grantId, EditorClaims.Email(user), path);
        return ($"{Prefix}/{_protector.Protect(payload)}", expiresAt);
    }

    /// <summary>Finds the item a link shows, checking the app's access and the editor's rights as they are now.</summary>
    /// <param name="token">The token from the link.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    /// <returns>The item, or why the link shows nothing.</returns>
    public async Task<PreviewLinkResult> ResolveAsync(string? token, CancellationToken cancellationToken)
    {
        string[] parts;
        try
        {
            parts = _protector.Unprotect(token ?? "").Split('\n');
        }
        catch (CryptographicException)
        {
            return new PreviewLinkResult(null, PreviewLinkProblem.Unknown);
        }

        if (parts is not [var expires, var grantId, var email, var path]
            || !long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return new PreviewLinkResult(null, PreviewLinkProblem.Unknown);
        }

        if (DateTimeOffset.FromUnixTimeSeconds(seconds) <= time.GetUtcNow())
        {
            return new PreviewLinkResult(null, PreviewLinkProblem.Expired);
        }

        // The app's access and the editor's role as they are now, not as they were.
        if (await store.FindGrantAsync(grantId, cancellationToken) is not { } grant
            || !string.Equals(grant.Email, email, StringComparison.OrdinalIgnoreCase)
            || (await SignIn.FindStatusAsync(cache, editors, email, cancellationToken)).Role is not { } role)
        {
            return new PreviewLinkResult(null, PreviewLinkProblem.AccessEnded);
        }

        var editor = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Role, role.ToString())],
            "PreviewLink",
            ClaimTypes.Email,
            ClaimTypes.Role));
        return content.Current.AllItems.FirstOrDefault(i => i.SourcePath == path) is { Status: not ContentStatus.Archived } item
            && EditorRights.CanEdit(editor, item.Kind, item.AuthorId, isNew: false)
            ? new PreviewLinkResult(item, null, DateTimeOffset.FromUnixTimeSeconds(seconds))
            : new PreviewLinkResult(null, PreviewLinkProblem.Unknown);
    }

    /// <summary>
    /// Leaves a link's token out of the request's trace: the path names the page instead, so
    /// Application Insights never holds a link that still works.
    /// </summary>
    /// <param name="activity">The request's activity.</param>
    /// <param name="request">The request.</param>
    public static void Redact(Activity activity, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            activity.SetTag("url.path", Prefix + "/{token}");
            activity.SetTag("url.query", null);
        }
    }
}

/// <summary>Why a preview link shows nothing.</summary>
internal enum PreviewLinkProblem
{
    /// <summary>The token isn't one this site made, or the item is gone, archived or not the editor's.</summary>
    Unknown,

    /// <summary>The link's hour is over.</summary>
    Expired,

    /// <summary>The app was revoked, or the editor removed.</summary>
    AccessEnded,
}

/// <summary>The item a preview link shows, or why it shows none.</summary>
/// <param name="Item">The item.</param>
/// <param name="Problem">Why there's none.</param>
/// <param name="ExpiresAt">When the link stops working, when it shows an item.</param>
internal sealed record PreviewLinkResult(ContentItem? Item, PreviewLinkProblem? Problem, DateTimeOffset? ExpiresAt = null);
