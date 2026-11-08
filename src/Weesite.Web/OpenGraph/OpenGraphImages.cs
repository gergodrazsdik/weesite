using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;
using Weesite.Content;
using Weesite.Content.Media;

namespace Weesite.Web.OpenGraph;

/// <summary>An Open Graph image: a path on this site, its size and its alternative text.</summary>
/// <param name="Path">The path, such as <c>/og/{key}.png</c> or <c>/media/{hash}-{name}</c>.</param>
/// <param name="Width">The width in pixels, if known.</param>
/// <param name="Height">The height in pixels, if known.</param>
/// <param name="Alt">The alternative text.</param>
internal sealed record OpenGraphImage(string Path, int? Width, int? Height, string Alt);

/// <summary>
/// Chooses the Open Graph image for a post, page or the site, and serves the generated cards at
/// <c>/og/{key}.png</c> (ADR 0002). A post's or page's own image wins; otherwise a card with its
/// title and the site name. The key changes with the card's content, so responses are immutable.
/// Cards render on the first request and stay in memory.
/// </summary>
internal sealed partial class OpenGraphImages(ContentCatalogProvider content, IMemoryCache cache)
{
    private const string MediaPrefix = "/media/";

    // Per catalog: every card the site can show, by key. A reload builds a new catalog.
    private static readonly ConditionalWeakTable<ContentCatalog, FrozenDictionary<string, CardSpec>> Cards = [];

    /// <summary>Gets the image for an item, or for the site when the item is null.</summary>
    /// <param name="item">The post or page, or null for site-wide pages.</param>
    /// <returns>The image.</returns>
    public OpenGraphImage For(ContentItem? item)
    {
        var catalog = content.Current;
        if (item?.Image is { } image && image.StartsWith(MediaPrefix, StringComparison.Ordinal))
        {
            var info = catalog.Media.Find(image[MediaPrefix.Length..]);
            return new OpenGraphImage(image, info?.Width, info?.Height, item.ImageAlt ?? item.Title);
        }

        var spec = Spec(catalog.Site, item);
        return new OpenGraphImage($"/og/{spec.Key}.png", OpenGraphCard.Width, OpenGraphCard.Height, spec.Title);
    }

    /// <summary>Maps <c>/og/{key}.png</c>.</summary>
    /// <param name="app">The app.</param>
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/og/{name}", (string name, HttpContext context, OpenGraphImages images) => images.Serve(name, context));

    private static CardSpec Spec(SiteSettings site, ContentItem? item)
    {
        var title = item?.Title ?? site.Tagline ?? site.Name;
        var colours = CardColours.For(site);
        return new CardSpec(OpenGraphCard.Key(title, site.Name, colours), title, site.Name, colours);
    }

    private static FrozenDictionary<string, CardSpec> AllCards(ContentCatalog catalog) =>
        Cards.GetValue(catalog, c => c.Posts.Concat(c.Pages).Select(i => Spec(c.Site, i))
            .Append(Spec(c.Site, null))
            .DistinctBy(s => s.Key)
            .ToFrozenDictionary(s => s.Key, StringComparer.Ordinal));

    private IResult Serve(string name, HttpContext context)
    {
        var match = CardName().Match(name);
        if (!match.Success || !AllCards(content.Current).TryGetValue(match.Groups["key"].Value, out var spec))
        {
            return Results.NotFound();
        }

        var png = cache.GetOrCreate($"og:{spec.Key}", entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromDays(1);
            return OpenGraphCard.Render(spec.Title, spec.SiteName, spec.Colours);
        })!;

        var headers = context.Response.Headers;
        headers[HeaderNames.XContentTypeOptions] = "nosniff";
        headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
        return Results.Bytes(png, "image/png", entityTag: new EntityTagHeaderValue($"\"{spec.Key}\""));
    }

    [GeneratedRegex("^(?<key>[0-9a-f]{16})\\.png$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex CardName();

    private sealed record CardSpec(string Key, string Title, string SiteName, CardColours Colours);
}
