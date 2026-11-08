using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Weesite.Web.Admin;

/// <summary>
/// The sign-in and sign-out posts, and the headers every <c>/admin</c> response gets: no
/// indexing, no caching.
/// </summary>
internal static class AdminEndpoints
{
    /// <summary>Marks <c>/admin</c> responses as private and not for search engines.</summary>
    /// <param name="app">The app.</param>
    public static void UseAdminHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                context.Response.Headers[HeaderNames.CacheControl] = "no-store";
            }

            await next(context);
        });

    /// <summary>Maps <c>POST /admin/sign-in</c> and <c>POST /admin/sign-out</c> (antiforgery-checked forms).</summary>
    /// <param name="app">The app.</param>
    public static void MapAdminSignIn(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/sign-in", ([FromForm] string provider, [FromForm] string? returnUrl, IConfiguration configuration) =>
            SignIn.ConfiguredProviders(configuration).Contains(provider)
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = LocalUrl(returnUrl) }, [provider])
                : Results.Redirect("/admin/sign-in"))
            .WithMetadata(new RequireAntiforgeryTokenAttribute());

        // No form fields to bind, so the token is checked here rather than by binding.
        app.MapPost("/admin/sign-out", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            if (context.User.FindFirst(ClaimTypes.Email)?.Value is { } email)
            {
                var now = context.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                await context.RequestServices.GetRequiredService<IAuditLog>().WriteAsync(new AuditEntry(now, email, "sign-out"), context.RequestAborted);
            }

            await context.SignOutAsync(EditorClaims.Scheme);
            return Results.Redirect("/");
        }).DisableAntiforgery();
    }

    /// <summary>
    /// Maps the editors page's posts (admins only, antiforgery-checked): add an editor or change
    /// their role, and remove one. Each redirects back with a status. Nobody can change their own
    /// entry or a configured admin, so nobody can lock the site out.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapEditorManagement(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/editors", async ([FromForm] string email, [FromForm] string role, HttpContext context, EditorChanges changes) =>
            Results.Redirect($"/admin/editors?status={await changes.SetAsync(context.User, email, role, context.RequestAborted)}"))
            .RequireAuthorization(EditorClaims.AdminPolicy);

        app.MapPost("/admin/editors/remove", async ([FromForm] string email, HttpContext context, EditorChanges changes) =>
            Results.Redirect($"/admin/editors?status={await changes.RemoveAsync(context.User, email, context.RequestAborted)}"))
            .RequireAuthorization(EditorClaims.AdminPolicy);
    }

    /// <summary>Keeps a return URL on this site: a local path, or <c>/admin</c>.</summary>
    /// <param name="returnUrl">The requested return URL.</param>
    /// <returns>A safe local path.</returns>
    internal static string LocalUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
            ? url
            : "/admin";
}
