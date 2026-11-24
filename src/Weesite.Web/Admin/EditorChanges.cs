using System.Net.Mail;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using Weesite.Web.Agents.OAuth;

namespace Weesite.Web.Admin;

/// <summary>
/// What a change to the editors came to: a closed hierarchy (C# 15), so a <c>switch</c> over it
/// must handle every case (T-0122).
/// </summary>
internal closed record EditorOutcome
{
    /// <summary>Gets the status the editors page shows it by, in its address.</summary>
    public string Status => this switch
    {
        Saved => "saved",
        Unchanged => "unchanged",
        Removed => "removed",
        Missing => "missing",
        Invalid => "invalid",
        Self => "self",
        Configured => "configured",
    };

    /// <summary>The editor was added, or their role changed.</summary>
    /// <param name="Editor">The editor as saved.</param>
    /// <param name="RoleChanged">Whether an existing editor's role changed, rather than an editor being added.</param>
    internal sealed record Saved(Editor Editor, bool RoleChanged) : EditorOutcome;

    /// <summary>The editor already had that role: nothing was written or logged.</summary>
    internal sealed record Unchanged : EditorOutcome;

    /// <summary>The editor was removed.</summary>
    internal sealed record Removed : EditorOutcome;

    /// <summary>There was no such editor to remove.</summary>
    internal sealed record Missing : EditorOutcome;

    /// <summary>The address or the role isn't valid.</summary>
    internal sealed record Invalid : EditorOutcome;

    /// <summary>Admins don't change their own entry.</summary>
    internal sealed record Self : EditorOutcome;

    /// <summary>The address is a configured admin, changed in configuration only.</summary>
    internal sealed record Configured : EditorOutcome;
}

/// <summary>
/// Changes to the editors (the editors page): checks, the write, the audit entry, and dropping
/// the cached role so the change applies at the editor's next request.
/// </summary>
internal sealed class EditorChanges(ConfiguredAdmins admins, IEditorStore store, IAuditLog audit, IMemoryCache cache, OAuthTokens tokens, TimeProvider time)
{
    /// <summary>The longest email address accepted (RFC 5321).</summary>
    public const int MaxEmailLength = 254;

    /// <summary>Adds an editor or changes their role.</summary>
    /// <param name="user">The admin making the change.</param>
    /// <param name="email">The editor's address.</param>
    /// <param name="role">The role: <c>Admin</c> or <c>Author</c>.</param>
    /// <param name="cancellationToken">Stops the change.</param>
    /// <returns>The outcome.</returns>
    public async Task<EditorOutcome> SetAsync(ClaimsPrincipal user, string email, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!IsAddress(email) || !Enum.TryParse<EditorRole>(role, out var parsed) || !Enum.IsDefined(parsed))
        {
            return new EditorOutcome.Invalid();
        }

        if (Check(user, email) is { } refusal)
        {
            return refusal;
        }

        var actor = Actor(user);
        var existing = await store.FindAsync(email, cancellationToken);

        // The same address again with the role it has: nothing changes, so nothing is logged (F-0091 L12).
        if (existing?.Role == parsed)
        {
            return new EditorOutcome.Unchanged();
        }

        var saved = new Editor(email.Trim(), parsed, actor, time.GetUtcNow());
        await store.SetAsync(saved, cancellationToken);
        var action = existing is null ? "editor-added" : "editor-role-changed";
        // Saved: the audit entry isn't stopped with the request (F-0248).
        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, action, $"{email.Trim()} ({parsed})"), CancellationToken.None);
        cache.Remove(SignIn.RoleCacheKey(email));
        return new EditorOutcome.Saved(saved, RoleChanged: existing is not null);
    }

    /// <summary>Removes an editor.</summary>
    /// <param name="user">The admin making the change.</param>
    /// <param name="email">The editor's address.</param>
    /// <param name="cancellationToken">Stops the change.</param>
    /// <returns>The outcome.</returns>
    public async Task<EditorOutcome> RemoveAsync(ClaimsPrincipal user, string email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!IsAddress(email))
        {
            return new EditorOutcome.Invalid();
        }

        if (Check(user, email) is { } refusal)
        {
            return refusal;
        }

        if (!await store.RemoveAsync(email, cancellationToken))
        {
            return new EditorOutcome.Missing();
        }

        // Removed: what follows isn't stopped with the request, or a closed tab would leave their
        // sessions and apps alive (F-0248). The audit entry is written also when a step fails.
        try
        {
            await EndAccessAsync(email, Actor(user));
        }
        finally
        {
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(user), "editor-removed", email.Trim()), CancellationToken.None);
        }

        return new EditorOutcome.Removed();
    }

    /// <summary>
    /// Ends a removed editor's access: their sessions end (and stay ended if they're added again
    /// later), their cached role goes, and the apps they allowed are revoked (ADR 0007). Not
    /// stopped with a request, and each step runs even when one before it failed; a failure is
    /// thrown once they've all run. The editors page and the reset both remove editors this way.
    /// </summary>
    /// <param name="email">The editor's address.</param>
    /// <param name="actor">Who removed them, for the revocations' audit entries.</param>
    /// <returns>A task.</returns>
    internal async Task EndAccessAsync(string email, string actor)
    {
        var failures = new List<Exception>();
        try
        {
            await store.EndSessionsAsync(email, time.GetUtcNow(), CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failures.Add(e);
        }

        cache.Remove(SignIn.RoleCacheKey(email));
        try
        {
            await tokens.RevokeAllAsync(email, actor, CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failures.Add(e);
        }

        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }
    }

    /// <summary>Gets whether a string is a plain email address.</summary>
    /// <param name="email">The string.</param>
    /// <returns>True for an address such as <c>name@example.com</c>.</returns>
    internal static bool IsAddress(string? email) =>
        email is { Length: > 0 and <= MaxEmailLength }
        && MailAddress.TryCreate(email.Trim(), out var address)
        && string.Equals(address.Address, email.Trim(), StringComparison.Ordinal)
        && address.Host.Contains('.', StringComparison.Ordinal);

    private static string Actor(ClaimsPrincipal user) => EditorClaims.Email(user);

    // Nobody changes their own entry (no self-demotion or self-removal), and configured admins
    // are changed in configuration only.
    private EditorOutcome? Check(ClaimsPrincipal user, string email) =>
        string.Equals(ConfiguredAdmins.Normalize(Actor(user)), ConfiguredAdmins.Normalize(email), StringComparison.Ordinal) ? new EditorOutcome.Self()
        : admins.Contains(email) ? new EditorOutcome.Configured()
        : null;
}
