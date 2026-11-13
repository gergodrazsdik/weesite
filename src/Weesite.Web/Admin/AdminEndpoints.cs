using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Weesite.Content;
using Weesite.Content.Media;

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
    /// Maps <c>POST /admin/media</c>: a file upload from the media page or <c>ws-media-picker</c>
    /// (editors only, antiforgery-checked, at most <see cref="MediaLibrary.MaxBytes"/>). Images need
    /// their description, for the Markdown to paste. Redirects back with the outcome, or answers in
    /// JSON when asked to. Also maps <c>GET /admin/media.json</c>, the stored files for the picker.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapMediaUpload(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/media", async (IFormFile? file, [FromForm] string? alt, HttpContext context, MediaLibrary library, ContentCatalogProvider content, IAuditLog audit, TimeProvider time) =>
        {
            // A browser form gets a redirect back to the media page; ws-media-picker asks for JSON.
            var json = context.Request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
            IResult Refuse(string reason) => json
                ? Results.Json(new { error = reason }, statusCode: StatusCodes.Status400BadRequest)
                : Results.Redirect("/admin/media?error=" + Uri.EscapeDataString(reason));

            if (file is null)
            {
                return Refuse("Choose a file to upload.");
            }

            if (file.Length == 0)
            {
                return Refuse("The file is empty.");
            }

            if (file.Length > MediaLibrary.MaxBytes)
            {
                return Refuse("The file is larger than 20 MB.");
            }

            if (ImageProcessor.IsProcessable(file.FileName) && string.IsNullOrWhiteSpace(alt))
            {
                return Refuse("Describe the image (alt text): what does it show?");
            }

            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, context.RequestAborted);
            var outcome = await library.UploadAsync(file.FileName, buffer.ToArray(), context.RequestAborted);
            if (outcome is not MediaUploaded uploaded)
            {
                return Refuse(((MediaRejected)outcome).Reason);
            }

            var actor = context.User.FindFirst(ClaimTypes.Email)?.Value ?? "(unknown)";
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "media-uploaded", uploaded.Item.Name), context.RequestAborted);
            return json
                ? Results.Json(MediaLinks.Describe(uploaded.Item, content.Current.Media, alt))
                : Results.Redirect($"/admin/media?uploaded={Uri.EscapeDataString(uploaded.Item.Name)}&alt={Uri.EscapeDataString(alt?.Trim() ?? "")}");
        })
        .RequireAuthorization(EditorClaims.EditorPolicy)
        .WithMetadata(new RequestSizeLimitAttribute(MediaLibrary.MaxBytes + (1024 * 1024)))
        .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MediaLibrary.MaxBytes + (1024 * 1024) });

        app.MapGet("/admin/media.json", async (HttpContext context, MediaLibrary library, ContentCatalogProvider content) =>
        {
            // Archived files stay served but aren't offered for new posts (T-0111).
            var items = await library.ListAsync(context.RequestAborted);
            return Results.Json(items.Where(i => !i.IsArchived).Select(i => MediaLinks.Describe(i, content.Current.Media, null)));
        })
        .RequireAuthorization(EditorClaims.EditorPolicy);
    }

    /// <summary>
    /// Maps <c>POST /admin/preview</c>: Markdown in, the HTML the public site would render out, for
    /// the editor's live preview (editors only, antiforgery-checked).
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapEditorPreview(this IEndpointRouteBuilder app) =>
        app.MapPost("/admin/preview", ([FromForm] string? markdown, Weesite.Content.Markdown.MarkdownRenderer renderer, Weesite.Content.ContentCatalogProvider content) =>
            Results.Content(renderer.Render(markdown ?? "", content.Current.Media).Html, "text/html; charset=utf-8"))
            .RequireAuthorization(EditorClaims.EditorPolicy);

    /// <summary>
    /// Maps the editors page's posts (admins only, antiforgery-checked): add an editor or change
    /// their role, and remove one. Each redirects back with a status. Nobody can change their own
    /// entry or a configured admin, so nobody can lock the site out.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapEditorManagement(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/editors", async ([FromForm] string email, [FromForm] string role, HttpContext context, EditorChanges changes, EditorInvitations invitations) =>
        {
            var change = await changes.SetAsync(context.User, email, role, context.RequestAborted);

            // The invitation (ADR 0010), once the editor is saved; without email, nothing is said about it.
            var invite = change is EditorOutcome.Saved saved ? await invitations.SendAsync(context.Request, saved.Editor, saved.RoleChanged, context.RequestAborted) : null;
            return Results.Redirect($"/admin/editors?status={change.Status}{(invite is null ? "" : $"&invite={invite.Status}")}");
        })
            .RequireAuthorization(EditorClaims.AdminPolicy);

        app.MapPost("/admin/editors/remove", async ([FromForm] string email, HttpContext context, EditorChanges changes) =>
            Results.Redirect($"/admin/editors?status={(await changes.RemoveAsync(context.User, email, context.RequestAborted)).Status}"))
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
