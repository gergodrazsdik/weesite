using System.Collections.Frozen;
using Weesite.Content.Media;

namespace Weesite.Content;

/// <summary>
/// Everything the public site shows, in memory: posts newest first, pages, tags, redirects and
/// the site settings. Immutable: a reload builds a new catalog.
/// </summary>
public sealed class ContentCatalog
{
    private readonly FrozenDictionary<string, ContentItem> _posts;
    private readonly FrozenDictionary<string, ContentItem> _pages;
    private readonly FrozenDictionary<string, string> _postRedirects;
    private readonly FrozenDictionary<string, string> _pageRedirects;

    private ContentCatalog(
        SiteSettings site,
        IReadOnlyList<ContentItem> posts,
        IReadOnlyList<ContentItem> pages,
        IReadOnlyList<string> problems,
        MediaIndex media)
    {
        Site = site;
        Media = media;
        Posts = posts;
        Pages = pages;
        Problems = problems;
        _posts = posts.ToFrozenDictionary(p => p.Slug, StringComparer.Ordinal);
        _pages = pages.ToFrozenDictionary(p => p.Slug, StringComparer.Ordinal);
        _postRedirects = Redirects(posts, _posts.Keys);
        _pageRedirects = Redirects(pages, _pages.Keys);
        Tags = posts
            .SelectMany(p => p.Tags.Select(t => (Tag: t, Post: p)))
            .GroupBy(x => x.Tag, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => (IReadOnlyList<ContentItem>)g.Select(x => x.Post).ToList(), StringComparer.Ordinal);
    }

    /// <summary>Gets an empty catalog, before content is loaded.</summary>
    public static ContentCatalog Empty { get; } = new(SiteSettings.Empty, [], [], [], MediaIndex.Empty);

    /// <summary>Gets the site settings.</summary>
    public SiteSettings Site { get; }

    /// <summary>Gets the sizes and variants of the images in the media store.</summary>
    public MediaIndex Media { get; }

    /// <summary>Gets the posts, newest first.</summary>
    public IReadOnlyList<ContentItem> Posts { get; }

    /// <summary>Gets the content pages, by title.</summary>
    public IReadOnlyList<ContentItem> Pages { get; }

    /// <summary>Gets the posts per tag, newest first.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ContentItem>> Tags { get; }

    /// <summary>Gets what was skipped while building the catalog, and why.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Builds a catalog from parsed items, skipping duplicates and reserved slugs.</summary>
    /// <param name="site">The site settings.</param>
    /// <param name="results">The parse results of every content file.</param>
    /// <param name="media">The media index, if images were indexed.</param>
    /// <returns>The catalog; skipped items are listed in <see cref="Problems"/>.</returns>
    public static ContentCatalog Build(SiteSettings site, IEnumerable<ParseResult> results, MediaIndex? media = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(results);

        var problems = new List<string>();
        var posts = new Dictionary<string, ContentItem>(StringComparer.Ordinal);
        var pages = new Dictionary<string, ContentItem>(StringComparer.Ordinal);

        foreach (var result in results)
        {
            if (result.Item is not { } item)
            {
                problems.Add(result.Error ?? "unknown error");
                continue;
            }

            var target = item.Kind == ContentKind.Post ? posts : pages;
            if ((item.Kind == ContentKind.Page && Slugs.Reserved.Contains(item.Slug))
                || (item.Kind == ContentKind.Post && Slugs.ReservedForPosts.Contains(item.Slug)))
            {
                problems.Add($"{item.SourcePath}: slug '{item.Slug}' is reserved for the app");
            }
            else if (!target.TryAdd(item.Slug, item))
            {
                problems.Add($"{item.SourcePath}: slug '{item.Slug}' is already used by {target[item.Slug].SourcePath}");
            }
        }

        var orderedPosts = posts.Values
            .OrderByDescending(p => p.Date)
            .ThenBy(p => p.Slug, StringComparer.Ordinal)
            .ToList();
        var orderedPages = pages.Values.OrderBy(p => p.Title, StringComparer.CurrentCulture).ToList();
        return new ContentCatalog(site, orderedPosts, orderedPages, problems, media ?? MediaIndex.Empty);
    }

    /// <summary>Finds a post by slug.</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>The post, or null.</returns>
    public ContentItem? FindPost(string slug) => _posts.GetValueOrDefault(slug);

    /// <summary>Finds a page by slug.</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>The page, or null.</returns>
    public ContentItem? FindPage(string slug) => _pages.GetValueOrDefault(slug);

    /// <summary>Finds the current slug of a post that used to have another.</summary>
    /// <param name="oldSlug">The earlier slug.</param>
    /// <returns>The current slug, or null.</returns>
    public string? FindPostRedirect(string oldSlug) => _postRedirects.GetValueOrDefault(oldSlug);

    /// <summary>Finds the current slug of a page that used to have another.</summary>
    /// <param name="oldSlug">The earlier slug.</param>
    /// <returns>The current slug, or null.</returns>
    public string? FindPageRedirect(string oldSlug) => _pageRedirects.GetValueOrDefault(oldSlug);

    // An earlier slug redirects only if no current item uses it.
    private static FrozenDictionary<string, string> Redirects(IEnumerable<ContentItem> items, IEnumerable<string> current)
    {
        var taken = current.ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            foreach (var previous in item.PreviousSlugs.Where(p => !taken.Contains(p)))
            {
                map.TryAdd(previous, item.Slug);
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
