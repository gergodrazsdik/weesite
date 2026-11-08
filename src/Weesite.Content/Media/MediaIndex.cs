using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Weesite.Content.Media;

/// <summary>An image's size and its resized copies.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Variants">The WebP copies, smallest first: width and stored name.</param>
public sealed record ImageInfo(int Width, int Height, IReadOnlyList<(int Width, string Name)> Variants);

/// <summary>
/// What the renderer needs to know about images at <c>/media/…</c>: their size (for
/// <c>width</c>/<c>height</c>, so pages don't shift while loading) and their variants (for
/// <c>srcset</c>). Built when the catalog loads.
/// </summary>
public sealed partial class MediaIndex
{
    private readonly FrozenDictionary<string, ImageInfo> _images;

    /// <summary>Initializes a new instance of the <see cref="MediaIndex"/> class.</summary>
    /// <param name="images">Image info by stored name.</param>
    public MediaIndex(IDictionary<string, ImageInfo> images) =>
        _images = images.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets an empty index.</summary>
    public static MediaIndex Empty { get; } = new(new Dictionary<string, ImageInfo>());

    /// <summary>Gets the number of images.</summary>
    public int Count => _images.Count;

    /// <summary>Finds an image by stored name.</summary>
    /// <param name="name">The name, such as <c>abc123-photo.jpg</c>.</param>
    /// <returns>Its info, or null.</returns>
    public ImageInfo? Find(string name) => _images.GetValueOrDefault(name);

    /// <summary>Builds the index from a listing of the media container.</summary>
    /// <param name="names">Every stored name.</param>
    /// <param name="measure">Reads an original's size.</param>
    /// <param name="cancellationToken">Stops the build.</param>
    /// <returns>The index.</returns>
    public static async Task<MediaIndex> BuildAsync(
        IReadOnlyCollection<string> names,
        Func<string, CancellationToken, Task<(int Width, int Height)?>> measure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(measure);

        var present = names.ToHashSet(StringComparer.Ordinal);
        var images = new Dictionary<string, ImageInfo>(StringComparer.Ordinal);
        foreach (var name in names.Where(n => ImageProcessor.IsProcessable(n) && !VariantPattern().IsMatch(n)))
        {
            if (await measure(name, cancellationToken) is not { } size)
            {
                continue;
            }

            var variants = ImageProcessor.Widths
                .Select(w => (Width: w, Name: ImageProcessor.VariantName(name, w)))
                .Where(v => present.Contains(v.Name))
                .ToList();
            images[name] = new ImageInfo(size.Width, size.Height, variants);
        }

        return new MediaIndex(images);
    }

    [GeneratedRegex(@"-\d+w\.webp$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex VariantPattern();
}
