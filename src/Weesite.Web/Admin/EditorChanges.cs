using System.Net.Mail;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;

namespace Weesite.Web.Admin;

/// <summary>
/// Changes to the editors (the editors page): checks, the write, the audit entry, and dropping
/// the cached role so the change applies at the editor's next request.
/// </summary>
internal sealed class EditorChanges(ConfiguredAdmins admins, IEditorStore store, IAuditLog audit, IMemoryCache cache, TimeProvider time)
{
    /// <summary>The longest email address accepted (RFC 5321).</summary>
    public const int MaxEmailLength = 254;

    /// <summary>Adds an editor or changes their role.</summary>
    /// <param name="user">The admin making the change.</param>
    /// <param name="email">The editor's address.</param>
    /// <param name="role">The role: <c>Admin</c> or <c>Author</c>.</param>
    /// <param name="cancellationToken">Stops the change.</param>
    /// <returns>The status for the page: <c>saved</c>, <c>invalid</c>, <c>self</c> or <c>configured</c>.</returns>
    public async Task<string> SetAsync(ClaimsPrincipal user, string email, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!IsAddress(email) || !Enum.TryParse<EditorRole>(role, out var parsed) || !Enum.IsDefined(parsed))
        {
            return "invalid";
        }

        if (Check(user, email) is { } refusal)
        {
            return refusal;
        }

        var actor = Actor(user);
        var existing = await store.FindAsync(email, cancellationToken);
        await store.SetAsync(new Editor(email.Trim(), parsed, actor, time.GetUtcNow()), cancellationToken);
        var action = existing is null ? "editor-added" : "editor-role-changed";
        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, action, $"{email.Trim()} ({parsed})"), cancellationToken);
        cache.Remove(SignIn.RoleCacheKey(email));
        return "saved";
    }

    /// <summary>Removes an editor.</summary>
    /// <param name="user">The admin making the change.</param>
    /// <param name="email">The editor's address.</param>
    /// <param name="cancellationToken">Stops the change.</param>
    /// <returns>The status for the page: <c>removed</c>, <c>missing</c>, <c>invalid</c>, <c>self</c> or <c>configured</c>.</returns>
    public async Task<string> RemoveAsync(ClaimsPrincipal user, string email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!IsAddress(email))
        {
            return "invalid";
        }

        if (Check(user, email) is { } refusal)
        {
            return refusal;
        }

        if (!await store.RemoveAsync(email, cancellationToken))
        {
            return "missing";
        }

        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(user), "editor-removed", email.Trim()), cancellationToken);
        cache.Remove(SignIn.RoleCacheKey(email));
        return "removed";
    }

    /// <summary>Gets whether a string is a plain email address.</summary>
    /// <param name="email">The string.</param>
    /// <returns>True for an address such as <c>name@example.com</c>.</returns>
    internal static bool IsAddress(string? email) =>
        email is { Length: > 0 and <= MaxEmailLength }
        && MailAddress.TryCreate(email.Trim(), out var address)
        && string.Equals(address.Address, email.Trim(), StringComparison.Ordinal)
        && address.Host.Contains('.', StringComparison.Ordinal);

    private static string Actor(ClaimsPrincipal user) => user.FindFirst(ClaimTypes.Email)?.Value ?? "(unknown)";

    // Nobody changes their own entry (no self-demotion or self-removal), and configured admins
    // are changed in configuration only.
    private string? Check(ClaimsPrincipal user, string email) =>
        string.Equals(ConfiguredAdmins.Normalize(Actor(user)), ConfiguredAdmins.Normalize(email), StringComparison.Ordinal) ? "self"
        : admins.Contains(email) ? "configured"
        : null;
}
