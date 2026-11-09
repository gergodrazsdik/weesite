using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Weesite.Content;
using Weesite.Content.Markdown;
using Weesite.Content.Sources;
using Weesite.Web.OpenGraph;
using Weesite.Web.Seo;

namespace Weesite.Web.Admin;

/// <summary>
/// The HTML export of a post or page (brief, HTML export; T-0050), as last saved: the body from
/// <see cref="HtmlExporter"/>, plus the title and the cover image, which most editors take in
/// fields of their own. The export page shows it; <c>/admin/export/file</c> downloads it.
/// </summary>
internal static class PostExport
{
    /// <summary>Finds a stored item the user may edit, published or not.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="path">The item's source path, such as <c>posts/hello.md</c>.</param>
    /// <param name="user">The signed-in editor.</param>
    /// <returns>The item, or null.</returns>
    public static ContentItem? Find(ContentCatalog catalog, string? path, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var item = path is null ? null : catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished).FirstOrDefault(i => i.SourcePath == path);
        return item is not null && EditorRights.CanEdit(user, item.Kind, item.AuthorId, isNew: false) ? item : null;
    }

    /// <summary>The item's public address, absolute (where it is, or will be once published).</summary>
    /// <param name="item">The item.</param>
    /// <param name="urls">The site's addresses.</param>
    /// <param name="request">The current request.</param>
    /// <returns>The address.</returns>
    public static Uri Canonical(ContentItem item, SiteUrls urls, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(urls);
        return new Uri(urls.Absolute(request, item.Kind == ContentKind.Post ? $"/blog/{item.Slug}" : $"/{item.Slug}"));
    }

    /// <summary>The cover image, absolute: the item's own image, or its generated card once it's public.</summary>
    /// <param name="item">The item.</param>
    /// <param name="catalog">The content.</param>
    /// <param name="images">The Open Graph images.</param>
    /// <param name="urls">The site's addresses.</param>
    /// <param name="request">The current request.</param>
    /// <returns>The address, or null when there's none yet.</returns>
    public static string? Cover(ContentItem item, ContentCatalog catalog, OpenGraphImages images, SiteUrls urls, HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(urls);
        if (item.Image is null && catalog.Unpublished.Contains(item))
        {
            return null;
        }

        return urls.Absolute(request, images.For(item).Path);
    }

    /// <summary>A standalone HTML file: the title as <c>h1</c>, then the body.</summary>
    /// <param name="item">The item.</param>
    /// <param name="export">Its export.</param>
    /// <param name="language">The site's language.</param>
    /// <returns>The document.</returns>
    public static string Document(ContentItem item, HtmlExport export, string language)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(export);
        var title = WebUtility.HtmlEncode(item.Title);
        return string.Create(CultureInfo.InvariantCulture, $"""
            <!doctype html>
            <html lang="{WebUtility.HtmlEncode(language)}">
            <head>
            <meta charset="utf-8">
            <title>{title}</title>
            </head>
            <body>
            <h1>{title}</h1>
            {export.Html}</body>
            </html>

            """);
    }

    /// <summary>Maps <c>GET /admin/export/file?path=…&amp;footer=1</c>: the export as a download.</summary>
    /// <param name="app">The app.</param>
    public static void MapExport(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/export/file", ([FromQuery] string? path, [FromQuery] string? footer, HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
        {
            var catalog = content.Current;
            if (Find(catalog, path, context.User) is not { } item)
            {
                return Results.NotFound();
            }

            var export = HtmlExporter.Export(item.Markdown, Canonical(item, urls, context.Request), footer == "1");
            var bytes = Encoding.UTF8.GetBytes(Document(item, export, catalog.Site.Language));
            return Results.File(bytes, "text/html; charset=utf-8", $"{item.Slug}.html");
        })
        .RequireAuthorization(EditorClaims.EditorPolicy);

    /// <summary>
    /// Maps <c>GET /admin/export/bundle?path=…&amp;footer=1</c> (T-0095): a zip with the HTML file
    /// and the post's own images in <c>images/</c>, the HTML pointing at them by relative path,
    /// for editors that take uploads rather than links. Streamed, so nothing is buffered.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapExportBundle(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/export/bundle", async ([FromQuery] string? path, [FromQuery] string? footer, HttpContext context, ContentCatalogProvider content, SiteUrls urls, IMediaStore media) =>
        {
            var catalog = content.Current;
            if (Find(catalog, path, context.User) is not { } item)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var cancel = context.RequestAborted;
            var stored = new HashSet<string>(await media.ListAsync(cancel), StringComparer.Ordinal);
            var export = HtmlExporter.Export(item.Markdown, Canonical(item, urls, context.Request), footer == "1", name => MediaEndpoint.IsValidName(name) && stored.Contains(name));

            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = $"{item.Slug}.zip" }.ToString();
            await using var zip = await ZipArchive.CreateAsync(context.Response.Body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, cancel);
            var html = zip.CreateEntry($"{item.Slug}.html", CompressionLevel.Optimal);
            await using (var stream = await html.OpenAsync(cancel))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(Document(item, export, catalog.Site.Language)), cancel);
            }

            foreach (var name in export.Images)
            {
                if (await media.OpenAsync(name, cancel) is not { } file)
                {
                    continue;
                }

                await using (file.Content)
                {
                    var entry = zip.CreateEntry($"images/{name}", CompressionLevel.NoCompression);
                    await using var stream = await entry.OpenAsync(cancel);
                    await file.Content.CopyToAsync(stream, cancel);
                }
            }
        })
        .RequireAuthorization(EditorClaims.EditorPolicy);
}
