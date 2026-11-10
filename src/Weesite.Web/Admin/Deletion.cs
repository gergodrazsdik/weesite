using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Search;

namespace Weesite.Web.Admin;

/// <summary>
/// Deleting for good (T-0110): <c>POST /admin/delete</c> for a post or page and
/// <c>POST /admin/media/delete</c> for a media file. Each needs an antiforgery token (a form field
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
            IVectorStore vectors,
            IEmbedder embedder,
            IAuditLog audit,
            TimeProvider time) =>
        {
            var cancel = context.RequestAborted;
            var back = "/admin/delete?path=" + Uri.EscapeDataString(path);
            if (await editor.OpenAsync(path, cancel) is not { } opened)
            {
                return Results.Redirect(back + "&error=changed");
            }

            if (!EditorRights.CanEdit(context.User, opened.Draft.Kind, opened.Draft.AuthorId, isNew: false))
            {
                return Results.Redirect("/admin/denied");
            }

            if (!string.Equals(confirm?.Trim(), opened.Draft.Slug, StringComparison.Ordinal))
            {
                return Results.Redirect(back + "&error=confirm");
            }

            var catalog = content.Current;
            var item = catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished).FirstOrDefault(i => i.SourcePath == path);
            if (!await editor.DeleteAsync(path, etag, cancel))
            {
                return Results.Redirect(back + "&error=changed");
            }

            // Its stored search vectors would only wait for a re-index to be dropped.
            if (item is not null && embedder.Dimensions > 0)
            {
                await vectors.DeleteAsync(embedder.ModelId, VectorKeys.Item(item), cancel);
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(context), "content-deleted", path), cancel);
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
    }

    private static string Actor(HttpContext context) => context.User.FindFirst(ClaimTypes.Email)?.Value ?? "(unknown)";
}
