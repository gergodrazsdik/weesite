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

/// <summary>A page of list_items (F-0163).</summary>
/// <param name="Items">The items on this page.</param>
/// <param name="Total">How many items match, on all pages.</param>
/// <param name="NextOffset">The offset of the next page, or null on the last one.</param>
internal sealed record EditorItemPage(IReadOnlyList<EditorItem> Items, int Total, int? NextOffset);

/// <summary>A page of list_media (F-0163).</summary>
/// <param name="Files">The files on this page.</param>
/// <param name="Total">How many files match, on all pages.</param>
/// <param name="NextOffset">The offset of the next page, or null on the last one.</param>
internal sealed record EditorMediaPage(IReadOnlyList<EditorMedia> Files, int Total, int? NextOffset);

/// <summary>A replaced media file (F-0143).</summary>
/// <param name="File">The new file.</param>
/// <param name="Updated">The posts and pages now using it, by path.</param>
/// <param name="Failed">Those that still use the old file, by path.</param>
/// <param name="Note">What became of the old file.</param>
internal sealed record EditorReplaced(EditorMedia File, IReadOnlyList<string> Updated, IReadOnlyList<string> Failed, string Note);

/// <summary>A saved post or page.</summary>
/// <param name="Path">Its file.</param>
/// <param name="Version">The new version, for the next save.</param>
/// <param name="Status">Its status now.</param>
/// <param name="Url">Its public address, once it's public and has one.</param>
/// <param name="PreviewUrl">Its unlisted preview for editors, while it isn't public or has no address.</param>
/// <param name="Unchanged">True when the item already had this text: nothing was written and no version was made (F-0174).</param>
/// <param name="Note">Why a published item has no address, if it has none (F-0233).</param>
internal sealed record EditorSaved(string Path, string Version, string Status, string? Url, string? PreviewUrl, bool Unchanged = false, string? Note = null);

/// <summary>An item's tags after <c>set_tags</c> (T-0186).</summary>
/// <param name="Path">Its file.</param>
/// <param name="Version">The new version, for the next save.</param>
/// <param name="Tags">Its tags now, in order.</param>
/// <param name="NewTags">Those of them no other post or page carries: check they aren't misspellings of a tag in use.</param>
/// <param name="Unchanged">True when it already had these tags: nothing was written.</param>
internal sealed record EditorTagsSaved(string Path, string Version, IReadOnlyList<string> Tags, IReadOnlyList<string> NewTags, bool Unchanged);

/// <summary>A tag of the site's posts (T-0140).</summary>
/// <param name="Name">The tag, exactly as posts carry it.</param>
/// <param name="Posts">How many published posts carry it.</param>
/// <param name="NotPublic">How many of the editor's drafts, scheduled and archived posts carry it.</param>
/// <param name="Colour">Its colour from the tags page, if it has one.</param>
/// <param name="Description">What it's about, from the tags page, if it has one.</param>
internal sealed record EditorTag(string Name, int Posts, int NotPublic, string? Colour, string? Description);

/// <summary>A post or page rendered as the site would show it (F-0123).</summary>
/// <param name="Title">Its title.</param>
/// <param name="Html">The text as HTML, as on the site: sanitized, headings shifted, code highlighted.</param>
/// <param name="Words">The number of words.</param>
/// <param name="ReadingMinutes">The reading time the site shows.</param>
/// <param name="Problems">What the site would skip or warn about: images without alt text, unknown media, unsafe links.</param>
/// <param name="PreviewUrl">The stored item's preview page, for the editor to look at in a browser; null for unsaved text.</param>
internal sealed record EditorPreview(string Title, string Html, int Words, int ReadingMinutes, IReadOnlyList<string> Problems, string? PreviewUrl);

