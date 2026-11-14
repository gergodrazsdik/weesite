using System.Globalization;
using System.Security.Claims;

namespace Weesite.Web.Admin;

/// <summary>What an editor may do in <c>/admin</c> (brief, Editor and authentication).</summary>
internal enum EditorRole
{
    /// <summary>Own posts only.</summary>
    Author,

    /// <summary>Everything, including editors and settings.</summary>
    Admin,
}

/// <summary>The claim types and policy names of signed-in editors.</summary>
internal static class EditorClaims
{
    /// <summary>The cookie scheme editors are signed in with.</summary>
    public const string Scheme = "Cookies";

    /// <summary>The provider an editor signed in with (<c>Microsoft</c> or <c>Google</c>).</summary>
    public const string Provider = "weesite:provider";

    /// <summary>When the editor signed in with their provider (round-trip format), for the absolute session lifetime.</summary>
    public const string SignedInAt = "weesite:signed_in_at";

    /// <summary>The policy every <c>/admin</c> page needs: any editor.</summary>
    public const string EditorPolicy = "Editor";

    /// <summary>The policy for admin-only pages.</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>Gets the signed-in editor's address, for audit entries; <c>(unknown)</c> without one (F-0120).</summary>
    /// <param name="user">The signed-in editor.</param>
    /// <returns>The address.</returns>
    public static string Email(ClaimsPrincipal user) =>
        user?.FindFirst(ClaimTypes.Email)?.Value ?? "(unknown)";

    /// <summary>
    /// Gets the name the sign-in provider gave, or null. The identity's name falls back to the
    /// email address, which mustn't become a public byline (F-0092).
    /// </summary>
    /// <param name="user">The signed-in editor.</param>
    /// <returns>The provider's name for them, or null.</returns>
    public static string? ProvidedName(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var name = user.FindFirst(ClaimTypes.Name)?.Value?.Trim();
        return string.IsNullOrEmpty(name) || name.Contains('@', StringComparison.Ordinal) ? null : name;
    }

    /// <summary>Builds the signed-in editor's identity.</summary>
    /// <param name="provider">The sign-in provider.</param>
    /// <param name="subject">The provider's stable user ID.</param>
    /// <param name="email">The verified email address.</param>
    /// <param name="name">The display name, if the provider gave one.</param>
    /// <param name="role">The editor's role.</param>
    /// <param name="signedInAt">When they signed in with the provider: set for the sign-in cookie.</param>
    /// <returns>The principal for the cookie.</returns>
    public static ClaimsPrincipal Principal(string provider, string subject, string email, string? name, EditorRole role, DateTimeOffset? signedInAt = null)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, $"{provider}:{subject}"),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? email : name),
                new Claim(ClaimTypes.Role, role.ToString()),
                new Claim(Provider, provider),
            ],
            Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        if (signedInAt is { } at)
        {
            identity.AddClaim(new Claim(SignedInAt, at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        }

        return new ClaimsPrincipal(identity);
    }

    /// <summary>Gets when the editor signed in with their provider.</summary>
    /// <param name="user">The signed-in editor.</param>
    /// <returns>The time, or null when the principal doesn't say.</returns>
    public static DateTimeOffset? SignedInTime(ClaimsPrincipal user) =>
        user?.FindFirst(SignedInAt)?.Value is { } value
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
}

/// <summary>Finds an editor's role by verified email address.</summary>
internal interface IEditorDirectory
{
    /// <summary>Finds the role of an email address on the allowlist.</summary>
    /// <param name="email">The verified email address.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    /// <returns>The role, or null when the address isn't allowed in.</returns>
    Task<EditorRole?> FindRoleAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Finds when the editor last ended all their sessions (signed out everywhere, or was removed):
    /// sessions signed in before then are over.
    /// </summary>
    /// <param name="email">The verified email address.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    /// <returns>The time, or null.</returns>
    Task<DateTimeOffset?> FindSessionsEndedAsync(string email, CancellationToken cancellationToken);
}

/// <summary>An editor's role and when they last ended all their sessions, as <see cref="SignIn.FindStatusAsync"/> caches them.</summary>
/// <param name="Role">The role, or null when no longer an editor.</param>
/// <param name="SessionsEnded">When they last ended all their sessions, or null.</param>
internal sealed record EditorStatus(EditorRole? Role, DateTimeOffset? SessionsEnded);

/// <summary>
/// The admins from configuration (<c>Site:Admins</c>, a comma- or semicolon-separated list of email
/// addresses), so the maintainer can never lock themselves out (ADR 0004).
/// </summary>
internal sealed class ConfiguredAdmins(IConfiguration configuration) : IEditorDirectory
{
    private readonly IReadOnlyList<string> _list = Split(configuration["Site:Admins"]);
    private readonly HashSet<string> _admins = [.. Split(configuration["Site:Admins"]).Select(Normalize)];

    /// <summary>Gets the configured admin addresses, as configured.</summary>
    public IReadOnlyList<string> Addresses => _list;

    /// <summary>Gets whether an address is a configured admin.</summary>
    /// <param name="email">The address.</param>
    /// <returns>True for a configured admin.</returns>
    public bool Contains(string email) => _admins.Contains(Normalize(email));

    /// <inheritdoc />
    public Task<EditorRole?> FindRoleAsync(string email, CancellationToken cancellationToken) =>
        Task.FromResult<EditorRole?>(_admins.Contains(Normalize(email)) ? EditorRole.Admin : null);

    /// <inheritdoc />
    /// <remarks>Kept with the editors (<see cref="IEditorStore"/>): <see cref="EditorDirectory"/> asks there.</remarks>
    public Task<DateTimeOffset?> FindSessionsEndedAsync(string email, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Ask EditorDirectory: sessions are kept with the editors.");

    /// <summary>
    /// Normalizes an email address for comparison: trimmed, and only ASCII letters upper-cased.
    /// Unicode case folding would make different addresses equal ("ı" and "i" both become "I"),
    /// so another account could match an editor's (T-0122).
    /// </summary>
    /// <param name="email">The address.</param>
    /// <returns>Trimmed, with a to z upper-cased.</returns>
    public static string Normalize(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return string.Create(email.Trim().Length, email.Trim(), (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] is >= 'a' and <= 'z' ? (char)(source[i] - 32) : source[i];
            }
        });
    }

    private static string[] Split(string? value) =>
        (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
