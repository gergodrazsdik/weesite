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

    /// <summary>The policy every <c>/admin</c> page needs: any editor.</summary>
    public const string EditorPolicy = "Editor";

    /// <summary>The policy for admin-only pages.</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>Builds the signed-in editor's identity.</summary>
    /// <param name="provider">The sign-in provider.</param>
    /// <param name="subject">The provider's stable user ID.</param>
    /// <param name="email">The verified email address.</param>
    /// <param name="name">The display name, if the provider gave one.</param>
    /// <param name="role">The editor's role.</param>
    /// <returns>The principal for the cookie.</returns>
    public static ClaimsPrincipal Principal(string provider, string subject, string email, string? name, EditorRole role) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, $"{provider}:{subject}"),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(name) ? email : name),
                new Claim(ClaimTypes.Role, role.ToString()),
                new Claim(Provider, provider),
            ],
            Scheme,
            ClaimTypes.Name,
            ClaimTypes.Role));
}

/// <summary>Finds an editor's role by verified email address.</summary>
internal interface IEditorDirectory
{
    /// <summary>Finds the role of an email address on the allowlist.</summary>
    /// <param name="email">The verified email address.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    /// <returns>The role, or null when the address isn't allowed in.</returns>
    Task<EditorRole?> FindRoleAsync(string email, CancellationToken cancellationToken);
}

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

    /// <summary>Normalizes an email address for comparison.</summary>
    /// <param name="email">The address.</param>
    /// <returns>Trimmed and upper-cased.</returns>
    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    private static string[] Split(string? value) =>
        (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
