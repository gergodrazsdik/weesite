using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Content.Sources;

namespace Weesite.Web.Admin;

/// <summary>Which part of the site an entry of a backup belongs to.</summary>
internal enum ImportArea
{
    /// <summary>A post or page (<c>content/</c>).</summary>
    Content,

    /// <summary>A settings file (<c>settings/</c>).</summary>
    Settings,

    /// <summary>A media file (<c>media/</c>).</summary>
    Media,
}

/// <summary>How an entry compares with the site.</summary>
internal enum ImportChange
{
    /// <summary>The site doesn't have it: it's added.</summary>
    New,

    /// <summary>The site has it with other content: kept unless the admin chooses to overwrite it.</summary>
    Changed,

    /// <summary>The site has it as it is: nothing to do.</summary>
    Same,

    /// <summary>It can't be imported; <see cref="ImportEntry.Reason"/> says why.</summary>
    Refused,
}

/// <summary>One file of a backup and what importing it would do.</summary>
/// <param name="Path">Its path in the zip, such as <c>content/posts/hello.md</c>.</param>
/// <param name="Area">Content, settings or media.</param>
/// <param name="Change">New, changed, the same, or refused.</param>
/// <param name="Label">What to call it: a post's title, or the file name.</param>
/// <param name="Reason">Why it's refused.</param>
internal sealed record ImportEntry(string Path, ImportArea Area, ImportChange Change, string Label, string? Reason = null);

/// <summary>A staged backup compared with the site.</summary>
/// <param name="Site">The site name in the backup's manifest.</param>
/// <param name="ExportedAt">When it was exported, as the manifest says.</param>
/// <param name="Entries">Every file, in the zip's order.</param>
internal sealed record ImportPlan(string Site, string ExportedAt, IReadOnlyList<ImportEntry> Entries)
{
    /// <summary>Counts the entries with a change.</summary>
    /// <param name="change">The change.</param>
    /// <returns>How many.</returns>
    public int Count(ImportChange change) => Entries.Count(e => e.Change == change);
}

/// <summary>What an import did.</summary>
/// <param name="Added">Files added.</param>
/// <param name="Overwritten">Files overwritten by choice.</param>
/// <param name="Kept">Files in both that were kept as they were.</param>
/// <param name="Conflicts">Files that changed on the site between the review and the apply, left alone.</param>
internal sealed record ImportResult(int Added, int Overwritten, int Kept, int Conflicts);

