using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weesite.Content;

/// <summary>
/// Everything that identifies the site, from <c>settings/site.json</c>. The engine itself names no
/// one; a deployment's identity lives here.
/// </summary>
public sealed record SiteSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
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

    /// <summary>Gets the one-level menu, in order.</summary>
    public IReadOnlyList<MenuItem> Menu { get; init; } = [];

    /// <summary>Gets what the home page shows.</summary>
    public HomeSettings Home { get; init; } = new();

    /// <summary>Gets the site's colour overrides, if any.</summary>
    public ThemeSettings? Theme { get; init; }

    /// <summary>Parses <c>site.json</c>.</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The settings.</returns>
    public static SiteSettings Parse(string json) =>
        JsonSerializer.Deserialize<SiteSettings>(json, JsonOptions) ?? Empty;

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

/// <summary>What the home page shows.</summary>
public sealed record HomeSettings
{
    /// <summary>Gets <c>latest-posts</c>, <c>page</c> or <c>page-and-posts</c>.</summary>
    public string Show { get; init; } = "latest-posts";

    /// <summary>Gets the slug of the page to show, for <c>page</c> and <c>page-and-posts</c>.</summary>
    public string? Page { get; init; }
}
