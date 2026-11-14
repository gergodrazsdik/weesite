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

    /// <summary>Gets the kind.</summary>
    public ContentKind Kind { get; init; } = ContentKind.Post;

    /// <summary>Gets the title.</summary>
    public string Title { get; init; } = "";

    /// <summary>Gets the slug.</summary>
    public string Slug { get; init; } = "";

    /// <summary>Gets the language code.</summary>
    public string Language { get; init; } = "en";

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

    /// <summary>Gets the earlier slugs that redirect here.</summary>
    public IReadOnlyList<string> RedirectFrom { get; init; } = [];

    /// <summary>Gets the Markdown body.</summary>
    public string Body { get; init; } = "";

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
            ["slug"] = Slug.Trim(),
        };
        if (Kind == ContentKind.Page)
        {
            fields["kind"] = "page";
        }

        Add(fields, "date", Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(fields, "updated", Updated?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Add(fields, "author", AuthorId);
        fields["lang"] = Language;
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
