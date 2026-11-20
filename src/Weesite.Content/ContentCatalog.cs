using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Weesite.Content.Media;

namespace Weesite.Content;

/// <summary>
/// Everything the public site shows, in memory: posts newest first, pages, tags, redirects and
/// the site settings. Immutable: a reload builds a new catalog.
/// </summary>
public sealed partial class ContentCatalog
{
    private readonly FrozenDictionary<string, ContentItem> _posts;
    private readonly FrozenDictionary<string, ContentItem> _pages;
    private readonly FrozenDictionary<string, string> _postRedirects;
    private readonly FrozenDictionary<string, string> _pageRedirects;
    private FrozenDictionary<string, IReadOnlyList<ContentItem>>? _mediaUses;

    private ContentCatalog(
        SiteSettings site,
        IReadOnlyList<ContentItem> posts,
        IReadOnlyList<ContentItem> pages,
        IReadOnlyList<ContentItem> specialPages,
        IReadOnlyList<string> problems,
        MediaIndex media,
        IReadOnlyList<ContentItem> unpublished)
    {
        Site = site;
        Media = media;
        Unpublished = unpublished;
        NextPublishAt = unpublished
            .Where(i => i.Status == ContentStatus.Scheduled && i.PublishAt is not null)
            .Min(i => i.PublishAt);
        Posts = posts;
        Pages = pages;
        SpecialPages = specialPages;
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
    public static ContentCatalog Empty { get; } = new(SiteSettings.Empty, [], [], [], [], MediaIndex.Empty, []);

    /// <summary>Gets the site settings.</summary>
    public SiteSettings Site { get; }

    /// <summary>Gets the sizes and variants of the images in the media store.</summary>
    public MediaIndex Media { get; }

    /// <summary>Gets the posts, newest first.</summary>
    public IReadOnlyList<ContentItem> Posts { get; }

    /// <summary>Gets the content pages, by title: the ones with a slug, each at its own address.</summary>
    public IReadOnlyList<ContentItem> Pages { get; }

    /// <summary>Gets the published special pages, by title: pages without a slug, which Site settings
    /// show as the privacy policy, the terms or the contact form's text (T-0159). Drafts are in
    /// <see cref="Unpublished"/>.</summary>
    public IReadOnlyList<ContentItem> SpecialPages { get; }

    /// <summary>Gets the posts per tag, newest first.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ContentItem>> Tags { get; }

    /// <summary>Gets the drafts and the scheduled items not yet due: for /admin, never the public site.</summary>
    public IReadOnlyList<ContentItem> Unpublished { get; }

    /// <summary>Gets when the next scheduled item becomes public, if any: the catalog is rebuilt then.</summary>
    public DateTimeOffset? NextPublishAt { get; }

    /// <summary>Gets what was skipped while building the catalog, and why.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Builds a catalog from parsed items, skipping duplicates and reserved slugs.</summary>
    /// <param name="site">The site settings.</param>
    /// <param name="results">The parse results of every content file.</param>
    /// <param name="media">The media index, if images were indexed.</param>
    /// <param name="now">The moment that decides which scheduled items are public (default: now).</param>
    /// <returns>The catalog; skipped items are listed in <see cref="Problems"/>.</returns>
    public static ContentCatalog Build(SiteSettings site, IEnumerable<ParseResult> results, MediaIndex? media = null, DateTimeOffset? now = null)
    {
        var moment = now ?? DateTimeOffset.UtcNow;
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(results);

        var problems = new List<string>();
        var posts = new Dictionary<string, ContentItem>(StringComparer.Ordinal);
        var pages = new Dictionary<string, ContentItem>(StringComparer.Ordinal);
        var special = new List<ContentItem>();

        foreach (var result in results)
        {
            if (result.Item is not { } item)
            {
                problems.Add(result.Error ?? "unknown error");
                continue;
            }

            // A special page has no slug to clash (T-0159). The special addresses were free slugs
            // before: a page that has one still loads, so an editor can take its slug away.
            if (item.IsSpecial)
            {
                special.Add(item);
                continue;
            }

            var target = item.Kind == ContentKind.Post ? posts : pages;
            if ((item.Kind == ContentKind.Page && Slugs.Reserved.Contains(item.Slug) && !Slugs.SpecialAddresses.Contains(item.Slug))
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
        // Slugs are unique across every item, drafts included; only public items reach the public views.
        // Where Site settings show each special page (T-0159).
        string? AddressOf(ContentItem page) =>
            site.Home.Show != "latest-posts" && page.SourcePath == site.Home.Page ? ""
            : page.SourcePath == site.PrivacyPage ? "privacy"
            : page.SourcePath == site.TermsPage ? "terms"
            : page.SourcePath == site.ContactForm?.Page ? "contact"
            : null;
        var orderedSpecial = special
            .Select(p => p with { SpecialAddress = AddressOf(p) })
            .OrderBy(p => p.Title, StringComparer.CurrentCulture)
            .ToList();
        var unpublished = orderedPosts.Concat(orderedPages).Concat(orderedSpecial).Where(i => !i.IsPublicAt(moment)).ToList();
        return new ContentCatalog(
            site,
            [.. orderedPosts.Where(i => i.IsPublicAt(moment))],
            [.. orderedPages.Where(i => i.IsPublicAt(moment))],
            [.. orderedSpecial.Where(i => i.IsPublicAt(moment))],
            problems,
            media ?? MediaIndex.Empty,
            unpublished);
    }

    /// <summary>
    /// Gets the posts and pages, drafts and scheduled ones included, that use each media file
    /// (T-0112): a <c>/media/</c> address in their text or as their image. Keyed by the file's
    /// name; files nothing uses aren't in it. Worked out on first use, once per catalog.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ContentItem>> MediaUses => _mediaUses ??= FindMediaUses();

    /// <summary>Gets the posts and pages that use a media file.</summary>
    /// <param name="name">The file's name, such as <c>0e3d1bf78702-diagram.svg</c>.</param>
    /// <returns>The items, posts first; empty when nothing uses it.</returns>
    public IReadOnlyList<ContentItem> UsesOf(string name) => MediaUses.GetValueOrDefault(name) ?? [];

    /// <summary>Gets whether an archived post or page has this slug (T-0111): its address answers 410 Gone.</summary>
    /// <param name="kind">Post or page.</param>
    /// <param name="slug">The slug.</param>
    /// <returns>True when it's archived.</returns>
    public bool IsArchived(ContentKind kind, string slug) =>
        Unpublished.Any(i => i.Kind == kind && i.Status == ContentStatus.Archived && i.Slug == slug);

    /// <summary>Gets the posts and pages that use a media file, archived ones left out (T-0111, T-0112).</summary>
    /// <param name="name">The file's name.</param>
    /// <returns>The items.</returns>
    public IReadOnlyList<ContentItem> ActiveUsesOf(string name) => [.. UsesOf(name).Where(i => i.Status != ContentStatus.Archived)];

    /// <summary>Finds a post by slug.</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>The post, or null.</returns>
    public ContentItem? FindPost(string slug) => _posts.GetValueOrDefault(slug);

    /// <summary>Finds a page by slug.</summary>
    /// <param name="slug">The slug.</param>
    /// <returns>The page, or null.</returns>
    public ContentItem? FindPage(string slug) => _pages.GetValueOrDefault(slug);

    /// <summary>Finds a published special page by its file (T-0159).</summary>
    /// <param name="path">Its source path, such as <c>pages/privacy.md</c>.</param>
    /// <returns>The page, or null when there's none or it isn't published.</returns>
    public ContentItem? FindSpecialPage(string? path) =>
        string.IsNullOrEmpty(path) ? null : SpecialPages.FirstOrDefault(p => string.Equals(p.SourcePath, path, StringComparison.Ordinal));

    /// <summary>Gets every post and page, special pages, drafts and scheduled ones included: what the admin and the editor's tools work on.</summary>
    public IEnumerable<ContentItem> AllItems => Posts.Concat(Pages).Concat(SpecialPages).Concat(Unpublished);

    /// <summary>Gets the published posts and pages the site shows at an address: special pages only
    /// where Site settings place them (T-0159). What search and the sitemap take.</summary>
    public IEnumerable<ContentItem> PublicItems => Posts.Concat(Pages).Concat(SpecialPages.Where(p => p.SpecialAddress is not null));

    /// <summary>Gets the published posts and pages, special pages included.</summary>
    public IEnumerable<ContentItem> PublishedItems => Posts.Concat(Pages).Concat(SpecialPages);

    /// <summary>Gets every special page, drafts and scheduled ones included, by title: what Site settings choose from.</summary>
    public IReadOnlyList<ContentItem> AllSpecialPages =>
        [.. SpecialPages.Concat(Unpublished.Where(i => i.IsSpecial)).OrderBy(p => p.Title, StringComparer.CurrentCulture)];

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

    private FrozenDictionary<string, IReadOnlyList<ContentItem>> FindMediaUses()
    {
        var uses = new Dictionary<string, List<ContentItem>>(StringComparer.Ordinal);
        foreach (var item in Posts.Concat(Pages).Concat(SpecialPages).Concat(Unpublished).OrderBy(i => i.Kind))
        {
            var names = MediaAddress().Matches(item.Markdown).Select(m => m.Groups[1].Value);
            if (item.Image is { } image && MediaAddress().Match(image) is { Success: true } match)
            {
                names = names.Append(match.Groups[1].Value);
            }

            foreach (var name in names.Distinct(StringComparer.Ordinal))
            {
                if (!uses.TryGetValue(name, out var list))
                {
                    list = [];
                    uses[name] = list;
                }

                list.Add(item);
            }
        }

        return uses.ToFrozenDictionary(u => u.Key, u => (IReadOnlyList<ContentItem>)u.Value, StringComparer.Ordinal);
    }

    // A stored file's address, as the media endpoint serves them: /media/{hash}-{name}.
    [GeneratedRegex(@"/media/([0-9a-f]{8,64}-[a-z0-9][a-z0-9._-]{0,200}[a-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex MediaAddress();
}
