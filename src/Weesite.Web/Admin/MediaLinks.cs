using Weesite.Content.Media;

namespace Weesite.Web.Admin;

/// <summary>A stored file as the media page and <c>ws-media-picker</c> show it.</summary>
/// <param name="Name">The file's name.</param>
/// <param name="Path">Where the site serves it.</param>
/// <param name="IsImage">Whether it's an image.</param>
/// <param name="Width">An image's width, if known.</param>
/// <param name="Height">An image's height, if known.</param>
/// <param name="Thumbnail">A small copy to show in lists (the smallest variant), or the file itself.</param>
/// <param name="Markdown">The Markdown that shows or links it.</param>
/// <param name="IsArchived">Whether it's archived (T-0111).</param>
/// <param name="Alt">An image's description, as given or kept from its upload (F-0139).</param>
internal sealed record MediaLink(string Name, string Path, bool IsImage, int? Width, int? Height, string? Thumbnail, string Markdown, bool IsArchived = false, string? Alt = null);

/// <summary>Markdown snippets and thumbnails for stored files.</summary>
internal static class MediaLinks
{
    /// <summary>The placeholder description in an image snippet without one.</summary>
    public const string AltPlaceholder = "describe the image";

    /// <summary>Describes a stored file.</summary>
    /// <param name="item">The file.</param>
    /// <param name="media">The media index, for sizes and variants.</param>
    /// <param name="alt">The image's description, if known.</param>
    /// <returns>The description.</returns>
    public static MediaLink Describe(MediaItem item, MediaIndex media, string? alt)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(media);
        var info = media.Find(item.Name);
        // An SVG is its own thumbnail (T-0120): browsers draw it in an img, where its scripts don't run.
        var thumbnail = item.IsImage
            ? info is { Variants.Count: > 0 } ? "/media/" + info.Variants[0].Name : item.Path
            : item.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? item.Path : null;
        // The description given now, or the one kept from its upload (F-0139).
        var description = string.IsNullOrWhiteSpace(alt) ? item.Alt : alt.Trim();
        return new MediaLink(item.Name, item.Path, item.IsImage, info?.Width ?? item.Width, info?.Height ?? item.Height, thumbnail, Snippet(item, description), item.IsArchived, item.IsImage ? description : null);
    }

    /// <summary>Gets the Markdown for a file: an image with its description, or a link named after it.</summary>
    /// <param name="item">The file.</param>
    /// <param name="alt">The image's description, if known.</param>
    /// <returns>Such as <c>![A lighthouse](/media/1a2b3c4d5e6f-lighthouse.jpg)</c>.</returns>
    public static string Snippet(MediaItem item, string? alt)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsImage)
        {
            var text = string.IsNullOrWhiteSpace(alt) ? AltPlaceholder : alt.Trim().Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);
            return $"![{text}]({item.Path})";
        }

        return $"[{item.Name[(item.Name.IndexOf('-', StringComparison.Ordinal) + 1)..]}]({item.Path})";
    }
}
