using Microsoft.AspNetCore.Diagnostics;

namespace Weesite.Web.Components.Pages;

/// <summary>
/// Whether a "not found" or error page answers a request under <c>/admin</c> (F-0091 L15), so it
/// can offer a way back to the editor. Those pages are shown in place (the router's not-found
/// page) or re-executed (status code pages, the exception handler), which keep the original path.
/// </summary>
internal static class InAdmin
{
    /// <summary>Gets whether the request, as first made, was under <c>/admin</c>.</summary>
    /// <param name="context">The request.</param>
    /// <returns>True for the editor's pages.</returns>
    public static bool Under(HttpContext? context)
    {
        if (context is null)
        {
            return false;
        }

        var path = context.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalPath
            ?? context.Features.Get<IExceptionHandlerPathFeature>()?.Path
            ?? context.Request.Path.Value;
        return path is not null && (path == "/admin" || path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase));
    }
}
