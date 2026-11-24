using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Search;

namespace Weesite.Web.Admin;

/// <summary>
/// Deleting for good (T-0110): <c>POST /admin/delete</c> for a post or page and
/// <c>POST /admin/media/delete</c> for a media file; and <c>POST /admin/media/archive</c>, which
/// archives a file or brings it back (T-0111). Each needs an antiforgery token (a form field
/// binds, so it's checked) and the item's slug (a special page's file name) or the media file's
/// name typed as confirmation; the confirmation pages (<c>DeleteItem</c>, <c>DeleteMedia</c>)
/// post here. Audited.
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
            if (catalog.AllItems.FirstOrDefault(i => i.SourcePath == path) is not { } item
                || await editor.OpenAsync(item.SourcePath, cancel) is not { } opened)
            {
                return Results.Redirect(back + "&error=changed");
            }

            // An item the editor may not delete gets the same answer as a missing one (T-0177).
            if (!EditorRights.CanEdit(context.User, opened.Draft.Kind, opened.Draft.AuthorId, isNew: false))
            {
                return Results.Redirect(back + "&error=changed");
            }

            if (!string.Equals(confirm?.Trim(), ConfirmTextOf(item.SourcePath, opened.Draft.Slug), StringComparison.Ordinal))
            {
                return Results.Redirect(back + "&error=confirm");
            }

            if (!await editor.DeleteAsync(item.SourcePath, etag, cancel))
            {
                return Results.Redirect(back + "&error=changed");
            }

            // Deleted: what follows isn't stopped with the request, or a closed tab would leave the
            // vectors, the audit entry and the menu item behind (F-0248).
            cancel = CancellationToken.None;

            // Its stored search vectors would only wait for a re-index to be dropped.
            if (embedder.Dimensions > 0)
            {
                await vectors.DeleteAsync(embedder.ModelId, VectorKeys.Item(item), cancel);
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(context), "content-deleted", item.SourcePath), cancel);

            // A deleted page leaves the menu too (F-0076); the header skips it either way.
            if (opened.Draft.Kind == ContentKind.Page && opened.Draft.Slug.Length > 0)
            {
                await RemoveFromMenuAsync(settings, opened.Draft.Slug, Actor(context), audit, time, cancel);
            }

            var list = opened.Draft.Kind == ContentKind.Page ? "/admin/pages" : "/admin/posts";
            // A title is free text, so it's sealed for the page (T-0177).
            return Results.Redirect(list + "?deleted=" + AdminNotices.Seal(context, opened.Draft.Title));
        })
        .RequireAuthorization(EditorClaims.EditorPolicy);

        // Admins only: a file can be used by anyone's posts.
        app.MapPost("/admin/media/delete", async (
            [FromForm] string name,
            [FromForm] string? confirm,
            [FromForm] string? back,
            HttpContext context,
            MediaLibrary library,
            IAuditLog audit,
            TimeProvider time) =>
        {
            var cancel = context.RequestAborted;
            var list = MediaList(back);
            if (!MediaEndpoint.IsValidName(name))
            {
                return Results.Redirect(list);
            }

            var again = $"/admin/media/delete?name={Uri.EscapeDataString(name)}&back={Uri.EscapeDataString(list)}";
            if (!string.Equals(confirm?.Trim(), name, StringComparison.Ordinal))
            {
                return Results.Redirect(again + "&error=confirm");
            }

            var deleted = await library.DeleteAsync(name, cancel);
            if (deleted == 0)
            {
                return Results.Redirect(again + "&error=changed");
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(context), "media-deleted", $"{name} ({deleted.ToString(CultureInfo.InvariantCulture)} {(deleted == 1 ? "file" : "files")})"), CancellationToken.None);
            return Results.Redirect(WithNotice(list, "deleted", name));
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);

        // Archiving a file (T-0111) or bringing it back, admins only. Archiving asks first on its
        // own page (ArchiveMedia) when posts or pages still use the file; unarchiving doesn't ask.
        app.MapPost("/admin/media/archive", async (
            [FromForm] string name,
            [FromForm] bool archived,
            [FromForm] string? back,
            HttpContext context,
            MediaLibrary library,
            MediaCount count,
            IAuditLog audit,
            TimeProvider time) =>
        {
            var cancel = context.RequestAborted;
            var list = MediaList(back);
            if (!MediaEndpoint.IsValidName(name) || !(await library.ListAsync(cancel)).Any(f => f.Name == name))
            {
                return Results.Redirect(list);
            }

            if (!await library.SetArchivedAsync(name, archived, cancel))
            {
                return Results.Redirect(WithNotice(list, "error", AdminNotices.Seal(context, "Someone else changed the archived files at the same moment; try again."), escaped: true));
            }

            count.Forget();
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Actor(context), archived ? "media-archived" : "media-unarchived", name), CancellationToken.None);
            return Results.Redirect(WithNotice(list, archived ? "archived" : "unarchived", name));
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);
    }

    /// <summary>
    /// Gets what an editor types to confirm deleting a post or page: its slug, or for a special
    /// page, which has none (F-0217), its file name, such as <c>privacy.md</c>. The file name rather
    /// than the title: it's unique, short and plain to type, and Site settings choose special pages
    /// by it.
    /// </summary>
    /// <param name="path">The item's source path, such as <c>pages/privacy.md</c>.</param>
    /// <param name="slug">The item's slug, empty for a special page.</param>
    /// <returns>The text to type.</returns>
    public static string ConfirmTextOf(string path, string slug) =>
        slug.Length > 0 ? slug : Path.GetFileName(path);

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

            var result = await settings.SaveAsync(opened.ETag, change, cancel);
            if (result == SettingsSaveResult.Unchanged)
            {
                return;
            }

            if (result == SettingsSaveResult.Saved)
            {
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "menu-changed", $"site.json (removed /{page})"), cancel);
                return;
            }
        }
    }

    /// <summary>
    /// Gets the media list to go back to, with its filter and tab (F-0096): a <c>/admin/media</c>
    /// address from the request, or the plain list for anything else.
    /// </summary>
    /// <param name="back">The address the list had, if any.</param>
    /// <returns>A local media list address.</returns>
    public static string MediaList(string? back) =>
        back is not null && (back == "/admin/media" || back.StartsWith("/admin/media?", StringComparison.Ordinal))
            && !back.Contains("//", StringComparison.Ordinal) && !back.Contains('\\', StringComparison.Ordinal)
            ? back
            : "/admin/media";

    // The list's address with an outcome for its notice, in place of an earlier one.
    private static string WithNotice(string list, string key, string value, bool escaped = false)
    {
        var at = list.IndexOf('?', StringComparison.Ordinal);
        var kept = at < 0 ? [] : list[(at + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.StartsWith("q=", StringComparison.Ordinal) || p.StartsWith("show=", StringComparison.Ordinal) || p.StartsWith("page=", StringComparison.Ordinal));
        return "/admin/media?" + string.Join('&', [.. kept, $"{key}={(escaped ? value : Uri.EscapeDataString(value))}"]);
    }

    private static string Actor(HttpContext context) => EditorClaims.Email(context.User);
}
