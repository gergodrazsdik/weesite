namespace Weesite.Content;

/// <summary>Whether an item is a dated blog post or an undated content page.</summary>
public enum ContentKind
{
    /// <summary>A blog post: dated, in the feeds, under <c>/blog/{slug}</c>.</summary>
    Post,

    /// <summary>A content page: no date, not in the feeds, at <c>/{slug}</c>.</summary>
    Page,
}

/// <summary>A post or page, parsed from Markdown with front matter.</summary>
/// <param name="Kind">Post or page.</param>
/// <param name="Slug">The URL segment: lowercase letters, digits and single hyphens.</param>
/// <param name="Title">The title.</param>
/// <param name="Language">The language code, such as <c>en</c>.</param>
/// <param name="Markdown">The body, without front matter.</param>
/// <param name="SourcePath">Where it came from, relative to the content root (for messages).</param>
public sealed record ContentItem(
    ContentKind Kind,
    string Slug,
    string Title,
    string Language,
    string Markdown,
    string SourcePath)
{
    /// <summary>Gets a one-sentence summary for lists, feeds and meta descriptions.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the publication date (posts only).</summary>
    public DateOnly? Date { get; init; }

    /// <summary>Gets the date of the last notable update, if any.</summary>
    public DateOnly? Updated { get; init; }

    /// <summary>Gets the author's ID in the site settings.</summary>
    public string? AuthorId { get; init; }

    /// <summary>Gets the tags, lowercase slugs.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the ID that links translations of the same item.</summary>
    public string? TranslationGroup { get; init; }

    /// <summary>Gets a value indicating whether a page appears in the menu.</summary>
    public bool ShowInMenu { get; init; }

    /// <summary>Gets earlier slugs, which redirect (301) to this item.</summary>
    public IReadOnlyList<string> PreviousSlugs { get; init; } = [];

    /// <summary>Gets the path of the item's own image (Open Graph), if any.</summary>
    public string? Image { get; init; }

    /// <summary>Gets the image's alternative text.</summary>
    public string? ImageAlt { get; init; }

    /// <summary>Gets the rendered HTML (set when the catalog loads).</summary>
    public string Html { get; init; } = "";

    /// <summary>Gets the text without markup, for search, summaries and reading time.</summary>
    public string PlainText { get; init; } = "";
}
