using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Web.Admin;

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

/// <summary>
/// The editor MCP tools (ADR 0007), served at <c>/mcp/editor</c> to apps an editor allowed: the
/// editor's own view of the content, drafts included, with the admin's rights (authors see and
/// change only their own posts).
/// </summary>
[McpServerToolType]
internal sealed class EditorTools(ContentCatalogProvider content, ContentEditor editor, MediaLibrary media, IHttpContextAccessor http, TimeProvider time)
{
    /// <summary>The names of the editor tools: <c>/mcp/editor</c> serves these and nothing else, <c>/mcp</c> never these.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal) { "list_items", "get_item", "list_media" };

    private const int MaxLimit = 200;

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