/// <summary>
/// Imports a backup zip from <see cref="SiteBackup"/> into this site (T-0051; maintainer,
/// 2026-11-10): new files are added; files the site already has with other content are listed, and
/// each is kept unless the admin ticks it to overwrite; nothing is deleted. Every file is checked
/// first: posts and pages must parse and keep their addresses unique, settings must be valid JSON,
/// media names must be ones the media endpoint serves, sizes are capped, and anything else in the
/// zip is refused.
/// </summary>
internal sealed partial class SiteImport(
    IImportStaging staging,
    IContentSource source,
    IContentStore store,
    IMediaStore media,
    ContentCatalogProvider content,
    ContentLoader loader,
    ContentReloader reloader)
{
    /// <summary>The largest zip accepted: 1 GiB.</summary>
    public const long MaxZipBytes = 1L << 30;

    private const long MaxTextBytes = 1024 * 1024;
    private static readonly string[] SettingsFiles = [ContentContainers.SiteSettingsFile, ContentContainers.AuthorsFile, ContentContainers.MediaSettingsFile];

    /// <summary>Stages an uploaded zip.</summary>
    /// <param name="zip">The upload.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The staged import's ID.</returns>
    public Task<string> StageAsync(Stream zip, CancellationToken cancellationToken) => staging.SaveAsync(zip, cancellationToken);

    /// <summary>Compares a staged zip with the site.</summary>
    /// <param name="id">The staged import.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The plan, or null when the import is gone or the zip isn't a Weesite backup.</returns>
    public async Task<ImportPlan?> AnalyzeAsync(string id, CancellationToken cancellationToken) =>
        (await ReadAsync(id, cancellationToken))?.Plan;

    /// <summary>Applies a staged import, then deletes it.</summary>
    /// <param name="id">The staged import.</param>
    /// <param name="overwrite">Paths (in the zip) the admin chose to overwrite.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What it did, or null when the import is gone.</returns>
    public async Task<ImportResult?> ApplyAsync(string id, IReadOnlySet<string> overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overwrite);
        if (await ReadAsync(id, cancellationToken) is not { } read)
        {
            return null;
        }

        int added = 0, overwritten = 0, kept = 0, conflicts = 0;
        foreach (var entry in read.Plan.Entries)
        {
            var replace = entry.Change == ImportChange.Changed && overwrite.Contains(entry.Path);
            if (entry.Change == ImportChange.Changed && !replace)
            {
                kept++;
            }

            if (entry.Change is not ImportChange.New && !replace)
            {
                continue;
            }

            var data = read.Data[entry.Path];
            var ok = entry.Area switch
            {
                ImportArea.Media => await WriteMediaAsync(entry.Path, data, cancellationToken),
                ImportArea.Settings => await WriteSettingsAsync(entry.Path, data, replace, cancellationToken),
                _ => await WriteContentAsync(entry.Path, data, replace, cancellationToken),
            };
            if (!ok)
            {
                conflicts++;
            }
            else if (replace)
            {
                overwritten++;
            }
            else
            {
                added++;
            }
        }

        if (added + overwritten > 0)
        {
            var version = await store.BumpVersionAsync(cancellationToken);
            await loader.LoadAsync(cancellationToken);
            reloader.Acknowledge(version);
        }

        await staging.DeleteAsync(id, cancellationToken);
        return new ImportResult(added, overwritten, kept, conflicts);
    }

    /// <summary>Drops a staged import without applying it.</summary>
    /// <param name="id">The staged import.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>A task.</returns>
    public Task CancelAsync(string id, CancellationToken cancellationToken) => staging.DeleteAsync(id, cancellationToken);

    /// <summary>
    /// Maps <c>POST /admin/backup/import</c> (the zip, streamed into staging, then to the review)
    /// and <c>POST /admin/backup/import/apply</c> (apply or cancel), for admins.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void MapImport(IEndpointRouteBuilder app)
    {
        // The form's first field is the antiforgery token, then the file. The body is read as a
        // stream, never buffered: the token goes into the request's antiforgery header and is
        // checked before the file is read, then the file streams into staging.
        app.MapPost("/admin/backup/import", async (HttpContext context, IAntiforgery antiforgery, SiteImport import) =>
        {
            static IResult Back(string error) => Results.Redirect("/admin/backup?importError=" + Uri.EscapeDataString(error));
            var cancel = context.RequestAborted;
            if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type) || type.Boundary.Length == 0)
            {
                return Results.BadRequest();
            }

            var reader = new MultipartReader(HeaderUtilities.RemoveQuotes(type.Boundary).Value!, context.Request.Body) { BodyLengthLimit = MaxZipBytes };
            var section = await reader.ReadNextSectionAsync(cancel);
            if (section?.AsFormDataSection() is not { Name: "__RequestVerificationToken" } token)
            {
                return Results.BadRequest();
            }

            context.Request.Headers["RequestVerificationToken"] = await token.GetValueAsync(cancel);
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            section = await reader.ReadNextSectionAsync(cancel);
            if (section?.AsFileSection() is not { Name: "file" } file || file.FileStream is null)
            {
                return Back("Choose a backup zip to import.");
            }

            try
            {
                await using var limited = new LimitedStream(file.FileStream, MaxZipBytes);
                var id = await import.StageAsync(limited, cancel);
                return Results.Redirect($"/admin/backup?import={id}");
            }
            catch (InvalidDataException)
            {
                return Back("The zip is larger than 1 GB.");
            }
        })
        .RequireAuthorization(EditorClaims.AdminPolicy)
        .DisableAntiforgery()
        .WithMetadata(new RequestSizeLimitAttribute(MaxZipBytes + (1024 * 1024)));

        app.MapPost("/admin/backup/import/apply", async ([FromForm] string id, [FromForm] string? action, [FromForm(Name = "overwrite")] string[]? overwrite, HttpContext context, SiteImport import, IAuditLog audit, TimeProvider time) =>
        {
            var cancel = context.RequestAborted;
            var actor = context.User.FindFirst(ClaimTypes.Email)?.Value ?? "(unknown)";
            if (action == "cancel")
            {
                await import.CancelAsync(id, cancel);
                return Results.Redirect("/admin/backup?importCancelled=1");
            }

            if (await import.ApplyAsync(id, new HashSet<string>(overwrite ?? [], StringComparer.Ordinal), cancel) is not { } result)
            {
                return Results.Redirect("/admin/backup?importError=" + Uri.EscapeDataString("That import is gone: upload the zip again."));
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "backup-imported", $"{result.Added} added, {result.Overwritten} overwritten, {result.Kept} kept"), cancel);
            return Results.Redirect($"/admin/backup?added={result.Added}&overwritten={result.Overwritten}&kept={result.Kept}&conflicts={result.Conflicts}");
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);
    }

    // Reads and checks the whole zip; keeps the bytes of every file that may be written.
    private async Task<(ImportPlan Plan, Dictionary<string, byte[]> Data)?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        await using var stream = await staging.OpenAsync(id, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        ZipArchive zip;
        try
        {
            zip = await ZipArchive.CreateAsync(stream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: Encoding.UTF8, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        await using (zip)
        {
            if (zip.GetEntry(SiteBackup.ManifestName) is not { } manifestEntry
                || await ReadTextAsync(manifestEntry, cancellationToken) is not { } manifestText
                || Manifest(manifestText) is not { } manifest)
            {
                return null;
            }

            var current = (await source.ReadContentAsync(cancellationToken)).ToDictionary(f => f.Path, f => f.Text, StringComparer.Ordinal);
            var mediaNames = new HashSet<string>(await media.ListAsync(cancellationToken), StringComparer.Ordinal);
            var catalog = content.Current;
            var addresses = catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished)
                .ToDictionary(i => (i.Kind, i.Slug), i => i.SourcePath);
            var entries = new List<ImportEntry>();
            var data = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/', StringComparison.Ordinal) && e.FullName != SiteBackup.ManifestName))
            {
                var path = entry.FullName;
                ImportEntry Refuse(ImportArea area, string reason) => new(path, area, ImportChange.Refused, path, reason);

                if (path.StartsWith("media/", StringComparison.Ordinal))
                {
                    var name = path["media/".Length..];
                    if (!MediaEndpoint.IsValidName(name))
                    {
                        entries.Add(Refuse(ImportArea.Media, "Not a media file name this site serves."));
                    }
                    else if (entry.Length > MediaLibrary.MaxBytes)
                    {
                        entries.Add(Refuse(ImportArea.Media, "Larger than 20 MB."));
                    }
                    else if (mediaNames.Contains(name))
                    {
                        entries.Add(new ImportEntry(path, ImportArea.Media, ImportChange.Same, name));
                    }
                    else
                    {
                        var bytes = await ReadBytesAsync(entry, MediaLibrary.MaxBytes, cancellationToken);
                        if (MediaLibrary.Problem(name, bytes) is { } problem)
                        {
                            entries.Add(Refuse(ImportArea.Media, problem));
                        }
                        else
                        {
                            data[path] = bytes;
                            entries.Add(new ImportEntry(path, ImportArea.Media, ImportChange.New, name));
                        }
                    }

                    continue;
                }

                if (path.StartsWith("settings/", StringComparison.Ordinal))
                {
                    var name = path["settings/".Length..];
                    if (!SettingsFiles.Contains(name))
                    {
                        entries.Add(Refuse(ImportArea.Settings, "Not a settings file this site uses."));
                        continue;
                    }

                    var text = entry.Length <= MaxTextBytes ? await ReadTextAsync(entry, cancellationToken) : null;
                    if (text is null || !IsJson(text))
                    {
                        entries.Add(Refuse(ImportArea.Settings, "Not valid JSON, or larger than 1 MB."));
                        continue;
                    }

                    var existing = await source.ReadSettingsFileAsync(name, cancellationToken);
                    data[path] = Encoding.UTF8.GetBytes(text);
                    entries.Add(new ImportEntry(path, ImportArea.Settings, existing is null ? ImportChange.New : existing == text ? ImportChange.Same : ImportChange.Changed, name));
                    continue;
                }

                var match = ContentPath().Match(path);
                if (!match.Success)
                {
                    entries.Add(Refuse(ImportArea.Content, "Not part of a Weesite backup."));
                    continue;
                }

                var relative = path["content/".Length..];
                var body = entry.Length <= MaxTextBytes ? await ReadTextAsync(entry, cancellationToken) : null;
                if (body is null)
                {
                    entries.Add(Refuse(ImportArea.Content, "Larger than 1 MB."));
                    continue;
                }

                var parsed = ContentParser.Parse(relative, body, catalog.Site.Language);
                if (parsed.Item is not { } item)
                {
                    entries.Add(Refuse(ImportArea.Content, parsed.Error ?? "It doesn't parse."));
                    continue;
                }

                if (addresses.TryGetValue((item.Kind, item.Slug), out var holder) && holder != relative)
                {
                    entries.Add(Refuse(ImportArea.Content, $"Its address is already used by {holder}."));
                    continue;
                }

                addresses[(item.Kind, item.Slug)] = relative;
                data[path] = Encoding.UTF8.GetBytes(body);
                var change = !current.TryGetValue(relative, out var now) ? ImportChange.New : now == body ? ImportChange.Same : ImportChange.Changed;
                entries.Add(new ImportEntry(path, ImportArea.Content, change, item.Title));
            }

            return (new ImportPlan(manifest.Site, manifest.ExportedAt, entries), data);
        }
    }

    private async Task<bool> WriteMediaAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        await media.WriteAsync(path["media/".Length..], data, cancellationToken);
        return true;
    }

    // Overwrites only the version that's there now; a file that appeared or changed meanwhile is a conflict.
    private async Task<bool> WriteSettingsAsync(string path, byte[] data, bool replace, CancellationToken cancellationToken)
    {
        var name = path["settings/".Length..];
        var etag = replace ? (await store.ReadSettingsFileAsync(name, cancellationToken))?.ETag : null;
        return await store.WriteSettingsFileAsync(name, Encoding.UTF8.GetString(data), etag, cancellationToken) is not null;
    }

    private async Task<bool> WriteContentAsync(string path, byte[] data, bool replace, CancellationToken cancellationToken)
    {
        var relative = path["content/".Length..];
        var etag = replace ? (await store.ReadAsync(relative, cancellationToken))?.ETag : null;
        return await store.WriteAsync(relative, Encoding.UTF8.GetString(data), etag, cancellationToken) is not null;
    }

    private static (string Site, string ExportedAt)? Manifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Number && format.GetInt32() == SiteBackup.Format
                ? (root.TryGetProperty("site", out var site) ? site.GetString() ?? "" : "", root.TryGetProperty("exportedAt", out var at) ? at.GetString() ?? "" : "")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > MaxTextBytes)
        {
            return null;
        }

        return Encoding.UTF8.GetString(await ReadBytesAsync(entry, MaxTextBytes, cancellationToken));
    }

    // Reads at most limit bytes, whatever the entry's header claims (a zip bomb lies about sizes).
    private static async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry, long limit, CancellationToken cancellationToken)
    {
        await using var stream = await entry.OpenAsync(cancellationToken);
        await using var limited = new LimitedStream(stream, limit);
        using var buffer = new MemoryStream();
        await limited.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    // Content files: posts/ or pages/, a slug-like name, .md.
    [GeneratedRegex("^content/(posts|pages)/[a-z0-9][a-z0-9-]{0,150}\\.md$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ContentPath();
}

/// <summary>Reads a stream and throws once more than a limit has been read.</summary>
/// <param name="inner">The stream.</param>
/// <param name="limit">The most bytes allowed.</param>
internal sealed class LimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int read)
    {
        _read += read;
        return _read > limit ? throw new InvalidDataException($"More than {limit} bytes.") : read;
    }
}
