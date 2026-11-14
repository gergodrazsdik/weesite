namespace Weesite.Content;

/// <summary>Whether an item is a dated blog post or an undated content page.</summary>
public enum ContentKind
{
    /// <summary>A blog post: dated, in the feeds, under <c>/blog/{slug}</c>.</summary>
    Post,

    /// <summary>A content page: no date, not in the feeds, at <c>/{slug}</c>.</summary>
    Page,
}

/// <summary>Whether an item is public (ADR 0005).</summary>
public enum ContentStatus
{
    /// <summary>Public. Items without a status are published.</summary>
    Published,

    /// <summary>Not public; editors see it in /admin and through unlisted previews.</summary>
    Draft,

    /// <summary>Public from <see cref="ContentItem.PublishAt"/> on.</summary>
    Scheduled,

    /// <summary>
    /// Off the site (T-0111): its address answers 410 Gone, and it's in no list, feed, search or
    /// sitemap. Editors find it under Archived and bring it back as a draft.
    /// </summary>
    Archived,
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

    /// <summary>Gets a value indicating whether a page appears in the menu.</summary>
    public bool ShowInMenu { get; init; }

    /// <summary>Gets earlier slugs, which redirect (301) to this item.</summary>
    public IReadOnlyList<string> PreviousSlugs { get; init; } = [];

    /// <summary>Gets the path of the item's own image (Open Graph), if any.</summary>
    public string? Image { get; init; }

    /// <summary>Gets the image's alternative text.</summary>
    public string? ImageAlt { get; init; }

    /// <summary>Gets whether the item is published, a draft or scheduled.</summary>
    public ContentStatus Status { get; init; } = ContentStatus.Published;

    /// <summary>Gets when a scheduled item becomes public.</summary>
    public DateTimeOffset? PublishAt { get; init; }

    /// <summary>Gets whether the item is public at a moment: published, or scheduled for then or earlier.</summary>
    /// <param name="now">The moment.</param>
    /// <returns>True when the public site shows it.</returns>
    public bool IsPublicAt(DateTimeOffset now) =>
        Status == ContentStatus.Published || (Status == ContentStatus.Scheduled && PublishAt <= now);

    /// <summary>Gets the rendered HTML (set when the catalog loads).</summary>
    public string Html { get; init; } = "";

    /// <summary>Gets the text without markup, for search, summaries and reading time.</summary>
    public string PlainText { get; init; } = "";

    /// <summary>
    /// Gets a description for search engines and link previews (T-0119): the summary, or else
    /// the start of the text, at most 160 characters and cut at a word. Null for an item with
    /// neither.
    /// </summary>
    public string? Description
    {
        get
        {
            if (Summary is { Length: > 0 } summary)
            {
                return summary;
            }

            var text = string.Join(' ', PlainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (text.Length <= 160)
            {
                return text.Length == 0 ? null : text;
            }

            var cut = text.AsSpan(0, 159).LastIndexOf(' ');
            return string.Concat(text.AsSpan(0, cut > 80 ? cut : 158).TrimEnd(",;:-–—".AsSpan()), "…");
        }
    }

    /// <summary>Gets the reading time in whole minutes (at least one), at 220 words a minute.</summary>
    public int ReadingMinutes => MinutesFor(PlainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    /// <summary>Gets the reading time of a number of words, as <see cref="ReadingMinutes"/> counts it.</summary>
    /// <param name="words">The number of words.</param>
    /// <returns>Whole minutes, at least one.</returns>
    public static int MinutesFor(int words) => Math.Max(1, (int)Math.Ceiling(words / 220.0));
}
