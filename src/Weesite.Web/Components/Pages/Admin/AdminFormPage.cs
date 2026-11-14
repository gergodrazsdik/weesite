using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Weesite.Web.Admin;

namespace Weesite.Web.Components.Pages.Admin;

/// <summary>
/// What the admin pages that save one form have in common (F-0120): the request, the message
/// after a post, "Saved." once after the redirect (TempData), and the end of a save: the audit
/// entry, "Saved." and the way back.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor components are public, so their base class must be too.")]
public abstract class AdminFormPage : ComponentBase
{
    /// <summary>
    /// Gets or sets "Saved." after the redirect, in TempData rather than the address, so a reload
    /// doesn't show it again (T-0122).
    /// </summary>
    [SupplyParameterFromTempData(Name = "saved")]
    public string? Saved { get; set; }

    /// <summary>Gets or sets the request.</summary>
    [CascadingParameter]
    private protected HttpContext? HttpContext { get; set; }

    /// <summary>Gets or sets the audit log.</summary>
    [Inject]
    private protected IAuditLog Audit { get; set; } = default!;

    /// <summary>Gets or sets the clock.</summary>
    [Inject]
    private protected TimeProvider Time { get; set; } = default!;

    /// <summary>Gets or sets the navigation.</summary>
    [Inject]
    private protected NavigationManager Navigation { get; set; } = default!;

    /// <summary>Gets or sets the message to show: an error after a post, or "Saved.".</summary>
    private protected string? Message { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="Message"/> is an error.</summary>
    private protected bool IsError { get; set; }

    /// <summary>Shows "Saved." when the page was reached by a save, once: TempData keeps what the property holds when the request ends.</summary>
    /// <param name="text">The message.</param>
    private protected void TakeSaved(string text)
    {
        Message = Saved == "1" ? text : null;
        Saved = null;
    }

    /// <summary>Ends a save: the audit entry (not stopped with the request: the save happened), "Saved." for the next page, and the redirect.</summary>
    /// <param name="action">The audit action.</param>
    /// <param name="target">What changed.</param>
    /// <param name="address">Where to go.</param>
    /// <returns>A task.</returns>
    private protected async Task SavedAsync(string action, string target, string address)
    {
        await Audit.WriteAsync(new AuditEntry(Time.GetUtcNow(), EditorClaims.Email(HttpContext!.User), action, target), CancellationToken.None);
        Saved = "1";
        Navigation.NavigateTo(address);
    }
}
