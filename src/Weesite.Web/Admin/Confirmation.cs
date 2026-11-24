namespace Weesite.Web.Admin;

/// <summary>
/// The admin's confirm-first forms (F-0091 L13, U-67): deleting a message, the read messages or a
/// comment, removing an editor, revoking a connected app, signing out everywhere. admin/confirm-forms.js
/// asks in a dialog and adds <see cref="Field"/> once the editor confirms. Without it, such as
/// without JavaScript, which the admin needs (ADR 0005), the post changes nothing and goes back with
/// <see cref="Query"/>, for which the admin layout says why.
/// </summary>
internal static class Confirmation
{
    /// <summary>The form field the confirm dialog adds, with the value <c>1</c>.</summary>
    public const string Field = "confirmed";

    /// <summary>The query parameter, with the value <c>1</c>, on the page a refused post goes back to.</summary>
    public const string Query = "unconfirmed";

    /// <summary>Whether a post was confirmed in the dialog.</summary>
    /// <param name="context">The request.</param>
    /// <returns>True when the form carries <see cref="Field"/> set to <c>1</c>.</returns>
    public static async Task<bool> IsConfirmedAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.HasFormContentType
            && (await context.Request.ReadFormAsync(context.RequestAborted))[Field] == "1";
    }

    /// <summary>Goes back to the top of a page, where the admin layout says nothing changed.</summary>
    /// <param name="back">The local page, with its query if any, without a fragment.</param>
    /// <returns>The redirect.</returns>
    public static IResult Refused(string back)
    {
        ArgumentNullException.ThrowIfNull(back);
        return Results.Redirect($"{back}{(back.Contains('?', StringComparison.Ordinal) ? '&' : '?')}{Query}=1");
    }
}
