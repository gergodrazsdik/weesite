using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Search;

namespace Weesite.Web.Admin;

/// <summary>
/// Deleting for good (T-0110): <c>POST /admin/delete</c> for a post or page and
/// <c>POST /admin/media/delete</c> for a media file; and <c>POST /admin/media/archive</c>, which
/// archives a file or brings it back (T-0111). Each needs an antiforgery token (a form field
/// binds, so it's checked) and the item's slug or the file's name typed as confirmation; the
/// confirmation pages (<c>DeleteItem</c>, <c>DeleteMedia</c>) post here. Audited.
/// </summary>
internal static class Deletion
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapDeletion(IEndpointRouteBuilder app)
    {
        // Rights like editing: admins any post or page, authors their own posts.
        app.MapPost("/admin/delete", async (
            [FromForm] string path,
            [FromForm] string etag,
            [FromForm] string? confirm,
            HttpContext context,
            ContentEditor editor,
            ContentCatalogProvider content,
            SiteSettingsEditor settings,
            IVectorStore vectors,
            IEmbedder embedder,
            IAuditLog audit,
            TimeProvider time) =>
        {
            var cancel = context.RequestAborted;
            var back = "/admin/delete?path=" + Uri.EscapeDataString(path);

            // Only an item the site knows: the request names it, the catalog gives the path that's
            // read and deleted, so nothing from the request reaches the store as a path.
            var catalog = content.Current;
            if (catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished).FirstOrDefault(i => i.SourcePath == path) is not { } item
                || await editor.OpenAsync(item.SourcePath, cancel) is not { } opened)
            {
                return Results.Redirect(back + "&error=changed");
            }

            if (!EditorRights.CanEdit(context.User, opened.Draft.Kind, opened.Draft.AuthorId, isNew: false))
            {
                return Results.Redirect("/admin/forbidden");
            }

            if (!string.Equals(confirm?.Trim(), opened.Draft.Slug, StringComparison.Ordinal))
            {
                return Results.Redirect(back + "&error=confirm");
            }

            if (!await editor.DeleteAsync(item.SourcePath, etag, cancel))
            {
                return Results.Redirect(back + "&error=changed");
            }

            // Its stored search vectors would only wait for a re-index to be dropped.
            if (embedder.Dimensions > 0)
            {
                await vectors.DeleteAsync(embedder.ModelId, VectorKeys.Item(item), cancel);
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(context), "content-deleted", item.SourcePath), cancel);

            // A deleted page leaves the menu too (F-0076); the header skips it either way.
            if (opened.Draft.Kind == ContentKind.Page)
            {
                await RemoveFromMenuAsync(settings, opened.Draft.Slug, Actor(context), audit, time, cancel);
            }

            var list = opened.Draft.Kind == ContentKind.Page ? "/admin/pages" : "/admin/posts";
            return Results.Redirect(list + "?deleted=" + Uri.EscapeDataString(opened.Draft.Title));
        })
        .RequireAuthorization(EditorClaims.EditorPolicy);

        // Admins only: a file can be used by anyone's posts.
        app.MapPost("/admin/media/delete", async (
            [FromForm] string name,
            [FromForm] string? confirm,
            HttpContext context,
            MediaLibrary library,
            IAuditLog audit,
            TimeProvider time) =>
        {
            var cancel = context.RequestAborted;
            if (!MediaEndpoint.IsValidName(name))
            {
                return Results.Redirect("/admin/media");
            }

            var back = "/admin/media/delete?name=" + Uri.EscapeDataString(name);
            if (!string.Equals(confirm?.Trim(), name, StringComparison.Ordinal))
            {
                return Results.Redirect(back + "&error=confirm");
            }

            var deleted = await library.DeleteAsync(name, cancel);
            if (deleted == 0)
            {
                return Results.Redirect(back + "&error=changed");
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(context), "media-deleted", $"{name} ({deleted.ToString(CultureInfo.InvariantCulture)} files)"), cancel);
            return Results.Redirect("/admin/media?deleted=" + Uri.EscapeDataString(name));
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);

        // Archiving a file (T-0111) or bringing it back, admins only. Archiving asks first on its
        // own page (ArchiveMedia) when posts or pages still use the file; unarchiving doesn't ask.
        app.MapPost("/admin/media/archive", async (
            [FromForm] string name,
            [FromForm] bool archived,
            HttpContext context,
            MediaLibrary library,
            IAuditLog audit,
            TimeProvider time) =>
        {
            var cancel = context.RequestAborted;
            if (!MediaEndpoint.IsValidName(name) || !(await library.ListAsync(cancel)).Any(f => f.Name == name))
            {
                return Results.Redirect("/admin/media");
            }

            if (!await library.SetArchivedAsync(name, archived, cancel))
            {
                return Results.Redirect("/admin/media?error=" + Uri.EscapeDataString("Someone else changed the archived files at the same moment; try again."));
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(context), archived ? "media-archived" : "media-unarchived", name), cancel);
            return Results.Redirect($"/admin/media?{(archived ? "archived" : "unarchived")}=" + Uri.EscapeDataString(name));
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);
    }

    // A settings change between reading and saving is tried again once; after that the header
    // still leaves the item out, and the menu page shows it as not published.
    private static async Task RemoveFromMenuAsync(SiteSettingsEditor settings, string page, string actor, IAuditLog audit, TimeProvider time, CancellationToken cancel)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var opened = await settings.OpenAsync(cancel);
            if (MenuForm.WithoutPage(opened.Settings.Menu, page) is not { } change)
            {
                return;
            }

            if (await settings.SaveAsync(opened.ETag, change, cancel))
            {
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "menu-changed", $"site.json (removed /{page})"), cancel);
                return;
            }
        }
    }

    private static string Actor(HttpContext context) => context.User.FindFirst(ClaimTypes.Email)?.Value ?? "(unknown)";
}
