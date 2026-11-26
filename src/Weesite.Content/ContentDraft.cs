using System.Globalization;
using System.Text;
using YamlDotNet.Serialization;

namespace Weesite.Content;

/// <summary>
/// A post or page as the editor edits it: its front matter fields and body. Serializing gives the
/// Markdown file the content store keeps; the same parser as the public site reads it back.
/// </summary>
public sealed record ContentDraft
{
    /// <summary>
    /// The longest summary a save accepts: about what search engines show, and what the editor's
    /// counter counts to (F-0125). Files with longer ones still load.
    /// </summary>
    public const int MaxSummaryLength = 160;

    /// <summary>
    /// The longest text (after the front matter) a post or page may have, in characters: about
    /// three times a 25-minute read. Rendering a longer one could take minutes (T-0122); a file
    /// over it is refused on save and skipped at load.
    /// </summary>
    public const int MaxBodyLength = 100_000;

    /// <summary>
    /// The most tags a save accepts on one item (T-0177): enough to describe a post, few enough
    /// that the site's tag lists stay short. Files with more still load.
    /// </summary>
    public const int MaxTags = 20;

    /// <summary>
    /// The longest <c>&lt;title&gt;</c> element search engines show whole, the site's name included:
    /// Bing flags longer ones. Advice for the editor's counter and the editor tools' preview, not a
    /// limit: a save with a longer title goes through.
    /// </summary>
    public const int TitleTagLength = 70;

    /// <summary>Gets the kind.</summary>
    public ContentKind Kind { get; init; } = ContentKind.Post;

    /// <summary>Gets the title.</summary>
    public string Title { get; init; } = "";

    /// <summary>Gets the shorter title for the browser tab and search results, if any (<c>seo_title</c>).</summary>
    public string? SeoTitle { get; init; }

    /// <summary>Gets the slug.</summary>
    public string Slug { get; init; } = "";

    /// <summary>
    /// Gets the item's own language code; empty when it has none and follows the site's (ADR 0008),
    /// so a change of the site's language changes it too. Written as <c>lang</c> only when set.
    /// </summary>
    public string Language { get; init; } = "";

    /// <summary>Gets the summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the publication date (posts).</summary>
    public DateOnly? Date { get; init; }

    /// <summary>Gets the date of the last notable update.</summary>
    public DateOnly? Updated { get; init; }

    /// <summary>Gets the author's ID.</summary>
    public string? AuthorId { get; init; }

    /// <summary>Gets the tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the status.</summary>
    public ContentStatus Status { get; init; } = ContentStatus.Draft;

    /// <summary>Gets when a scheduled item becomes public.</summary>
    public DateTimeOffset? PublishAt { get; init; }

    /// <summary>Gets the item's own image path.</summary>
    public string? Image { get; init; }

    /// <summary>Gets the image's alternative text.</summary>
    public string? ImageAlt { get; init; }

    /// <summary>Gets whether a page appears in the menu.</summary>
    public bool ShowInMenu { get; init; }

    /// <summary>Gets whether a post's comments are closed (<c>comments: false</c>).</summary>
    public bool CommentsClosed { get; init; }

    /// <summary>Gets the earlier slugs that redirect here.</summary>
    public IReadOnlyList<string> RedirectFrom { get; init; } = [];

    /// <summary>Gets the Markdown body.</summary>
    public string Body { get; init; } = "";

    /// <summary>
    /// Parses a file for editing: as the public site does, except that an item without a language
    /// of its own keeps none rather than taking the site's, so saving it doesn't write one (U-39).
    /// </summary>
    /// <param name="path">The file's path.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>The parsed item, its <see cref="ContentItem.Language"/> empty when the file has no <c>lang</c>, or the error.</returns>
    public static ParseResult Read(string path, string text) => ContentParser.Parse(path, text, defaultLanguage: "");

    /// <summary>Gets a draft of an existing item.</summary>
    /// <param name="item">The item.</param>
    /// <returns>Its fields.</returns>
    public static ContentDraft From(ContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ContentDraft
        {
            Kind = item.Kind,
            Title = item.Title,
            SeoTitle = item.SeoTitle,
            Slug = item.Slug,
            Language = item.Language,
            Summary = item.Summary,
            Date = item.Date,
            Updated = item.Updated,
            AuthorId = item.AuthorId,
            Tags = item.Tags,
            Status = item.Status,
            PublishAt = item.PublishAt,
            Image = item.Image,
            ImageAlt = item.ImageAlt,
            ShowInMenu = item.ShowInMenu,
            CommentsClosed = item.CommentsClosed,
            RedirectFrom = item.PreviousSlugs,
            Body = item.Markdown.Trim(),
        };
    }

    /// <summary>Writes the draft as a Markdown file with YAML front matter.</summary>
    /// <returns>The file's text.</returns>
    public string ToMarkdown()
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["title"] = Title.Trim(),
        };

        Add(fields, "seo_title", SeoTitle?.Trim());

        // A special page has no slug (T-0159).
        Add(fields, "slug", string.IsNullOrWhiteSpace(Slug) ? null : Slug.Trim());
        if (Kind == ContentKind.Page)
        {
            fields["kind"] = "page";
        }

        Add(fields, "date", Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(fields, "updated", Updated?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(fields, "author", AuthorId);
        Add(fields, "lang", Language?.Trim());
        if (Tags.Count > 0)
        {
            fields["tags"] = Tags.ToList();
        }

        Add(fields, "summary", Summary?.Trim());
        if (Status != ContentStatus.Published)
        {
            fields["status"] = Status switch { ContentStatus.Draft => "draft", ContentStatus.Archived => "archived", _ => "scheduled" };
        }

        Add(fields, "publish_at", PublishAt?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        Add(fields, "image", Image);
        Add(fields, "image_alt", ImageAlt?.Trim());
        if (Kind == ContentKind.Page && ShowInMenu)
        {
            fields["menu"] = true;
        }

        if (Kind == ContentKind.Post && CommentsClosed)
        {
            fields["comments"] = false;
        }

        if (RedirectFrom.Count > 0)
        {
            fields["redirect_from"] = RedirectFrom.ToList();
        }

        var yaml = new SerializerBuilder().WithQuotingNecessaryStrings().Build().Serialize(fields);
        var text = new StringBuilder();
        text.Append("---\n").Append(yaml.Replace("\r\n", "\n", StringComparison.Ordinal)).Append("---\n\n");
        text.Append(Body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim()).Append('\n');
        return text.ToString();
    }

    private static void Add(Dictionary<string, object> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[name] = value;
        }
    }
}