/// <summary>A stored version of an item (F-0124).</summary>
/// <param name="Version">Its ID, for get_version_diff.</param>
/// <param name="SavedAt">When it was saved (UTC, ISO 8601).</param>
/// <param name="IsCurrent">Whether it's the version the site has now.</param>
internal sealed record EditorVersion(string Version, string SavedAt, bool IsCurrent);

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
    Weesite.Content.Markdown.MarkdownRenderer renderer,
    Weesite.Content.Markdown.CodeHighlighter highlighter,
    MediaReplacer replacer,
    MediaUploads uploads,
    IAuditLog audit,
    SiteUrls urls,
    IHttpContextAccessor http,
    TimeProvider time,
    RenderGate gate,
    PreviewLinks links)
{
    /// <summary>The names of the editor tools: <c>/mcp/editor</c> serves these and nothing else, <c>/mcp</c> never these.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_items", "get_item", "list_media", "create_item", "update_item", "set_status", "upload_media",
        "preview_item", "list_versions", "get_version_diff", "markdown_guide", "replace_media", "create_media_upload",
        "list_tags", "set_tags", "create_preview_link",

        // Comments (ADR 0011), in CommentTools.
        "list_comments", "moderate_comment", "delete_comment",
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
    /// <param name="offset">How many matching items to skip, for the next page.</param>
    /// <returns>A page of the items, newest posts first, then pages by title.</returns>
    [McpServerTool(Name = "list_items", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the site's posts and pages you may edit, drafts and scheduled ones included: path (the ID other tools take), title, slug, status, dates, tags and summary. Authors see only their own posts. Archived items appear only when asked for with status=archived. Returns a page: total says how many match, and nextOffset, when there are more, is the offset to ask for next.")]
    public EditorItemPage ListItems(
        [Description("post, page, or empty for both.")] string? kind = null,
        [Description("draft, scheduled, published or archived; empty for everything but archived.")] string? status = null,
        [Description("Only items whose title, slug or tags contain this text.")] string? query = null,
        [Description("The most items to return, 1 to 200.")] int limit = 50,
        [Description("How many matching items to skip: the nextOffset of the previous page.")] int offset = 0)
    {
        // Checked first, so a wrong value says so rather than finding nothing (F-0115).
        kind = kind?.Trim().ToUpperInvariant() switch
        {
            null or "" => null,
            "POST" or "POSTS" => "post",
            "PAGE" or "PAGES" => "page",
            _ => throw new McpException("kind is post, page or empty."),
        };
        status = status?.Trim().ToUpperInvariant() switch
        {
            null or "" => null,
            "DRAFT" => "draft",
            "SCHEDULED" => "scheduled",
            "PUBLISHED" => "published",
            "ARCHIVED" => "archived",
            _ => throw new McpException("status is draft, scheduled, published, archived or empty."),
        };
        var user = User();
        var catalog = content.Current;
        var items = catalog.AllItems
            .Where(i => EditorRights.CanEdit(user, i.Kind, i.AuthorId, isNew: false))
            .Where(i => kind is null || i.Kind == (kind == "post" ? ContentKind.Post : ContentKind.Page))
            .Where(i => status is null ? StatusOf(i) != "archived" : StatusOf(i) == status);
        if (query?.Trim().TrimStart('#') is { Length: > 0 } text)
        {
            items = items.Where(i => i.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || i.Slug.Contains(text, StringComparison.OrdinalIgnoreCase)
                || i.Tags.Any(t => t.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        var matching = items.ToList();
        var (skip, take) = Page(offset, limit);
        List<EditorItem> page = [.. matching
            .OrderBy(i => i.Kind == ContentKind.Page)
            .ThenByDescending(i => i.Date)
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .Skip(skip)
            .Take(take)
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
        return new EditorItemPage(page, matching.Count, NextOffset(skip, page.Count, matching.Count));
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

    /// <summary>Renders a post or page as the site would show it.</summary>
    /// <param name="path">A stored item's path, to preview it as saved.</param>
    /// <param name="markdown">Or a whole file, to preview text before saving it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The rendered item.</returns>
    [McpServerTool(Name = "preview_item", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Preview a post or page as the site would show it, before publishing: the HTML the site renders (sanitized, headings shifted under the title, code highlighted), its word count and reading time, and the problems the site would warn about, each with its line in the file (images without alt text, media that isn't in Media, images from other sites, braces after an image that aren't a placement, links whose scheme isn't allowed, relative links, the front matter image, a summary over 160 characters). Give a stored item's path, or a whole file to check text before saving it. For a stored item it also gives its preview page, for a person to look at in a browser signed in to /admin; to look at the page yourself, use create_preview_link.")]
    public async Task<EditorPreview> PreviewItemAsync(
        [Description("A stored item's path, such as posts/hello-world.md; or leave it out and give markdown.")] string? path = null,
        [Description("A whole file (--- front matter --- then the text) to preview without saving it.")] string? markdown = null,
        CancellationToken cancellationToken = default)
    {
        var user = User();
        ContentDraft draft;
        string? previewUrl = null;
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            draft = Parse(path ?? "preview.md", markdown);
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            draft = (await OpenAsync(path, cancellationToken)).Draft;
            var catalog = content.Current;
            var request = http.HttpContext!.Request;
            // A special page Site settings don't place has no address: its preview page, not "/" (F-0233).
            previewUrl = catalog.PublishedItems.FirstOrDefault(i => i.SourcePath == path) is { } item
                ? urls.Absolute(request, ContentUrls.PathOf(item) is { Length: > 0 } address ? address : Components.Pages.Preview.UrlOf(item))
                : catalog.Unpublished.FirstOrDefault(i => i.SourcePath == path) is { Status: not ContentStatus.Archived } waiting
                    ? urls.Absolute(request, Components.Pages.Preview.UrlOf(waiting))
                    : null;
        }
        else
        {
            throw new McpException("Give a stored item's path, or the markdown to preview.");
        }

        if (!EditorRights.CanEdit(user, draft.Kind, draft.AuthorId, isNew: path is null))
        {
            throw new McpException("You may not preview that item.");
        }

        // One render at a time per editor (T-0177), shared with the admin's preview and exports.
        using var turn = await gate.EnterAsync(user, RenderGate.RequestWait, cancellationToken) ?? throw new McpException("Another preview of yours is still being made: try again in a moment.");

        // Lines as in the whole file, the front matter included (F-0146).
        var media = content.Current.Media;
        var rendered = renderer.Render(draft.Body, media, ContentParser.BodyLine(string.IsNullOrWhiteSpace(markdown) ? draft.ToMarkdown() : markdown));
        var words = rendered.Words;
        var problems = rendered.Problems.ToList();
        if (draft.Image?.Trim() is { Length: > 0 } image)
        {
            if (!image.StartsWith("/media/", StringComparison.Ordinal))
            {
                problems.Add($"The front matter image {image} isn't a /media/ address: link previews show the site's generated card instead.");
            }
            else if (!ReferenceEquals(media, MediaIndex.Empty) && !media.Has(image["/media/".Length..].Split('?', '#')[0]))
            {
                problems.Add($"The front matter image {image} isn't in Media.");
            }

            if (string.IsNullOrWhiteSpace(draft.ImageAlt))
            {
                problems.Add("The front matter image has no image_alt: link previews describe it with the title.");
            }
        }

        if (draft.Summary?.Length > ContentDraft.MaxSummaryLength)
        {
            problems.Add($"The summary has {draft.Summary.Length} characters; at most {ContentDraft.MaxSummaryLength} are saved.");
        }

        // As the public page writes it: the SEO title or the title, then the site's name.
        var titleTag = $"{(string.IsNullOrWhiteSpace(draft.SeoTitle) ? draft.Title : draft.SeoTitle).Trim()} · {content.Current.Site.Name}";
        if (titleTag.Length > ContentDraft.TitleTagLength)
        {
            problems.Add($"The page's <title>, \"{titleTag}\", has {titleTag.Length} characters; search engines show about {ContentDraft.TitleTagLength}. Give it a shorter seo_title (the heading keeps the title).");
        }

        return new EditorPreview(draft.Title, rendered.Html, words, ContentItem.MinutesFor(words), problems, previewUrl);
    }

    /// <summary>Makes a link to a saved item's preview page that works without signing in (T-0187).</summary>
    /// <param name="path">Its path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The link, or the public address of a public item.</returns>
    [McpServerTool(Name = "create_preview_link", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Get a link to a saved post's or page's preview page that you can open in a browser or fetch without signing in, to see it as visitors will: the site's layout, styles, images and code highlighting. It shows the last saved version, so save first, and reload it after each save. It works for an hour, for anyone who has it, until this app is disconnected: don't share it. For an item that's already public it gives its public address. Not for archived items. (preview_item's previewUrl needs a browser signed in to /admin.)")]
    public async Task<EditorPreviewLink> CreatePreviewLinkAsync(
        [Description("The item's path, such as posts/hello-world.md.")] string path,
        CancellationToken cancellationToken)
    {
        var user = User();
        var stored = await OpenAsync(path, cancellationToken);
        var catalog = content.Current;
        var request = http.HttpContext!.Request;
        var item = catalog.AllItems.FirstOrDefault(i => i.SourcePath == stored.Path)
            ?? throw new McpException($"No post or page you may edit at {path}.");
        if (item.Status == ContentStatus.Archived)
        {
            throw new McpException("This item is archived: it has no preview. Bring it back as a draft in /admin first.");
        }

        if (!catalog.Unpublished.Contains(item) && Seo.ContentUrls.PathOf(item) is { Length: > 0 } address)
        {
            return new EditorPreviewLink(urls.Absolute(request, address), null, "The item is public: this is its address.");
        }

        var (link, expiresAt) = links.Create(user, item.SourcePath);
        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Email(user), "mcp-preview-link", Via(user, item.SourcePath)), CancellationToken.None);
        return new EditorPreviewLink(
            urls.Absolute(request, link),
            expiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            "The preview page as last saved, without signing in, until expiresAt. It needs no cookies; reload it after each save.");
    }

    /// <summary>Lists an item's stored versions.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>Newest first.</returns>
    [McpServerTool(Name = "list_versions", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List a post's or page's stored versions, newest first: each version's ID, when it was saved (UTC) and whether it's the current one. Every save, in /admin or by an app, keeps one. Use get_version_diff to see what changed.")]
    public async Task<IReadOnlyList<EditorVersion>> ListVersionsAsync(
        [Description("The item's path, such as posts/hello-world.md.")] string path,
        CancellationToken cancellationToken)
    {
        var stored = await OpenAsync(path, cancellationToken);
        return [.. (await editor.RevisionsAsync(stored.Path, cancellationToken)).Select(r =>
            new EditorVersion(r.VersionId, r.SavedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), r.IsCurrent))];
    }

    /// <summary>Compares two versions of an item.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="from">The older version.</param>
    /// <param name="to">The newer version; the current one when left out.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The changed lines with three around each.</returns>
    [McpServerTool(Name = "get_version_diff", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Show what changed in a post or page between two versions from list_versions: the changed lines of the whole file (front matter and text) with three unchanged lines around each, as a unified diff: '+ ' added, '- ' removed, '  ' unchanged, '…' for lines left out. Leave out 'to' to compare with the current version.")]
    public async Task<string> GetVersionDiffAsync(
        [Description("The item's path, such as posts/hello-world.md.")] string path,
        [Description("The older version's ID, from list_versions.")] string from,
        [Description("The newer version's ID; leave it out for the current version.")] string? to = null,
        CancellationToken cancellationToken = default)
    {
        var stored = await OpenAsync(path, cancellationToken);
        var before = await editor.OpenRevisionAsync(stored.Path, from, cancellationToken)
            ?? throw new McpException($"No version {from} of {path}: take an ID from list_versions.");
        var after = string.IsNullOrWhiteSpace(to)
            ? stored.Draft
            : await editor.OpenRevisionAsync(stored.Path, to, cancellationToken) ?? throw new McpException($"No version {to} of {path}: take an ID from list_versions.");
        var lines = LineDiff.Compare(before.ToMarkdown(), after.ToMarkdown())
            ?? throw new McpException($"The versions are longer than {LineDiff.MaxLines} lines, too long to compare here; compare them on the item's versions page.");
        return lines.All(l => l.Kind == DiffKind.Same) ? "The two versions are the same." : LineDiff.Text(LineDiff.Around(lines, 3));
    }

    /// <summary>Describes the Markdown a post or page may use (F-0136).</summary>
    /// <returns>The guide, as Markdown.</returns>
    [McpServerTool(Name = "markdown_guide", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The Markdown posts and pages may use on this site: headings and their links, lists, tables, footnotes and the other extensions, text styles, links, images and files from Media with alt text and captions, and code blocks with the languages the site highlights and an optional file name. Read it before writing a post.")]
    public string MarkdownGuideTool() => MarkdownGuide.Text(content.Current.Site.Name, highlighter.Languages);

    /// <summary>
    /// Lists the tags posts carry, so an app reuses them instead of inventing near-duplicates
    /// (T-0140): those of published posts, of the unpublished posts the editor may change (an author
    /// doesn't learn another's draft topics), and those set up on the tags page.
    /// </summary>
    /// <returns>The tags, most used first, then by name.</returns>
    [McpServerTool(Name = "list_tags", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the tags the site's posts use, most used first: each tag's name, how many published posts carry it (posts), how many of your drafts, scheduled and archived posts do (notPublic), and its colour and description from the tags page. Tags are matched exactly, case included. When tagging a post, reuse these names; add a new tag only when none of them fits.")]
    public IReadOnlyList<EditorTag> ListTags()
    {
        var user = User();
        var catalog = content.Current;
        var notPublic = catalog.Unpublished
            .Where(i => i.Kind == ContentKind.Post && EditorRights.CanEdit(user, i.Kind, i.AuthorId, isNew: false))
            .SelectMany(i => i.Tags)
            .CountBy(t => t, StringComparer.Ordinal)
            .ToDictionary(StringComparer.Ordinal);
        return [.. catalog.Tags.Keys.Concat(notPublic.Keys).Concat(catalog.Site.Tags.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(t => new EditorTag(t, catalog.Tags.GetValueOrDefault(t)?.Count ?? 0, notPublic.GetValueOrDefault(t), catalog.Site.ColourOf(t), catalog.Site.DescriptionOf(t)))
            .OrderByDescending(t => t.Posts + t.NotPublic)
            .ThenBy(t => t.Name, StringComparer.Ordinal)];
    }

    /// <summary>Lists the files in Media.</summary>
    /// <param name="query">Only files whose name contains this.</param>
    /// <param name="limit">The most files to return.</param>
    /// <param name="offset">How many matching files to skip, for the next page.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>A page of the files.</returns>
    [McpServerTool(Name = "list_media", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("List the images and files in Media that can go in a post: name, address, size of images, and the Markdown that shows each (replace an image's placeholder alt text with what it shows). Returns a page: total says how many match, and nextOffset, when there are more, is the offset to ask for next.")]
    public async Task<EditorMediaPage> ListMediaAsync(
        [Description("Only files whose name contains this text.")] string? query = null,
        [Description("The most files to return, 1 to 200.")] int limit = 50,
        [Description("How many matching files to skip: the nextOffset of the previous page.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var files = await media.ListAsync(cancellationToken);
        var index = content.Current.Media;
        var matching = files
            .Where(f => !f.IsArchived && (string.IsNullOrWhiteSpace(query) || f.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var (skip, take) = Page(offset, limit);
        List<EditorMedia> page = [.. matching
            .Skip(skip)
            .Take(take)
            .Select(f => MediaLinks.Describe(f, index, null))
            .Select(l => new EditorMedia(l.Name, l.Path, l.IsImage, l.Width, l.Height, l.Markdown))];
        return new EditorMediaPage(page, matching.Count, NextOffset(skip, page.Count, matching.Count));
    }

    /// <summary>Creates a post or page, always as a draft.</summary>
    /// <param name="markdown">The file: front matter and text.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Where it was saved.</returns>
    [McpServerTool(Name = "create_item", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Create a post or page as a draft, from a Markdown file with YAML front matter like get_item returns: title, seo_title (optional: a shorter title for the browser tab and search results, which the site's name follows; the heading keeps the title), slug (a page may leave it out: a special page, which Site settings show as the privacy policy, the terms or the contact form's text), kind (post or page; authors only post), date (yyyy-MM-dd, posts), tags, summary (at most 160 characters: search engines show about that many), lang (a code such as hu, only when the item isn't in the site's language), image and image_alt (the picture link previews show, a /media/ address, and what it shows), then the text (at most 100,000 characters). Images need alt text: ![what it shows](/media/...). markdown_guide lists the Markdown the site supports. It stays a draft whatever the front matter says; use set_status to publish or schedule it.")]
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
        return await SaveAsync(null, draft, null, "mcp-item-created", cancellationToken, ContentParser.BodyLine(markdown));
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
        return await SaveAsync(path, draft, version, "mcp-item-saved", cancellationToken, ContentParser.BodyLine(markdown));
    }

    /// <summary>Publishes, schedules, unpublishes or archives a post or page.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="version">The version last read or saved.</param>
    /// <param name="status">The new status.</param>
    /// <param name="publishAt">When a scheduled item goes public.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The new version and status.</returns>
    [McpServerTool(Name = "set_status", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Change a post's or page's status: published (public now, in feeds and search), scheduled (public at publishAt), draft (seen only in /admin and its previews) or archived (off the site, kept in /admin). Saved only if nobody saved it since the version you give. Only for apps allowed to publish; others get an error that says so.")]
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

    /// <summary>Adds tags to a post or page and removes others, leaving the rest of it as it is (T-0186).</summary>
    /// <param name="path">Its path.</param>
    /// <param name="add">Tags to add.</param>
    /// <param name="remove">Tags to remove.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The tags now and the new version.</returns>
    [McpServerTool(Name = "set_tags", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Add tags to a post or page and remove others, without sending the file again: the title, text and every other field stay as they are. Applied to the current version, so it needs no get_item and no version, and keeps a tag someone else added meanwhile. Tags are lowercase slugs (a leading # is dropped); reuse the ones list_tags gives, and an item has at most 20. Removing a tag the item doesn't have changes nothing. Returns the item's tags now, and newTags: those no other item carries, to check for typos. An app allowed drafts only can change drafts only.")]
    public async Task<EditorTagsSaved> SetTagsAsync(
        [Description("The item's path, such as posts/hello-world.md.")] string path,
        [Description("Tags to add, such as [\"travel\", \"photography\"].")] string[]? add = null,
        [Description("Tags to remove.")] string[]? remove = null,
        CancellationToken cancellationToken = default)
    {
        var adding = TagList(add);
        var removing = TagList(remove);
        if (adding.Count == 0 && removing.Count == 0)
        {
            throw new McpException("Give tags to add or to remove.");
        }

        if (adding.Intersect(removing, StringComparer.Ordinal).FirstOrDefault() is { } both)
        {
            throw new McpException($"The tag {both} is both added and removed: give it once.");
        }

        if (adding.FirstOrDefault(t => !Slugs.IsValid(t)) is { } bad)
        {
            throw new McpException($"The tag {bad} isn't a slug: use lowercase letters, digits and single hyphens, such as street-photography.");
        }

        // At the version just read; if someone saves in between, read again and apply the change to theirs.
        for (var attempt = 1; ; attempt++)
        {
            var stored = await OpenAsync(path, cancellationToken);
            if (!Has(User(), Agents.OAuth.EditorBearerHandler.EditClaim) && stored.Draft.Status != ContentStatus.Draft)
            {
                throw new McpException(DraftsOnly);
            }

            List<string> tags = [.. stored.Draft.Tags.Where(t => !removing.Contains(t)), .. adding.Where(t => !stored.Draft.Tags.Contains(t))];
            var saved = await TrySaveAsync(path, stored.Draft with { Tags = tags }, stored.ETag, "mcp-tags-saved", cancellationToken);
            if (saved is null)
            {
                if (attempt < 3)
                {
                    continue;
                }

                throw new McpException(Conflict);
            }

            var inUse = content.Current.AllItems.Where(i => i.SourcePath != saved.Path).SelectMany(i => i.Tags).ToHashSet(StringComparer.Ordinal);
            return new EditorTagsSaved(saved.Path, saved.Version, tags, [.. tags.Where(t => !inUse.Contains(t))], saved.Unchanged);
        }
    }

    // Tags as given: trimmed, without a leading #, in lowercase as the editor writes them, once each.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "Tags are lowercase slugs by definition.")]
    private static List<string> TagList(string[]? tags) =>
        [.. (tags ?? []).Select(t => (t ?? "").Trim().TrimStart('#').ToLowerInvariant()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>Uploads a file to Media.</summary>
    /// <param name="fileName">Its name, with an extension Media accepts.</param>
    /// <param name="contentBase64">Its bytes, base64.</param>
    /// <param name="alt">What an image shows.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The stored file and the Markdown that shows it.</returns>
    [McpServerTool(Name = "upload_media", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Upload an image (JPEG, PNG, WebP) or a file (PDF, SVG, text, Markdown, CSV, JSON, YAML, TOML, ZIP) of up to 10 MB to Media, as base64. Every character of the base64 passes through you, so use it for tiny files only; for anything larger, use create_media_upload if you can run commands that reach this site. Images lose their metadata (camera, location) and get smaller WebP copies. Returns the Markdown to put in a post; an image needs alt, a description of what it shows.")]
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

        var data = Decode(contentBase64, "Upload");
        var outcome = await media.UploadAsync(fileName, data, cancellationToken, alt, replaceDescription: user.IsInRole(nameof(EditorRole.Admin)));
        if (outcome is not MediaUploaded uploaded)
        {
            throw new McpException(((MediaRejected)outcome).Reason);
        }

        await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Email(user), "mcp-media-uploaded", Via(user, uploaded.Item.Name)), CancellationToken.None);
        var link = MediaLinks.Describe(uploaded.Item, content.Current.Media, alt);
        return new EditorMedia(link.Name, link.Path, link.IsImage, link.Width, link.Height, link.Markdown);
    }

    /// <summary>Makes a one-time upload address (T-0137), so a file's bytes needn't pass through the model.</summary>
    /// <param name="fileName">The file name, with its extension.</param>
    /// <param name="alt">What an image shows.</param>
    /// <param name="replaces">The stored file the upload replaces (T-0138), or empty for a new file.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>The address, the header to send, and a curl command.</returns>
    /// <remarks>Destructive: an address can replace a file, which changes posts and archives the old file.</remarks>
    [McpServerTool(Name = "create_media_upload", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Get a one-time address to upload an image (JPEG, PNG, WebP) or a file (PDF, SVG, text, Markdown, CSV, JSON, YAML, TOML, ZIP) of up to 20 MB to Media without passing its bytes through this conversation: send the file with an HTTP PUT from a shell, such as the curl command returned, with its Authorization header. Use it for anything but tiny files when you can run commands that reach this site; otherwise use upload_media. The address works once, for 10 minutes, and stores the file under this file name; the PUT's answer is the stored file and its Markdown, as from upload_media. Images lose their metadata (camera, location) and get smaller WebP copies; an image needs alt, a description of what it shows. With replaces, the file replaces a stored one as replace_media does, under its rules, and the PUT's answer is replace_media's.")]
    public async Task<EditorUploadAddress> CreateMediaUploadAsync(
        [Description("The file name with its extension, such as harbour-at-dusk.jpg.")] string fileName,
        [Description("For images: what it shows, for people who can't see it. When replacing, leave it out to keep the old description.")] string? alt = null,
        [Description("To replace a stored file instead of adding one: its name from list_media, such as 1a2b3c4d5e6f-chart.png. Admins only; an app allowed drafts only can do it only when drafts alone use the file. Never a file Site settings use (the logo, favicon, banner or home page photo): the editor changes those in /admin.")] string? replaces = null,
        CancellationToken cancellationToken = default) =>
        await uploads.CreateAsync(User(), http.HttpContext!.Request, fileName, alt, replaces, cancellationToken);

    /// <summary>Replaces a media file (F-0143): a new address, and every item that used the old one follows.</summary>
    /// <param name="name">The stored file's name.</param>
    /// <param name="fileName">The new file's name, with its extension.</param>
    /// <param name="contentBase64">The new file's bytes, base64-encoded.</param>
    /// <param name="alt">An image's description; left out, the old one's stays.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>The new file and the items changed.</returns>
    [McpServerTool(Name = "replace_media", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Replace a file in Media with a new one, such as an image you're still working on: the new file gets its own address (stored files never change, as browsers keep them for a year), every post and page that used the old address is saved with the new one as a new version, and the old file is archived. An image is replaced by an image. Admins only; an app allowed drafts only can do it only when drafts alone use the file. Never a file Site settings use (the logo, favicon, banner or home page photo): the editor changes those in /admin. Up to 10 MB, sent as base64, so tiny files only: for anything larger, use create_media_upload with replaces if you can run commands that reach this site.")]
    public async Task<EditorReplaced> ReplaceMediaAsync(
        [Description("The stored file's name, from list_media, such as 1a2b3c4d5e6f-chart.png.")] string name,
        [Description("The new file's name with its extension, such as chart.png.")] string fileName,
        [Description("The new file's bytes, base64-encoded.")] string contentBase64,
        [Description("For images: what the new one shows; leave it out to keep the old description.")] string? alt = null,
        CancellationToken cancellationToken = default)
    {
        var user = User();
        if (MediaUploads.ReplaceRefusal(content.Current, user.IsInRole(nameof(EditorRole.Admin)), Has(user, Agents.OAuth.EditorBearerHandler.EditClaim), name) is { } refusal)
        {
            throw new McpException(refusal);
        }

        var data = Decode(contentBase64, "Replace");
        switch (await replacer.ReplaceAsync(name, fileName, data, alt, cancellationToken))
        {
            case ReplaceOutcome.Replaced replaced:
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Email(user), "mcp-media-replaced", Via(user, $"{name} → {replaced.Item.Name} ({replaced.Updated.Count} items)")), CancellationToken.None);
                return MediaUploads.Describe(replaced, name, content.Current.Media);
            case ReplaceOutcome.Refused refused:
                throw new McpException(refused.Reason);
            default:
                throw new McpException("The file wasn't replaced.");
        }
    }

    // A file sent as base64 to upload_media or replace_media, refused before decoding when it's
    // too long (base64 takes four characters for every three bytes), and when it's empty.
    private static byte[] Decode(string contentBase64, string verb)
    {
        if (contentBase64.Length > ((MaxUploadBytes + 2) / 3 * 4) + 4)
        {
            throw new McpException($"The file is larger than 10 MB. {verb} it in /admin, under Media, instead.");
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

        return data.Length == 0 ? throw new McpException("The file is empty.")
            : data.Length > MaxUploadBytes ? throw new McpException($"The file is larger than 10 MB. {verb} it in /admin, under Media, instead.")
            : data;
    }

    // What the editor let the app do (the consent, F-0070): drafts only can't change what visitors
    // see; edits can change any item but not its status.
    internal const string DraftsOnly =
        "This app may write drafts only: it can't change published, scheduled or archived items, or any item's status. Do that in /admin, or connect the app again and allow edits or publishing.";

    // A published special page that Site settings show nowhere (T-0159, F-0233).
    private const string NotPlaced =
        "This page has no slug, so it's a special page: it has no public address until Site settings in /admin use it as the home page, the privacy policy, the terms or the contact page. Until then, previewUrl shows it to editors.";

    private const string Conflict =
        "Someone saved this item since the version you have: get_item again, then make your change to the new version.";

    private const string EditsOnly =
        "This app may edit but not publish: it can't change any item's status. Publish, schedule, unpublish or archive in /admin, or connect the app again and allow publishing.";

    private static bool Has(ClaimsPrincipal user, string claim) => user.HasClaim(c => c.Type == claim);

    // A page of a list: the offset is never negative, and the limit is 1 to MaxLimit.
    private static (int Skip, int Take) Page(int offset, int limit) => (Math.Max(offset, 0), Math.Clamp(limit, 1, MaxLimit));

    // Where the next page starts, or null when this one reached the end.
    private static int? NextOffset(int skip, int count, int total) => skip + count < total ? skip + count : null;

    // The target of an audit entry: what changed, and the app that changed it.
    private static string Via(ClaimsPrincipal user, string target) =>
        $"{target} (via {user.FindFirstValue(Agents.OAuth.EditorBearerHandler.ClientClaim) ?? "an app"})";

    private static string Email(ClaimsPrincipal user) => EditorClaims.Email(user);

    private DateTimeOffset ScheduleTime(string? publishAt) =>
        DateTimeOffset.TryParse(publishAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
        && publishAt!.Length > 10 && (publishAt is [.., 'Z'] || publishAt[^6] is '+' or '-')
            ? at > time.GetUtcNow() ? at : throw new McpException("publishAt is in the past: use status published to publish now.")
            : throw new McpException("A scheduled item needs a publish time (publishAt), a time with an offset, such as 2026-12-01T09:00:00Z.");

    // A file parsed as the public site does, so a save can't store what the site would skip; one
    // without lang keeps none, and follows the site's language (U-39).
    private static ContentDraft Parse(string path, string markdown)
    {
        var result = ContentDraft.Read(path, markdown ?? "");
        return result.Item is { } item
            ? ContentDraft.From(item)
            : throw new McpException(result.Error is { } error ? ContentParser.Reason(error) : "The file couldn't be read.");
    }

    private async Task<OpenedItem> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var opened = await editor.OpenAsync(path, cancellationToken);
        return opened is not null && EditorRights.CanEdit(User(), opened.Draft.Kind, opened.Draft.AuthorId, isNew: false)
            ? opened
            : throw new McpException($"No post or page you may edit at {path}.");
    }

    // firstLine: where the text starts in the file the app sent, so problems name its lines.
    private async Task<EditorSaved> SaveAsync(string? path, ContentDraft draft, string? version, string action, CancellationToken cancellationToken, int firstLine = 1) =>
        await TrySaveAsync(path, draft, version, action, cancellationToken, firstLine) ?? throw new McpException(Conflict);

    // As SaveAsync, but null when someone saved the item since the version given.
    private async Task<EditorSaved?> TrySaveAsync(string? path, ContentDraft draft, string? version, string action, CancellationToken cancellationToken, int firstLine = 1)
    {
        // An item that exists is saved at a version: a blank one would skip the check (F-0115).
        if (path is not null && string.IsNullOrWhiteSpace(version))
        {
            throw new McpException("Give the version from get_item (or from your last save).");
        }

        var user = User();
        // Another item's title and its earlier addresses only for an editor who may change it (T-0177).
        switch (await editor.SaveAsync(path, draft, version, firstLine, i => EditorRights.CanEdit(user, i.Kind, i.AuthorId, isNew: false), cancellationToken))
        {
            case ContentSaved { Unchanged: true } same:
                // Nothing written, so nothing to audit (F-0174).
                return Describe(same);
            case ContentSaved saved:
                // Saved: the entry isn't stopped with the request, or the app would see an error for
                // a save that happened, and try again (F-0115).
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), Email(user), action, Via(user, saved.Path)), CancellationToken.None);
                return Describe(saved);
            case ContentConflict:
                return null;
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
        if (catalog.PublishedItems.FirstOrDefault(i => i.SourcePath == saved.Path) is { } item)
        {
            // A special page Site settings don't place is published but has no address, not "/" (F-0233).
            return Seo.ContentUrls.PathOf(item) is { Length: > 0 } path
                ? new EditorSaved(saved.Path, saved.ETag, StatusOf(item), urls.Absolute(request, path), null, saved.Unchanged)
                : new EditorSaved(saved.Path, saved.ETag, StatusOf(item), null, urls.Absolute(request, Components.Pages.Preview.UrlOf(item)), saved.Unchanged, NotPlaced);
        }

        return catalog.Unpublished.FirstOrDefault(i => i.SourcePath == saved.Path) is { } waiting
            ? new EditorSaved(saved.Path, saved.ETag, StatusOf(waiting), null, waiting.Status == ContentStatus.Archived ? null : urls.Absolute(request, Components.Pages.Preview.UrlOf(waiting)), saved.Unchanged)
            : new EditorSaved(saved.Path, saved.ETag, "draft", null, null, saved.Unchanged);
    }

    // The editor the token belongs to: EditorBearerHandler signed them in for this request.
    // Only an app an editor allowed, with its bearer token: the tools are served at /mcp/editor
    // alone (Names), and this keeps it so even for a tool left off that list, which /mcp would
    // otherwise run under a signed-in editor's cookie (T-0122).
    private ClaimsPrincipal User() =>
        http.HttpContext?.User is { Identity.IsAuthenticated: true } user
            && user.HasClaim(c => c.Type == Agents.OAuth.EditorBearerHandler.GrantClaim)
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
