using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Web.Admin;
using Weesite.Web.Seo;

namespace Weesite.Web.Agents;

/// <summary>A post or page as the editor tools list it.</summary>
/// <param name="Path">Its file, the ID the other tools take, such as <c>posts/hello-world.md</c>.</param>
/// <param name="Kind"><c>post</c> or <c>page</c>.</param>
/// <param name="Title">The title.</param>
/// <param name="Slug">The slug.</param>
/// <param name="Status"><c>draft</c>, <c>scheduled</c>, <c>published</c> or <c>archived</c>.</param>
/// <param name="Date">The publication date (posts), <c>yyyy-MM-dd</c>.</param>
/// <param name="PublishAt">When a scheduled item goes public, in UTC.</param>
/// <param name="Tags">The tags.</param>
/// <param name="Summary">The summary, if any.</param>
internal sealed record EditorItem(
    string Path,
    string Kind,
    string Title,
    string Slug,
    string Status,
    string? Date,
    string? PublishAt,
    IReadOnlyList<string> Tags,
    string? Summary);

/// <summary>A post or page in full, as stored.</summary>
/// <param name="Path">Its file.</param>
/// <param name="Version">The stored version: give it back when saving, so a save never overwrites someone else's.</param>
/// <param name="Markdown">The file: YAML front matter, then the Markdown text.</param>
internal sealed record EditorDocument(string Path, string Version, string Markdown);

/// <summary>A file in Media.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Path">Its address on the site, such as <c>/media/abc123-photo.jpg</c>.</param>
/// <param name="IsImage">Whether it's an image.</param>
/// <param name="Width">The width of an image, in pixels.</param>
/// <param name="Height">The height of an image, in pixels.</param>
/// <param name="Markdown">The Markdown that shows it in a post; an image's alt text is a placeholder to replace.</param>
internal sealed record EditorMedia(string Name, string Path, bool IsImage, int? Width, int? Height, string Markdown);

/// <summary>A saved post or page.</summary>
/// <param name="Path">Its file.</param>
/// <param name="Version">The new version, for the next save.</param>
/// <param name="Status">Its status now.</param>
/// <param name="Url">Its public address, once it's public.</param>
/// <param name="PreviewUrl">Its unlisted preview for editors, while it isn't public.</param>
internal sealed record EditorSaved(string Path, string Version, string Status, string? Url, string? PreviewUrl);

