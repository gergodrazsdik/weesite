using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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
    /// <summary>
    /// Marks <c>/admin</c> responses as private and not for search engines, and answers 403 to a
    /// request the browser makes for something a page embeds, such as an image (T-0177): only pages,
    /// the admin's own scripts and stylesheets, its scripts' requests and downloads load from
    /// <c>/admin</c>, so text on a page can't make an editor's browser call an admin address.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void UseAdminHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                context.Response.Headers[HeaderNames.CacheControl] = "no-store";
                if (!IsAllowedDestination(context.Request.Headers["Sec-Fetch-Dest"]))
                {
                    context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>()?.Enabled = false;
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }

            await next(context);
        });

    /// <summary>
    /// Gets whether a request's <c>Sec-Fetch-Dest</c> may load from <c>/admin</c>: a page
    /// (<c>document</c>), a script's request or a download (<c>empty</c>), the admin's scripts and
    /// stylesheets, or none given (a browser without Fetch Metadata, or not a browser).
    /// </summary>
    /// <param name="destination">The header's value.</param>
    /// <returns>True when the request may go on.</returns>
    internal static bool IsAllowedDestination(string? destination) =>
        destination is null or "" or "document" or "empty" or "script" or "style";

    /// <summary>
    /// Maps <c>POST /admin/sign-in</c>, <c>POST /admin/sign-out</c> and
    /// <c>POST /admin/sign-out-everywhere</c> (antiforgery-checked forms).
    /// </summary>
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

        // Your profile: ends every sign-in of this editor, in every browser, this one included.
        // Other replicas notice within SignIn.RoleRecheck. Connected apps stay; they're revoked apart.
        app.MapPost("/admin/sign-out-everywhere", async (HttpContext context, IAntiforgery antiforgery, IEditorStore store, IAuditLog audit, IMemoryCache cache, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            var email = EditorClaims.Email(context.User);
            var now = time.GetUtcNow();
            await store.EndSessionsAsync(email, now, context.RequestAborted);
            await audit.WriteAsync(new AuditEntry(now, email, "sign-out-everywhere"), CancellationToken.None);
            cache.Remove(SignIn.RoleCacheKey(email));
            await context.SignOutAsync(EditorClaims.Scheme);
            return Results.Redirect("/");
        }).RequireAuthorization(EditorClaims.EditorPolicy).DisableAntiforgery();
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
                : Results.Redirect("/admin/media?error=" + AdminNotices.Seal(context, reason));

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
            var outcome = await library.UploadAsync(file.FileName, buffer.ToArray(), context.RequestAborted, alt, replaceDescription: context.User.IsInRole(nameof(EditorRole.Admin)));
            if (outcome is not MediaUploaded uploaded)
            {
                return Refuse(((MediaRejected)outcome).Reason);
            }

            var actor = EditorClaims.Email(context.User);
            // Stored: the audit entry isn't stopped with the request (F-0248).
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "media-uploaded", uploaded.Item.Name), CancellationToken.None);
            return json
                ? Results.Json(MediaLinks.Describe(uploaded.Item, content.Current.Media, alt))
                : Results.Redirect($"/admin/media?uploaded={Uri.EscapeDataString(uploaded.Item.Name)}");
        })
        .RequireAuthorization(EditorClaims.EditorPolicy)
        .WithMetadata(new RequestSizeLimitAttribute(MediaLibrary.MaxBytes + (1024 * 1024)))
        .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MediaLibrary.MaxBytes + (1024 * 1024) });

        // Replacing a file (F-0143), admins only, as it changes anyone's posts: from ReplaceMedia.
        app.MapPost("/admin/media/replace", async (IFormFile? file, [FromForm] string? name, [FromForm] string? alt, [FromForm] string? back, HttpContext context, MediaReplacer replacer, MediaCount count, IAuditLog audit, TimeProvider time) =>
        {
            var list = Deletion.MediaList(back);
            // An error is free text, so it's sealed (T-0177); the replaced file's name is checked by the page.
            string Notice(string key, string value) => $"{list}{(list.Contains('?', StringComparison.Ordinal) ? '&' : '?')}{key}={(key == "error" ? AdminNotices.Seal(context, value) : Uri.EscapeDataString(value))}";
            if (name is null || !MediaIndex.IsMediaName(name))
            {
                return Results.Redirect(list);
            }

            if (file is null || file.Length == 0 || file.Length > MediaLibrary.MaxBytes)
            {
                return Results.Redirect(Notice("error", "Choose a file of up to 20 MB to replace it with."));
            }

            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, context.RequestAborted);
            switch (await replacer.ReplaceAsync(name, file.FileName, buffer.ToArray(), alt, context.RequestAborted))
            {
                case ReplaceOutcome.Refused refused:
                    return Results.Redirect(Notice("error", refused.Reason));
                case ReplaceOutcome.Replaced replaced:
                    count.Forget();
                    await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(context.User), "media-replaced", $"{name} → {replaced.Item.Name} ({replaced.Updated.Count} items)"), CancellationToken.None);
                    return Results.Redirect(replaced.Failed.Count == 0
                        ? Notice("replaced", $"{replaced.Item.Name}|{replaced.Updated.Count}")
                        : Notice("error", $"Stored as {replaced.Item.Name}, but these were saved by someone else meanwhile and still use {name}, which stays: {string.Join(", ", replaced.Failed)}. Replace it again."));
                default:
                    return Results.Redirect(list);
            }
        })
        .RequireAuthorization(EditorClaims.AdminPolicy)
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
        app.MapPost("/admin/preview", async ([FromForm] string? markdown, HttpContext context, Weesite.Content.Markdown.MarkdownRenderer renderer, Weesite.Content.ContentCatalogProvider content, RenderGate gate) =>
        {
            // Text over the limit isn't rendered: it couldn't be saved, and it could take minutes (T-0122).
            if ((markdown?.Length ?? 0) > ContentDraft.MaxBodyLength)
            {
                return Results.Text($"Too long to preview: the text can be at most {ContentDraft.MaxBodyLength:N0} characters.", statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            // One at a time per editor (T-0177); the preview says so and catches up at the next pause.
            using var turn = await gate.EnterAsync(context.User, RenderGate.PreviewWait, context.RequestAborted);
            if (turn is null)
            {
                return Results.Text("Another preview of yours is still being made.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            // The words as the site counts them, for the editor's reading time (F-0181).
            var rendered = renderer.Render(markdown ?? "", content.Current.Media, sourceLines: true);
            context.Response.Headers[PreviewWordsHeader] = rendered.Words.ToString(CultureInfo.InvariantCulture);
            return Results.Content(rendered.Html, "text/html; charset=utf-8");
        })
            .RequireAuthorization(EditorClaims.EditorPolicy);

    /// <summary>The preview's response header with the text's word count.</summary>
    internal const string PreviewWordsHeader = "Weesite-Words";

    /// <summary>
    /// Maps <c>GET /admin/edit/state?path=…</c>: the stored version of an item the editor may change,
    /// so the edit page can tell when someone else saved it meanwhile, in another tab, in /admin or
    /// through an app (T-0141). 404 once it's deleted, and for an item the editor may not change (T-0177); never cached.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapEditorState(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/edit/state", async (string? path, HttpContext context, ContentEditor editor) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            // A missing item and one the editor may not change get the same answer (T-0177).
            var opened = string.IsNullOrEmpty(path) ? null : await editor.OpenAsync(path, context.RequestAborted);
            return opened is not null && EditorRights.CanEdit(context.User, opened.Draft.Kind, opened.Draft.AuthorId, isNew: false)
                ? Results.Json(new { etag = opened.ETag })
                : Results.NotFound();
        })
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
    // Control characters too: browsers drop a tab or a line break from an address, so "/\t/host"
    // would become "//host", another site (T-0122).
    internal static string LocalUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
            && !url.Any(char.IsControl)
            ? url
            : "/admin";
}
