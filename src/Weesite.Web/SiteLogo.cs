using System.Collections.Concurrent;
using Microsoft.Net.Http.Headers;
using Weesite.Content;
using Weesite.Content.Media;
using Weesite.Content.Sources;

namespace Weesite.Web;

/// <summary>
/// The site's own logo (T-0116) and favicon (T-0118), chosen in Site settings from the media library. An SVG is used as
/// it is; every size the site needs as PNG (the tab and home-screen icons, the sign-in logos) is
/// drawn from it at <c>/site-icon/{key}/{size}.png</c>, where the key is the file's content hash,
/// so a new logo gets new addresses and each one can be cached for a year. Without a logo, the
/// built-in one in <c>wwwroot/icons</c> is used.
/// </summary>
internal static class SiteLogo
{
    /// <summary>The sizes served: the tab icon, Google's and Entra's sign-in logos, the home-screen icons.</summary>
    public static readonly IReadOnlySet<int> Sizes = new HashSet<int> { 32, 120, 180, 192, 215, 512 };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".svg", ".png", ".jpg", ".jpeg", ".webp", ".avif" };

    // Drawn once per logo and size; a new logo has a new name.
    private static readonly ConcurrentDictionary<(string Name, int Size), byte[]> Drawn = new();

    /// <summary>Gets whether a path names a stored image that can be the logo.</summary>
    /// <param name="path">Such as <c>/media/1a2b3c4d5e6f-logo.svg</c>.</param>
    /// <param name="media">The media index.</param>
    /// <returns>True when it can.</returns>
    public static bool IsLogoFile(string path, MediaIndex media)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(media);
        return path.StartsWith("/media/", StringComparison.Ordinal)
            && path["/media/".Length..] is var name
            && Extensions.Contains(Path.GetExtension(name))
            && media.Has(name);
    }

    /// <summary>Gets the stored name of the site's logo, or null for the built-in one.</summary>
    /// <param name="site">The site settings.</param>
    /// <returns>Such as <c>1a2b3c4d5e6f-logo.svg</c>.</returns>
    public static string? Name(SiteSettings site) => MediaName(site.Logo);

    /// <summary>Gets the stored name of the site's favicon (T-0118): its own, or else the logo; null for the built-in one.</summary>
    /// <param name="site">The site settings.</param>
    /// <returns>Such as <c>1a2b3c4d5e6f-icon.svg</c>.</returns>
    public static string? FaviconName(SiteSettings site) => MediaName(site.Favicon) ?? Name(site);

    /// <summary>Gets whether the logo is an SVG, which browsers show as it is.</summary>
    /// <param name="name">The stored name.</param>
    /// <returns>True for an SVG.</returns>
    public static bool IsSvg(string name) => string.Equals(Path.GetExtension(name), ".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets the address of the logo drawn as a square PNG.</summary>
    /// <param name="name">The logo's stored name.</param>
    /// <param name="size">One of <see cref="Sizes"/>.</param>
    /// <returns>Such as <c>/site-icon/1a2b3c4d5e6f/120.png</c>.</returns>
    public static string Icon(string name, int size) =>
        $"/site-icon/{Key(name)}/{size.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png";

    /// <summary>Gets the address of the logo as the header and admin show it: the SVG, or a 192 px PNG.</summary>
    /// <param name="site">The site settings.</param>
    /// <param name="builtIn">The built-in logo's address.</param>
    /// <returns>The address.</returns>
    public static string Mark(SiteSettings site, string builtIn) =>
        Name(site) is { } name ? IsSvg(name) ? "/media/" + name : Icon(name, 192) : builtIn;

    /// <summary>Maps <c>GET /site-icon/{key}/{size}.png</c>.</summary>
    /// <param name="app">The app.</param>
    public static void MapSiteIcons(this IEndpointRouteBuilder app) =>
        app.MapGet("/site-icon/{key}/{size:int}.png", async (string key, int size, HttpContext context, ContentCatalogProvider content, IMediaStore media) =>
        {
            // Only the current logo or favicon, at the sizes the site uses: nothing else gets drawn.
            var site = content.Current.Site;
            var name = new[] { Name(site), FaviconName(site) }.FirstOrDefault(n => n is not null && Key(n) == key);
            if (name is null || !Sizes.Contains(size))
            {
                return Results.NotFound();
            }

            if (!Drawn.TryGetValue((name, size), out var png))
            {
                var file = await media.OpenAsync(name, context.RequestAborted);
                if (file is null)
                {
                    return Results.NotFound();
                }

                await using (file.Content)
                {
                    using var buffer = new MemoryStream();
                    await file.Content.CopyToAsync(buffer, context.RequestAborted);
                    png = ImageProcessor.Icon(buffer.ToArray(), size);
                }

                Drawn[(name, size)] = png;
            }

            context.Response.Headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
            return Results.Bytes(png, "image/png");
        });

    private static string? MediaName(string? path) =>
        path is { Length: > 7 } && path.StartsWith("/media/", StringComparison.Ordinal) ? path["/media/".Length..] : null;

    // The content hash the stored name starts with.
    private static string Key(string name) => name[..Math.Max(0, name.IndexOf('-', StringComparison.Ordinal))];
}