/// <summary>
/// The editor MCP tools (ADR 0007), served at <c>/mcp/editor</c> to apps an editor allowed: the
/// editor's own view of the content, drafts included, with the admin's rights (authors see and
/// change only their own posts).
/// </summary>
[McpServerToolType]
internal sealed class EditorTools(
    ContentCatalogProvider content,
    ContentEditor editor,
    MediaLibrary media,
    IAuditLog audit,
    SiteUrls urls,
    IHttpContextAccessor http,
    TimeProvider time)
{
    /// <summary>The names of the editor tools: <c>/mcp/editor</c> serves these and nothing else, <c>/mcp</c> never these.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_items", "get_item", "list_media", "create_item", "update_item", "set_status", "upload_media",
    };

    private const int MaxLimit = 200;

    /// <summary>
    /// The largest file upload_media takes: 10 MB, half the editor's. The file comes as base64 in
    /// JSON, so the request holds it several times over before libvips decodes it (F-0066).
    /// </summary>
    public const int MaxUploadBytes = 10 * 1024 * 1024;

    /// <summary>Lists the posts and pages the editor may change.</summary>
    /// <param name="kind"><c>post</c>, <c>page</c>, or empty for both.</param>
    /// <param name="status">Only this status, or empty for all but archived ones.</param>
    /// <param name="query">Only items whose title, slug or tags contain this.</param>
    /// <param name="limit">The most items to return.</param>
    /// <returns>The items, newest posts first, then pages by title.</returns>
    [McpServerTool(Name = "list_items", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the site's posts and pages you may edit, drafts and scheduled ones included: path (the ID other tools take), title, slug, status, dates, tags and summary. Authors see only their own posts. Archived items appear only when asked for with status=archived.")]
    public IReadOnlyList<EditorItem> ListItems(
        [Description("post, page, or empty for both.")] string? kind = null,
        [Description("draft, scheduled, published or archived; empty for everything but archived.")] string? status = null,
        [Description("Only items whose title, slug or tags contain this text.")] string? query = null,
        [Description("The most items to return, 1 to 200.")] int limit = 50)
    {
        var user = User();
        var catalog = content.Current;
        var items = catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished)
            .Where(i => EditorRights.CanEdit(user, i.Kind, i.AuthorId, isNew: false))
            .Where(i => kind switch
            {
                "post" => i.Kind == ContentKind.Post,
                "page" => i.Kind == ContentKind.Page,
                null or "" => true,
                _ => throw new McpException("kind is post, page or empty."),
            })
            .Where(i => string.IsNullOrEmpty(status) ? StatusOf(i) != "archived" : StatusOf(i) == status);
        if (query?.Trim().TrimStart('#') is { Length: > 0 } text)
        {
            items = items.Where(i => i.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || i.Slug.Contains(text, StringComparison.OrdinalIgnoreCase)
                || i.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        return [.. items
            .OrderBy(i => i.Kind == ContentKind.Page)
            .ThenByDescending(i => i.Date)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(i => new EditorItem(
                i.SourcePath,
                i.Kind == ContentKind.Page ? "page" : "post",
                i.Title,
                i.Slug,
                StatusOf(i),
                i.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                i.Status == ContentStatus.Scheduled ? i.PublishAt?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture) : null,
                i.Tags,
                i.Summary))];
    }

    /// <summary>Gets a post or page as stored.</summary>
    /// <param name="path">Its path, from list_items.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The file and its version.</returns>
    [McpServerTool(Name = "get_item", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get a post or page as stored: its front matter (title, slug, status, dates, tags, summary, image) and Markdown text, with the version to give back when saving it. Takes the path from list_items.")]
    public async Task<EditorDocument> GetItemAsync(
        [Description("The item's path from list_items, such as posts/hello-world.md.")] string path,
        CancellationToken cancellationToken)
    {
        var opened = await editor.OpenAsync(path, cancellationToken);
        if (opened is null || !EditorRights.CanEdit(User(), opened.Draft.Kind, opened.Draft.AuthorId, isNew: false))
        {
            throw new McpException($"No post or page you may edit at {path}.");
        }

        return new EditorDocument(opened.Path, opened.ETag, opened.Draft.ToMarkdown());
    }

    /// <summary>Lists the files in Media.</summary>
    /// <param name="query">Only files whose name contains this.</param>
    /// <param name="limit">The most files to return.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The files.</returns>
    [McpServerTool(Name = "list_media", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the images and files in Media that can go in a post: name, address, size of images, and the Markdown that shows each (replace an image's placeholder alt text with what it shows).")]
    public async Task<IReadOnlyList<EditorMedia>> ListMediaAsync(
        [Description("Only files whose name contains this text.")] string? query = null,
        [Description("The most files to return, 1 to 200.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var files = await media.ListAsync(cancellationToken);
        var index = content.Current.Media;
        return [.. files
            .Where(f => !f.IsArchived && (string.IsNullOrWhiteSpace(query) || f.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(f => MediaLinks.Describe(f, index, null))
            .Select(l => new EditorMedia(l.Name, l.Path, l.IsImage, l.Width, l.Height, l.Markdown))];
    }

    /// <summary>Creates a post or page, always as a draft.</summary>
    /// <param name="markdown">The file: front matter and text.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Where it was saved.</returns>
    [McpServerTool(Name = "create_item", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a post or page as a draft, from a Markdown file with YAML front matter like get_item returns: title, slug, kind (post or page; authors only post), date (yyyy-MM-dd, posts), tags, summary, lang (a code such as hu, only when the item isn't in the site's language), then the text. Images need alt text: ![what it shows](/media/...). It stays a draft whatever the front matter says; use set_status to publish or schedule it.")]
    public async Task<EditorSaved> CreateItemAsync(
        [Description("The whole file: --- front matter --- then the Markdown text.")] string markdown,
        CancellationToken cancellationToken)
    {
        var user = User();
        var draft = Parse("new.md", markdown);
        if (!EditorRights.CanEdit(user, draft.Kind, null, isNew: true))
        {
            throw new McpException("Authors can create posts only.");
        }

        // Admins may name another author; an author's posts are their own.
        var authorId = user.IsInRole(nameof(EditorRole.Admin)) ? draft.AuthorId ?? EditorRights.AuthorId(user) : EditorRights.AuthorId(user);
        draft = draft with { Status = ContentStatus.Draft, PublishAt = null, AuthorId = authorId, RedirectFrom = [] };
        return await SaveAsync(null, draft, null, "mcp-item-created", cancellationToken);
    }

    /// <summary>Saves new text and front matter for a post or page.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="version">The version get_item gave.</param>
    /// <param name="markdown">The whole new file.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The new version.</returns>
    [McpServerTool(Name = "update_item", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Replace a post's or page's front matter and text with a whole new file, saved only if nobody saved it since the version you read (get_item). Its kind, author and status stay as they are: use set_status to publish, schedule, unpublish or archive. Changing a public item's slug keeps the old address as a redirect. An app allowed drafts only can change drafts only; one allowed edits can change any item, keeping its status.")]
    public async Task<EditorSaved> UpdateItemAsync(
        [Description("The item's path, such as posts/hello-world.md.")] string path,
        [Description("The version from get_item (or from your last save).")] string version,
        [Description("The whole new file: --- front matter --- then the Markdown text.")] string markdown,
        CancellationToken cancellationToken)
    {
        var stored = await OpenAsync(path, cancellationToken);
        if (!Has(User(), Agents.OAuth.EditorBearerHandler.EditClaim) && stored.Draft.Status != ContentStatus.Draft)
        {
            throw new McpException(DraftsOnly);
        }

        var draft = Parse(path, markdown) with
        {
            Kind = stored.Draft.Kind,
            AuthorId = stored.Draft.AuthorId,
            Status = stored.Draft.Status,
            PublishAt = stored.Draft.PublishAt,
            RedirectFrom = stored.Draft.RedirectFrom,
        };
        return await SaveAsync(path, draft, version, "mcp-item-saved", cancellationToken);
    }

    /// <summary>Publishes, schedules, unpublishes or archives a post or page.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="version">The version last read or saved.</param>
    /// <param name="status">The new status.</param>
    /// <param name="publishAt">When a scheduled item goes public.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The new version and status.</returns>
    [McpServerTool(Name = "set_status", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Change a post's or page's status: published (public now, in feeds and search), scheduled (public at publish_at), draft (editors only) or archived (off the site, kept in the editor). Saved only if nobody saved it since the version you give. Only for apps the editor allowed to publish; others get an error that says so.")]
    public async Task<EditorSaved> SetStatusAsync(
        [Description("The item's path, such as posts/hello-world.md.")] string path,
        [Description("The version from get_item or your last save.")] string version,
        [Description("published, scheduled, draft or archived.")] string status,
        [Description("For scheduled: when it goes public, with an offset, such as 2026-12-01T09:00:00Z.")] string? publishAt = null,
        CancellationToken cancellationToken = default)
    {
        if (!Has(User(), Agents.OAuth.EditorBearerHandler.PublishClaim))
        {
            throw new McpException(Has(User(), Agents.OAuth.EditorBearerHandler.EditClaim) ? EditsOnly : DraftsOnly);
        }

        var stored = await OpenAsync(path, cancellationToken);
        var (newStatus, at) = status switch
        {
            "published" => (ContentStatus.Published, (DateTimeOffset?)null),
            "draft" => (ContentStatus.Draft, null),
            "archived" => (ContentStatus.Archived, null),
            "scheduled" => (ContentStatus.Scheduled, ScheduleTime(publishAt)),
            _ => throw new McpException("status is published, scheduled, draft or archived."),
        };
        var draft = stored.Draft with { Status = newStatus, PublishAt = at };
        return await SaveAsync(path, draft, version, "mcp-status-" + status, cancellationToken);
    }

    /// <summary>Uploads a file to Media.</summary>
    /// <param name="fileName">Its name, with an extension Media accepts.</param>
    /// <param name="contentBase64">Its bytes, base64.</param>
    /// <param name="alt">What an image shows.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The stored file and the Markdown that shows it.</returns>
    [McpServerTool(Name = "upload_media", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Upload an image (JPEG, PNG, WebP) or a file (PDF, SVG, text, CSV, ZIP) of up to 10 MB to Media; larger files go through the editor's media page. Images lose their metadata (camera, location) and get smaller WebP copies. Returns the Markdown to put in a post; an image needs alt, a description of what it shows.")]
    public async Task<EditorMedia> UploadMediaAsync(
        [Description("The file name with its extension, such as harbour-at-dusk.jpg.")] string fileName,
        [Description("The file's bytes, base64-encoded.")] string contentBase64,
        [Description("For images: what it shows, for people who can't see it.")] string? alt = null,
        CancellationToken cancellationToken = default)
    {
        var user = User();
        if (ImageProcessor.IsProcessable(fileName) && string.IsNullOrWhiteSpace(alt))
        {
            throw new McpException("Describe the image (alt): what does it show?");
        }

        // Refused before decoding: base64 takes four characters for every three bytes.
        if (contentBase64.Length > ((MaxUploadBytes + 2) / 3 * 4) + 4)
        {
            throw new McpException("The file is larger than 10 MB. Upload it on the editor's media page instead.");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            throw new McpException("contentBase64 isn't valid base64.");
        }

        if (data.Length == 0 || data.Length > MaxUploadBytes)
        {
            throw new McpException("The file is empty or larger than 10 MB. Upload larger files on the editor's media page.");
        }

        var outcome = await media.UploadAsync(fileName, data, cancellationToken);
        if (outcome is not MediaUploaded uploaded)
        {
            throw new McpException(((MediaRejected)outcome).Reason);
        }

        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Email(user), "mcp-media-uploaded", Via(user, uploaded.Item.Name)), cancellationToken);
        var link = MediaLinks.Describe(uploaded.Item, content.Current.Media, alt);
        return new EditorMedia(link.Name, link.Path, link.IsImage, link.Width, link.Height, link.Markdown);
    }

    // What the editor let the app do (the consent, F-0070): drafts only can't change what visitors
    // see; edits can change any item but not its status.
    private const string DraftsOnly =
        "This app may write drafts only: it can't change published, scheduled or archived items, or any item's status. Do that in the editor, or connect the app again and allow edits or publishing.";

    private const string EditsOnly =
        "This app may edit but not publish: it can't change any item's status. Publish, schedule, unpublish or archive in the editor, or connect the app again and allow publishing.";

    private static bool Has(ClaimsPrincipal user, string claim) => user.HasClaim(c => c.Type == claim);

    // The target of an audit entry: what changed, and the app that changed it.
    private static string Via(ClaimsPrincipal user, string target) =>
        $"{target} (via {user.FindFirstValue(Agents.OAuth.EditorBearerHandler.ClientClaim) ?? "an app"})";

    private static string Email(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? "(unknown)";

    private DateTimeOffset ScheduleTime(string? publishAt) =>
        DateTimeOffset.TryParse(publishAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
        && publishAt!.Length > 10 && (publishAt is [.., 'Z'] || publishAt[^6] is '+' or '-')
            ? at > time.GetUtcNow() ? at : throw new McpException("publish_at is in the past: use status published to publish now.")
            : throw new McpException("A scheduled item needs a publish time (publish_at), a time with an offset, such as 2026-12-01T09:00:00Z.");

    // A file parsed as the public site does, so a save can't store what the site would skip.
    private ContentDraft Parse(string path, string markdown)
    {
        var result = ContentParser.Parse(path, markdown ?? "", content.Current.Site.Language);
        return result.Item is { } item
            ? ContentDraft.From(item)
            : throw new McpException(result.Error is { } error ? error[(error.IndexOf(": ", StringComparison.Ordinal) + 2)..] : "The file couldn't be read.");
    }

    private async Task<OpenedItem> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var opened = await editor.OpenAsync(path, cancellationToken);
        return opened is not null && EditorRights.CanEdit(User(), opened.Draft.Kind, opened.Draft.AuthorId, isNew: false)
            ? opened
            : throw new McpException($"No post or page you may edit at {path}.");
    }

    private async Task<EditorSaved> SaveAsync(string? path, ContentDraft draft, string? version, string action, CancellationToken cancellationToken)
    {
        var user = User();
        switch (await editor.SaveAsync(path, draft, version, cancellationToken))
        {
            case ContentSaved saved:
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Email(user), action, Via(user, saved.Path)), cancellationToken);
                return Describe(saved);
            case ContentConflict:
                throw new McpException("Someone saved this item since the version you have: get_item again, then make your change to the new version.");
            case ContentInvalid invalid:
                throw new McpException(invalid.Message);
            default:
                throw new McpException("The item wasn't saved.");
        }
    }

    // The saved item as the reloaded catalog has it: public, or waiting with an unlisted preview.
    private EditorSaved Describe(ContentSaved saved)
    {
        var catalog = content.Current;
        var request = http.HttpContext!.Request;
        if (catalog.Posts.Concat(catalog.Pages).FirstOrDefault(i => i.SourcePath == saved.Path) is { } item)
        {
            var path = Seo.ContentUrls.PathOf(item);
            return new EditorSaved(saved.Path, saved.ETag, StatusOf(item), urls.Absolute(request, path), null);
        }

        return catalog.Unpublished.FirstOrDefault(i => i.SourcePath == saved.Path) is { } waiting
            ? new EditorSaved(saved.Path, saved.ETag, StatusOf(waiting), null, waiting.Status == ContentStatus.Archived ? null : urls.Absolute(request, Components.Pages.Preview.UrlOf(waiting)))
            : new EditorSaved(saved.Path, saved.ETag, "draft", null, null);
    }

    // The editor the token belongs to: EditorBearerHandler signed them in for this request.
    private ClaimsPrincipal User() =>
        http.HttpContext?.User is { Identity.IsAuthenticated: true } user
            ? user
            : throw new McpException("Not signed in.");

    // As on the admin lists: a scheduled item whose time has come is published.
    private string StatusOf(ContentItem item) => item.Status switch
    {
        ContentStatus.Draft => "draft",
        ContentStatus.Archived => "archived",
        ContentStatus.Scheduled when !item.IsPublicAt(time.GetUtcNow()) => "scheduled",
        _ => "published",
    };
}
