using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Weesite.Web.Admin;

/// <summary>
/// Notices in free text that an admin endpoint passes to the page it redirects to, in the address:
/// an upload's or an import's refusal, a deleted item's title. Sealed with Data Protection for the
/// signed-in editor, for an hour, so a page shows only a notice the site wrote for that editor, and
/// a link with text of someone else's choosing shows nothing (T-0177). Fixed notices go as keys
/// mapped to resource strings, as before.
/// </summary>
internal static class AdminNotices
{
    /// <summary>How long a sealed notice can be shown: long enough for the redirect and a reload.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private const string Purpose = "Weesite.Admin.Notices";

    /// <summary>Seals a notice for the signed-in editor, for the address of the page it goes to.</summary>
    /// <param name="context">The request, for the editor and the Data Protection keys.</param>
    /// <param name="text">The notice.</param>
    /// <returns>The sealed notice, URL-safe and escaped for a query string.</returns>
    public static string Seal(HttpContext context, string text)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Uri.EscapeDataString(Protector(context).Protect(text, Lifetime));
    }

    /// <summary>Opens a sealed notice.</summary>
    /// <param name="context">The request, for the editor and the Data Protection keys.</param>
    /// <param name="value">The value from the address.</param>
    /// <returns>The notice, or null when there's none, or it wasn't sealed by the site for this editor, or it's too old.</returns>
    public static string? Open(HttpContext? context, string? value)
    {
        if (context is null || string.IsNullOrEmpty(value))
        {
            return null;
        }

        try
        {
            return Protector(context).Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // One purpose per editor: a notice sealed in one editor's session doesn't open in another's.
    private static ITimeLimitedDataProtector Protector(HttpContext context) =>
        context.RequestServices.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(Purpose, ConfiguredAdmins.Normalize(EditorClaims.Email(context.User)))
            .ToTimeLimitedDataProtector();
}
