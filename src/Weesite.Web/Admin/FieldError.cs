using Microsoft.AspNetCore.Components.Forms;

namespace Weesite.Web.Admin;

/// <summary>
/// The field a form's error is about (F-0081), so the page can mark it invalid and link to it.
/// </summary>
/// <param name="Id">The element's id, for the link and for plain inputs' <c>aria-invalid</c>.</param>
/// <param name="Property">
/// For Blazor's own inputs (<c>InputText</c> and the like), the form property behind the field:
/// they set <c>aria-invalid</c> and the <c>invalid</c> class from the form's validation
/// messages, and drop one given by hand.
/// </param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
public sealed record FieldError(string Id, string? Property = null)
{
    /// <summary>Gets <c>true</c> for the element with the error, for a plain input's <c>aria-invalid</c>.</summary>
    /// <param name="error">The form's error field, if any.</param>
    /// <param name="id">The element's id.</param>
    /// <returns><c>"true"</c>, or null so the attribute is left out.</returns>
    public static string? Invalid(FieldError? error, string id) => error?.Id == id ? "true" : null;

    /// <summary>
    /// Puts the error on its field in the form's validation messages, once per form, so a Blazor
    /// input marks itself invalid.
    /// </summary>
    /// <param name="context">The form's edit context.</param>
    /// <param name="model">The object that has the property, usually the form.</param>
    /// <param name="error">The field, if any.</param>
    /// <param name="message">The error message.</param>
    public static void Mark(EditContext context, object model, FieldError? error, string? message)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (error?.Property is not { } property || message is null || context.Properties.TryGetValue(typeof(FieldError), out _))
        {
            return;
        }

        context.Properties[typeof(FieldError)] = error;
        new ValidationMessageStore(context).Add(new FieldIdentifier(model, property), message);
    }
}
