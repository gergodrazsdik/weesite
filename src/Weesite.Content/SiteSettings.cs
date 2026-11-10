using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weesite.Content;

/// <summary>
/// Everything that identifies the site, from <c>settings/site.json</c>. The engine itself names no
/// one; a deployment's identity lives here.
/// </summary>
public sealed record SiteSettings
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Gets the settings of a site that has none yet.</summary>
    public static SiteSettings Empty { get; } = new();

    /// <summary>Gets the site's name.</summary>
    public string Name { get; init; } = "Weesite";

    /// <summary>Gets the tagline under the name.</summary>
    public string? Tagline { get; init; }

    /// <summary>Gets the site's default language code.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Gets the authors posts refer to by ID.</summary>
    public IReadOnlyList<Author> Authors { get; init; } = [];

    /// <summary>Gets the contact email shown in the footer, if any.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>
    /// Gets the site's links (website, profiles elsewhere), shown in the footer with <c>rel="me"</c>.
    /// When there are none, the footer shows the first author's links.
    /// </summary>
    public IReadOnlyList<SiteLink> Links { get; init; } = [];

    /// <summary>Gets the one-level menu, in order.</summary>
    public IReadOnlyList<MenuItem> Menu { get; init; } = [];

    /// <summary>Gets what the home page shows.</summary>
    public HomeSettings Home { get; init; } = new();

    /// <summary>Gets the site's colour overrides, if any.</summary>
    public ThemeSettings? Theme { get; init; }

    /// <summary>
    /// Gets the home page's banner (ADR 0006): empty for the built-in drawing, <c>none</c> for no
    /// banner, or the path of an image in the media library, such as <c>/media/1a2b3c4d5e6f-sea.jpg</c>.
    /// </summary>
    public string? Banner { get; init; }

    /// <summary>Gets the slug of the content page that is the privacy policy, linked from the footer (T-0096).</summary>
    public string? PrivacyPage { get; init; }

    /// <summary>Gets the slug of the content page with the terms of use, linked from the footer (T-0097).</summary>
    public string? TermsPage { get; init; }

    /// <summary>
    /// Gets the footer's copyright line, where <c>{year}</c> becomes the current year, such as
    /// <c>© 2024–{year} Example Author</c>. Empty: <c>© {year}</c> and the site's name.
    /// </summary>
    public string? Copyright { get; init; }

    /// <summary>Gets a value indicating whether the footer leaves out "built with Weesite".</summary>
    public bool HideBuiltWith { get; init; }

    /// <summary>Gets the footer's copyright line for a year.</summary>
    /// <param name="year">The current year.</param>
    /// <returns>The line.</returns>
    public string CopyrightLine(int year)
    {
        var text = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(Copyright) ? $"© {text} {Name}" : Copyright.Replace("{year}", text, StringComparison.Ordinal);
    }

    /// <summary>Gets details of tags, their colours and descriptions, by tag.</summary>
    public IReadOnlyDictionary<string, TagDetails> Tags { get; init; } = new Dictionary<string, TagDetails>(StringComparer.Ordinal);

    /// <summary>
    /// Gets how dates are written for readers: a .NET date format such as <c>yyyy-MM-dd</c> (the
    /// default) or <c>d MMMM yyyy</c>, with month names in the site's language. Machine-readable
    /// dates (<c>datetime</c> attributes, feeds, the sitemap) stay ISO 8601.
    /// </summary>
    public string DateFormat { get; init; } = DefaultDateFormat;

    /// <summary>The date format when <c>site.json</c> sets none: ISO 8601, such as 2026-01-15.</summary>
    public const string DefaultDateFormat = "yyyy-MM-dd";

    /// <summary>Parses <c>site.json</c>.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The settings.</returns>
    public static SiteSettings Parse(string json) =>
        JsonSerializer.Deserialize<SiteSettings>(json, JsonOptions) ?? Empty;

    /// <summary>Writes a date in the site's <see cref="DateFormat"/>; an invalid format falls back to the default.</summary>
    /// <param name="date">The date.</param>
    /// <param name="culture">The language for month and day names.</param>
    /// <returns>Such as <c>2026-01-15</c>.</returns>
    public string FormatDate(DateOnly date, IFormatProvider culture)
    {
        try
        {
            return date.ToString(string.IsNullOrWhiteSpace(DateFormat) ? DefaultDateFormat : DateFormat, culture);
        }
        catch (FormatException)
        {
            return date.ToString(DefaultDateFormat, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Adds the editors' profiles to the authors; an author in <c>site.json</c> with the same ID wins.</summary>
    /// <param name="profiles">The profiles from <c>authors.json</c>.</param>
    /// <returns>The settings with every author.</returns>
    public SiteSettings WithProfiles(IReadOnlyList<Author> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.Count == 0 ? this : this with { Authors = [.. Authors, .. profiles.Where(p => FindAuthor(p.Id) is null)] };
    }

    /// <summary>Gets a tag's colour, if it has one of the <see cref="TagDetails.Colours"/>.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Such as <c>blue</c>, or null.</returns>
    public string? ColourOf(string tag) =>
        Tags.TryGetValue(tag, out var details) && details.Colour is { } colour && TagDetails.Colours.Contains(colour) ? colour : null;

    /// <summary>Gets a tag's description, if it has one.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Such as <c>web pages: markup, CSS and accessibility</c>.</returns>
    public string? DescriptionOf(string tag) =>
        Tags.TryGetValue(tag, out var details) && !string.IsNullOrWhiteSpace(details.Description) ? details.Description : null;

    /// <summary>Finds an author by ID.</summary>
    /// <param name="id">The ID from a post's front matter.</param>
    /// <returns>The author, or null.</returns>
    public Author? FindAuthor(string? id) =>
        id is null ? null : Authors.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
}

/// <summary>An author of posts.</summary>
public sealed record Author
{
    /// <summary>Gets the ID posts use in front matter.</summary>
    public string Id { get; init; } = "";

    /// <summary>Gets the display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Gets a short biography.</summary>
    public string? Bio { get; init; }

    /// <summary>Gets links to profiles elsewhere.</summary>
    public IReadOnlyList<SiteLink> Links { get; init; } = [];
}

/// <summary>A labelled link.</summary>
public sealed record SiteLink
{
    /// <summary>Gets the label.</summary>
    public string Label { get; init; } = "";

    /// <summary>Gets the address.</summary>
    [JsonPropertyName("url")]
    public Uri? Address { get; init; }
}

/// <summary>A menu item: a page by slug, or any address.</summary>
public sealed record MenuItem
{
    /// <summary>Gets the label.</summary>
    public string Label { get; init; } = "";

    /// <summary>Gets the slug of a content page, if the item points to one.</summary>
    public string? Page { get; init; }

    /// <summary>Gets the address, if the item points anywhere else (relative or absolute).</summary>
    public string? Href { get; init; }
}

/// <summary>
/// Colour overrides for the default theme: token name (such as <c>accent</c>) to colour, per
/// mode. Only colours are accepted, never free-form CSS.
/// </summary>
public sealed record ThemeSettings
{
    /// <summary>Gets the light-mode colours.</summary>
    public IReadOnlyDictionary<string, string> Light { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the dark-mode colours.</summary>
    public IReadOnlyDictionary<string, string> Dark { get; init; } = new Dictionary<string, string>();
}

/// <summary>Details of a tag.</summary>
public sealed record TagDetails
{
    /// <summary>Gets the colours a tag may have (ADR 0006); each passes 4.5 : 1 in both modes.</summary>
    public static IReadOnlyList<string> Colours { get; } = ["blue", "violet", "teal", "green", "amber", "orange", "red", "pink", "cyan", "slate"];

    /// <summary>Gets the tag's colour: one of <see cref="Colours"/>, or null for none.</summary>
    public string? Colour { get; init; }

    /// <summary>Gets what the tag is about, shown on its page after the post count, such as <c>web pages: markup, CSS and accessibility</c>.</summary>
    public string? Description { get; init; }
}

/// <summary>What the home page shows.</summary>
public sealed record HomeSettings
{
    /// <summary>Gets <c>latest-posts</c>, <c>page</c> or <c>page-and-posts</c>.</summary>
    public string Show { get; init; } = "latest-posts";

    /// <summary>Gets the slug of the page to show, for <c>page</c> and <c>page-and-posts</c>.</summary>
    public string? Page { get; init; }
}
