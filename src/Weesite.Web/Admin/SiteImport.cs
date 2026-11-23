using System.IO.Compression;
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
/// <param name="Version">
/// For a changed file, a hash of the site's version: an overwrite chosen in the review names it, so
/// a file changed on the site after the review is left alone (F-0109).
/// </param>
internal sealed record ImportEntry(string Path, ImportArea Area, ImportChange Change, string Label, string? Reason = null, string? Version = null);

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
/// <param name="Skipped">Files the review couldn't import (F-0091 L23).</param>
internal sealed record ImportResult(int Added, int Overwritten, int Kept, int Conflicts, int Skipped = 0);

/// <summary>
/// What an import has done so far, counted as it goes (F-0248): when it stops partway, the audit
/// entry and the page say what it wrote before it stopped, as a reset's do (F-0221).
/// </summary>
internal sealed class ImportProgress
{
    /// <summary>Gets the files added so far.</summary>
    public int Added { get; internal set; }

    /// <summary>Gets the files overwritten so far.</summary>
    public int Overwritten { get; internal set; }

    /// <summary>Gets the files kept as they were so far.</summary>
    public int Kept { get; internal set; }

    /// <summary>Gets the files changed meanwhile and left alone so far.</summary>
    public int Conflicts { get; internal set; }

    /// <summary>The audit entry's target.</summary>
    /// <param name="skipped">The files the review couldn't import, when it got that far.</param>
    /// <param name="failed">Whether the import stopped partway.</param>
    /// <returns>Such as <c>3 added, 0 overwritten, 1 kept, 0 changed meanwhile; failed</c>.</returns>
    public string Summary(int? skipped, bool failed) =>
        $"{Added} added, {Overwritten} overwritten, {Kept} kept, {Conflicts} changed meanwhile"
        + (skipped is { } refused ? $", {refused} refused" : "")
        + (failed ? "; failed" : "");
}

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
    ContentReloader reloader)
{
    /// <summary>The largest zip accepted: 1 GiB.</summary>
    public const long MaxZipBytes = 1L << 30;

    /// <summary>The most files a zip may hold: a site's backup has a few thousand (T-0122).</summary>
    public const int MaxEntries = 20_000;

    /// <summary>The most a zip's files may add up to, uncompressed (T-0122).</summary>
    public const long MaxTotalBytes = 2L << 30;

    private const long MaxTextBytes = 1024 * 1024;

    // A post or page: as large as the editor's form takes (4 MB, ASP.NET Core's form value limit),
    // so whatever the editor saved, an export of it imports (F-0119).
    private const long MaxContentBytes = 4 * 1024 * 1024;

    /// <summary>Stages an uploaded zip.</summary>
    /// <param name="zip">The upload.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The staged import's ID.</returns>
    public Task<string> StageAsync(Stream zip, CancellationToken cancellationToken) => staging.SaveAsync(zip, cancellationToken);

    /// <summary>Compares a staged zip with the site.</summary>
    /// <param name="id">The staged import.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>The plan, or null when the import is gone or the zip isn't a Weesite backup.</returns>
    public Task<ImportPlan?> AnalyzeAsync(string id, CancellationToken cancellationToken) => ReadAsync(id, accept: null, cancellationToken);

    /// <summary>Applies a staged import, then deletes it.</summary>
    /// <param name="id">The staged import.</param>
    /// <param name="overwrite">The changed files the admin chose to overwrite, as the review named them: the path in the zip, <c>|</c>, the site's version.</param>
    /// <param name="progress">Counts what it does as it goes, for when it stops partway (F-0248).</param>
    /// <returns>What it did, or null when the import is gone.</returns>
    public async Task<ImportResult?> ApplyAsync(string id, IReadOnlySet<string> overwrite, ImportProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(overwrite);
        progress ??= new ImportProgress();
        var chosen = overwrite
            .Select(o => o.Split('|', 2))
            .Where(o => o.Length == 2)
            .GroupBy(o => o[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.Ordinal);

        // Not stopped with the request: once files are written, the version bump and the reload
        // must follow, or no replica shows them (F-0109).
        var cancellationToken = CancellationToken.None;
        ImportPlan? plan;
        try
        {
            // Each file is written as it's read and checked again, so the zip's files are never
            // all in memory at once (F-0109).
            plan = await ReadAsync(id, async (entry, data) =>
            {
                var version = entry.Change == ImportChange.Changed && chosen.TryGetValue(entry.Path, out var v) ? v : null;
                if (entry.Change == ImportChange.Changed && version is null)
                {
                    progress.Kept++;
                    return;
                }

                var ok = entry.Area switch
                {
                    ImportArea.Media => await WriteMediaAsync(entry.Path, data, cancellationToken),
                    ImportArea.Settings => await WriteSettingsAsync(entry.Path, data, version, cancellationToken),
                    _ => await WriteContentAsync(entry.Path, data, version, cancellationToken),
                };
                if (!ok)
                {
                    progress.Conflicts++;
                }
                else if (version is not null)
                {
                    progress.Overwritten++;
                }
                else
                {
                    progress.Added++;
                }
            }, cancellationToken);
        }
        finally
        {
            if (progress.Added + progress.Overwritten > 0)
            {
                await reloader.ReloadAfterWriteAsync(store);
            }
        }

        if (plan is null)
        {
            return null;
        }

        await staging.DeleteAsync(id, cancellationToken);
        // The files the review couldn't import are said in the result too (F-0091 L23).
        var skipped = plan.Entries.Count(e => e.Change == ImportChange.Refused);
        return new ImportResult(progress.Added, progress.Overwritten, progress.Kept, progress.Conflicts, skipped);
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

        app.MapPost("/admin/backup/import/apply", async ([FromForm] string id, [FromForm] string? action, [FromForm(Name = "overwrite")] string[]? overwrite, HttpContext context, SiteImport import, IAuditLog audit, TimeProvider time, ILogger<SiteImport> logger) =>
        {
            var cancel = context.RequestAborted;
            var actor = EditorClaims.Email(context.User);
            if (action == "cancel")
            {
                await import.CancelAsync(id, cancel);
                return Results.Redirect("/admin/backup?importCancelled=1");
            }

            // Counted as it goes: an import that stops partway (a storage error, an image that
            // won't decode) is audited with what it wrote, and the page says so (F-0248).
            var progress = new ImportProgress();
            ImportResult? result = null;
            var failed = true;
            try
            {
                result = await import.ApplyAsync(id, new HashSet<string>(overwrite ?? [], StringComparer.Ordinal), progress);
                failed = false;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogImportFailed(logger, e);
            }
            finally
            {
                if (failed || result is not null)
                {
                    await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "backup-imported", progress.Summary(result?.Skipped, failed)), CancellationToken.None);
                }
            }

            if (failed)
            {
                // The staged zip stays: the review compares it with the site as it is now, so applying
                // it again imports the rest.
                return Results.Redirect($"/admin/backup?import={Uri.EscapeDataString(id)}&stopped=1&added={progress.Added}&overwritten={progress.Overwritten}");
            }

            if (result is null)
            {
                return Results.Redirect("/admin/backup?importError=" + Uri.EscapeDataString("That import is gone: upload the zip again."));
            }

            return Results.Redirect($"/admin/backup?added={result.Added}&overwritten={result.Overwritten}&kept={result.Kept}&conflicts={result.Conflicts}&skipped={result.Skipped}");
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The import stopped partway")]
    private static partial void LogImportFailed(ILogger logger, Exception exception);

    // Reads and checks the whole zip. Each file that would be added or overwritten goes to accept
    // with its bytes as soon as it's checked, and isn't kept (F-0109).
    private async Task<ImportPlan?> ReadAsync(string id, Func<ImportEntry, byte[], Task>? accept, CancellationToken cancellationToken)
    {
        await using var stream = await staging.OpenAsync(id, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        // The number of files, from the zip's end record, before its directory is read into memory:
        // a small zip can list millions (T-0122). Zip64 means more than 65,535 or over 4 GB.
        if (await EntryCountAsync(stream, cancellationToken) is not (>= 0 and <= MaxEntries))
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
            string? manifestText;
            try
            {
                manifestText = zip.GetEntry(SiteBackup.ManifestName) is { } manifestEntry ? await ReadTextAsync(manifestEntry, cancellationToken) : null;
            }
            catch (InvalidDataException)
            {
                return null;
            }

            if (manifestText is null || Manifest(manifestText) is not { } manifest)
            {
                return null;
            }

            var current = (await source.ReadContentAsync(cancellationToken)).ToDictionary(f => f.Path, f => f.Text, StringComparer.Ordinal);
            var mediaNames = new HashSet<string>(await media.ListAsync(cancellationToken), StringComparer.Ordinal);
            var catalog = content.Current;
            var addresses = catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished)
                .ToDictionary(i => (i.Kind, i.Slug), i => i.SourcePath);
            var entries = new List<ImportEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (zip.Entries.Sum(e => e.Length) > MaxTotalBytes)
            {
                return null;
            }

            // A backup's generated image copies are left out: the site makes them again from each
            // original it imports (T-0122).
            foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/', StringComparison.Ordinal) && e.FullName != SiteBackup.ManifestName
                && !(e.FullName.StartsWith("media/", StringComparison.Ordinal) && MediaIndex.IsVariant(e.FullName["media/".Length..]))))
            {
                var path = entry.FullName;
                var area = path.StartsWith("media/", StringComparison.Ordinal) ? ImportArea.Media
                    : path.StartsWith("settings/", StringComparison.Ordinal) ? ImportArea.Settings
                    : ImportArea.Content;
                if (!seen.Add(path))
                {
                    entries.Add(new ImportEntry(path, area, ImportChange.Refused, path, "It's in the zip more than once."));
                    continue;
                }

                (ImportEntry Entry, byte[]? Data) checkedEntry;
                try
                {
                    checkedEntry = area switch
                    {
                        ImportArea.Media => await CheckMediaAsync(entry, mediaNames, cancellationToken),
                        ImportArea.Settings => await CheckSettingsAsync(entry, cancellationToken),
                        _ => await CheckContentAsync(entry, current, addresses, catalog.Site.Language, cancellationToken),
                    };
                }
                catch (InvalidDataException)
                {
                    // A damaged entry, or one larger than its header says (F-0109).
                    checkedEntry = (new ImportEntry(path, area, ImportChange.Refused, path, "It's damaged, or larger than the zip says."), null);
                }

                entries.Add(checkedEntry.Entry);
                if (accept is not null && checkedEntry.Data is { } data && checkedEntry.Entry.Change is ImportChange.New or ImportChange.Changed)
                {
                    await accept(checkedEntry.Entry, data);
                }
            }

            return new ImportPlan(manifest.Site, manifest.ExportedAt, entries);
        }
    }

    private static ImportEntry Refuse(string path, ImportArea area, string reason) => new(path, area, ImportChange.Refused, path, reason);

    private static async Task<(ImportEntry, byte[]?)> CheckMediaAsync(ZipArchiveEntry entry, HashSet<string> stored, CancellationToken cancellationToken)
    {
        var path = entry.FullName;
        var name = path["media/".Length..];
        if (!MediaEndpoint.IsValidName(name))
        {
            return (Refuse(path, ImportArea.Media, "Not a media file name this site serves."), null);
        }

        if (entry.Length > MediaLibrary.MaxBytes)
        {
            return (Refuse(path, ImportArea.Media, "Larger than 20 MB."), null);
        }

        if (stored.Contains(name))
        {
            return (new ImportEntry(path, ImportArea.Media, ImportChange.Same, name), null);
        }

        var bytes = await ReadBytesAsync(entry, MediaLibrary.MaxBytes, cancellationToken);
        return MediaLibrary.Problem(name, bytes) is { } problem
            ? (Refuse(path, ImportArea.Media, problem), null)
            : (new ImportEntry(path, ImportArea.Media, ImportChange.New, name), bytes);
    }

    private async Task<(ImportEntry, byte[]?)> CheckSettingsAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        var path = entry.FullName;
        var name = path["settings/".Length..];
        if (!ContentContainers.SettingsFiles.Contains(name))
        {
            return (Refuse(path, ImportArea.Settings, "Not a settings file this site uses."), null);
        }

        var text = entry.Length <= MaxTextBytes ? await ReadTextAsync(entry, cancellationToken) : null;
        if (text is null || !IsSettings(name, text))
        {
            return (Refuse(path, ImportArea.Settings, "Not the JSON this settings file holds, or larger than 1 MB."), null);
        }

        var existing = await source.ReadSettingsFileAsync(name, cancellationToken);
        var change = existing is null ? ImportChange.New : existing == text ? ImportChange.Same : ImportChange.Changed;
        return (new ImportEntry(path, ImportArea.Settings, change, name, Version: existing is null ? null : Hash(existing)), Encoding.UTF8.GetBytes(text));
    }

    private static async Task<(ImportEntry, byte[]?)> CheckContentAsync(
        ZipArchiveEntry entry, Dictionary<string, string> current, Dictionary<(ContentKind, string), string> addresses, string language, CancellationToken cancellationToken)
    {
        // A post or page file named like a slug, in any script (F-0091 L1): "posts/első.md".
        var path = entry.FullName;
        // Folders under posts/ and pages/ too, and .MD, as the site loads them, so a backup of any
        // site imports whole (F-0119); every part must look like a slug, which rules out "..".
        var match = ContentPath().Match(path);
        if (!match.Success || !Slugs.IsValid(match.Groups["name"].Value) || !match.Groups["dir"].Captures.All(c => Slugs.IsValid(c.Value)))
        {
            return (Refuse(path, ImportArea.Content, "Not part of a Weesite backup."), null);
        }

        var relative = path["content/".Length..];
        var body = entry.Length <= MaxContentBytes ? await ReadTextAsync(entry, MaxContentBytes, cancellationToken) : null;
        if (body is null)
        {
            return (Refuse(path, ImportArea.Content, "Larger than 4 MB."), null);
        }

        var parsed = ContentParser.Parse(relative, body, language);
        if (parsed.Item is not { } item)
        {
            return (Refuse(path, ImportArea.Content, parsed.Error ?? "It doesn't parse."), null);
        }

        // The site would skip it: the editor refuses these slugs too (F-0119). The special
        // addresses were free slugs before, and still load (T-0159).
        if ((item.Kind == ContentKind.Page ? Slugs.Reserved : Slugs.ReservedForPosts).Contains(item.Slug) && !Slugs.SpecialAddresses.Contains(item.Slug))
        {
            return (Refuse(path, ImportArea.Content, $"The slug '{item.Slug}' is reserved for the site."), null);
        }

        // A special page has no address to clash (T-0159).
        if (!item.IsSpecial && addresses.TryGetValue((item.Kind, item.Slug), out var holder) && holder != relative)
        {
            return (Refuse(path, ImportArea.Content, $"Its address is already used by {holder}."), null);
        }

        if (!item.IsSpecial)
        {
            addresses[(item.Kind, item.Slug)] = relative;
        }

        var change = !current.TryGetValue(relative, out var now) ? ImportChange.New : now == body ? ImportChange.Same : ImportChange.Changed;
        return (new ImportEntry(path, ImportArea.Content, change, item.Title, Version: now is null ? null : Hash(now)), Encoding.UTF8.GetBytes(body));
    }

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    // An image goes through the same processing as an upload: its metadata (camera, location)
    // stripped, its copies made; the original last, so a cut-short import leaves nothing that
    // looks done (T-0122).
    private async Task<bool> WriteMediaAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        var name = path["media/".Length..];
        if (ImageProcessor.IsProcessable(name))
        {
            // Within the uploads' limit of two at a time: decoding a large one takes a few hundred MB (F-0248).
            var image = await MediaLibrary.ProcessAsync(name, data, cancellationToken);
            foreach (var variant in image.Variants)
            {
                await media.WriteAsync(variant.Name, variant.Data, cancellationToken);
            }

            await media.WriteAsync(name, image.Original, cancellationToken);
            return true;
        }

        await media.WriteAsync(name, data, cancellationToken);
        return true;
    }

    // The zip's file count from its end-of-central-directory record; -1 when there's none or the
    // zip is Zip64 (the count is then 0xFFFF). Leaves the stream at its start.
    private static async Task<long> EntryCountAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (!stream.CanSeek || stream.Length < 22)
        {
            return stream.CanSeek ? -1 : 0;
        }

        var tail = (int)Math.Min(stream.Length, 22 + ushort.MaxValue);
        var buffer = new byte[tail];
        stream.Seek(-tail, SeekOrigin.End);
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        stream.Seek(0, SeekOrigin.Begin);
        for (var i = tail - 22; i >= 0; i--)
        {
            if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
            {
                var count = BitConverter.ToUInt16(buffer, i + 10);
                return count == ushort.MaxValue ? -1 : count;
            }
        }

        return -1;
    }

    // A new file is written only if it's still missing; an overwrite only if the site still has
    // the version the review showed. Anything else changed meanwhile: a conflict, left alone.
    private async Task<bool> WriteSettingsAsync(string path, byte[] data, string? reviewed, CancellationToken cancellationToken)
    {
        var name = path["settings/".Length..];
        string? etag = null;
        if (reviewed is not null)
        {
            if (await store.ReadSettingsFileAsync(name, cancellationToken) is not { } now || Hash(now.Text) != reviewed)
            {
                return false;
            }

            etag = now.ETag;
        }

        return await store.WriteSettingsFileAsync(name, Encoding.UTF8.GetString(data), etag, cancellationToken) is not null;
    }

    private async Task<bool> WriteContentAsync(string path, byte[] data, string? reviewed, CancellationToken cancellationToken)
    {
        var relative = path["content/".Length..];
        string? etag = null;
        if (reviewed is not null)
        {
            if (await store.ReadAsync(relative, cancellationToken) is not { } now || Hash(now.Text) != reviewed)
            {
                return false;
            }

            etag = now.ETag;
        }

        return await store.WriteAsync(relative, Encoding.UTF8.GetString(data), etag, cancellationToken) is not null;
    }

    private static (string Site, string ExportedAt)? Manifest(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            static string Text(JsonElement root, string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

            // Any other shape (a fraction, a number as text) isn't a Weesite backup (F-0109).
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Number
                && format.TryGetInt32(out var number) && number == SiteBackup.Format
                ? (Text(root, "site"), Text(root, "exportedAt"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The shape the site writes: authors.json is a list of profiles, the others are objects
    // (F-0078); site.json must also read as the site's settings, which every load needs (F-0109).
    private static bool IsSettings(string name, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var kind = name == ContentContainers.AuthorsFile ? JsonValueKind.Array : JsonValueKind.Object;
            if (document.RootElement.ValueKind != kind)
            {
                return false;
            }

            if (name == ContentContainers.SiteSettingsFile)
            {
                _ = SiteSettings.Parse(text);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadTextAsync(ZipArchiveEntry entry, CancellationToken cancellationToken) =>
        await ReadTextAsync(entry, MaxTextBytes, cancellationToken);

    private static async Task<string?> ReadTextAsync(ZipArchiveEntry entry, long limit, CancellationToken cancellationToken) =>
        entry.Length > limit ? null : Encoding.UTF8.GetString(await ReadBytesAsync(entry, limit, cancellationToken));

    // Reads at most limit bytes, whatever the entry's header claims (a zip bomb lies about sizes).
    private static async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry, long limit, CancellationToken cancellationToken)
    {
        await using var stream = await entry.OpenAsync(cancellationToken);
        await using var limited = new LimitedStream(stream, limit);
        using var buffer = new MemoryStream();
        await limited.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    // Content files: posts/ or pages/, up to four folders, a slug-like name, .md in any case.
    [GeneratedRegex("^content/(posts|pages)/(?:(?<dir>[^/\\\\]{1,150})/){0,4}(?<name>[^/\\\\]{1,150})\\.[mM][dD]$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
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
