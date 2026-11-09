using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Weesite.Content;
using Weesite.Content.Sources;

namespace Weesite.Web.Admin;

/// <summary>
/// "Export everything" (brief, Backups and export; T-0051): one zip of the Markdown, the site
/// settings and the media, laid out like the repo's <c>seed/</c> directory (<c>content/</c>,
/// <c>settings/</c>, <c>media/</c>) with a <c>weesite-backup.json</c> that says what it is. The
/// same zip imports into another Weesite, so content is never locked in. Editors, the audit log
/// and page views stay behind: they belong to the deployment, not the content.
/// </summary>
internal static class SiteBackup
{
    /// <summary>The file that marks a Weesite backup.</summary>
    public const string ManifestName = "weesite-backup.json";

    /// <summary>The backup format's version.</summary>
    public const int Format = 1;

    private static readonly string[] SettingsFiles = [ContentContainers.SiteSettingsFile, ContentContainers.AuthorsFile];

    /// <summary>Writes the backup zip to a stream, without buffering it.</summary>
    /// <param name="output">Where to write.</param>
    /// <param name="source">The content and settings.</param>
    /// <param name="media">The media files.</param>
    /// <param name="siteName">The site's name, for the manifest.</param>
    /// <param name="exportedAt">When.</param>
    /// <param name="cancellationToken">Stops the export.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(Stream output, IContentSource source, IMediaStore media, string siteName, DateTimeOffset exportedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(media);
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, cancellationToken);

        var manifest = JsonSerializer.Serialize(new { format = Format, site = siteName, exportedAt = exportedAt.ToString("O", CultureInfo.InvariantCulture) });
        await WriteTextAsync(zip, ManifestName, manifest, cancellationToken);

        foreach (var file in (await source.ReadContentAsync(cancellationToken)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            await WriteTextAsync(zip, $"{ContentContainers.Content}/{file.Path}", file.Text, cancellationToken);
        }

        foreach (var name in SettingsFiles)
        {
            if (await source.ReadSettingsFileAsync(name, cancellationToken) is { } text)
            {
                await WriteTextAsync(zip, $"{ContentContainers.Settings}/{name}", text, cancellationToken);
            }
        }

        // Media is already compressed (images, PDFs): stored as is, which is also faster.
        foreach (var name in (await media.ListAsync(cancellationToken)).Order(StringComparer.Ordinal))
        {
            if (await media.OpenAsync(name, cancellationToken) is not { } file)
            {
                continue;
            }

            await using (file.Content)
            {
                var entry = zip.CreateEntry($"media/{name}", CompressionLevel.NoCompression);
                entry.LastWriteTime = file.LastModified;
                await using var stream = await entry.OpenAsync(cancellationToken);
                await file.Content.CopyToAsync(stream, cancellationToken);
            }
        }
    }

    /// <summary>Maps <c>GET /admin/backup/file</c>: the backup as a download, for admins.</summary>
    /// <param name="app">The app.</param>
    public static void MapBackup(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/backup/file", async (HttpContext context, IContentSource source, IMediaStore media, ContentCatalogProvider content, IAuditLog audit, TimeProvider time) =>
        {
            var now = time.GetUtcNow();
            var actor = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "(unknown)";
            await audit.WriteAsync(new AuditEntry(now, actor, "backup-downloaded"), context.RequestAborted);

            var name = string.Create(CultureInfo.InvariantCulture, $"weesite-backup-{now:yyyy-MM-dd}.zip");
            context.Response.ContentType = "application/zip";
            context.Response.Headers.ContentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
            await WriteAsync(context.Response.Body, source, media, content.Current.Site.Name, now, context.RequestAborted);
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(cancellationToken);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }
}
