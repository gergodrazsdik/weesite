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
    private readonly FrozenSet<string> _files;

    /// <summary>Initializes a new instance of the <see cref="MediaIndex"/> class.</summary>
    /// <param name="images">Image info by stored name.</param>
    /// <param name="files">Every stored file's name, generated variants aside; the images' names when omitted.</param>
    public MediaIndex(IDictionary<string, ImageInfo> images, IEnumerable<string>? files = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        _images = images.ToFrozenDictionary(StringComparer.Ordinal);
        _files = (files ?? images.Keys).ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Gets an empty index.</summary>
    public static MediaIndex Empty { get; } = new(new Dictionary<string, ImageInfo>());

    /// <summary>Gets the number of images.</summary>
    public int Count => _images.Count;

    /// <summary>Finds an image by stored name.</summary>
    /// <param name="name">The name, such as <c>abc123-photo.jpg</c>.</param>
    /// <returns>Its info, or null.</returns>
    public ImageInfo? Find(string name) => _images.GetValueOrDefault(name);

    /// <summary>Gets whether a file is stored, of any type, such as an SVG the index doesn't measure.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True when it's in the media library.</returns>
    public bool Has(string name) => _files.Contains(name);

    /// <summary>Builds the index from a listing of the media container.</summary>
    /// <param name="names">Every stored name.</param>
    /// <param name="measure">Reads an original's size.</param>
    /// <param name="cancellationToken">Stops the build.</param>
    /// <param name="previous">The last index: images it measured aren't read again.</param>
    /// <returns>The index.</returns>
    public static async Task<MediaIndex> BuildAsync(
        IReadOnlyCollection<string> names,
        Func<string, CancellationToken, Task<(int Width, int Height)?>> measure,
        CancellationToken cancellationToken,
        MediaIndex? previous = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(measure);

        var present = names.ToHashSet(StringComparer.Ordinal);
        var images = new Dictionary<string, ImageInfo>(StringComparer.Ordinal);
        foreach (var name in names.Where(IsOriginalImage))
        {
            // A name starts with a hash of the file, so it never changes meaning: an image the
            // last index measured keeps its size, and only new ones are read (F-0064).
            var size = previous?.Find(name) is { } known
                ? (known.Width, known.Height)
                : await measure(name, cancellationToken);
            if (size is null)
            {
                continue;
            }

            var variants = ImageProcessor.Widths
                .Select(w => (Width: w, Name: ImageProcessor.VariantName(name, w)))
                .Where(v => present.Contains(v.Name))
                .ToList();
            images[name] = new ImageInfo(size.Value.Width, size.Value.Height, variants);
        }

        return new MediaIndex(images, names.Where(n => !VariantPattern().IsMatch(n)));
    }

    /// <summary>Gets whether a stored name is an original image the index measures, not a variant.</summary>
    /// <param name="name">The stored name.</param>
    /// <returns>True for a JPEG, PNG or WebP original.</returns>
    public static bool IsOriginalImage(string name) => ImageProcessor.IsProcessable(name) && !VariantPattern().IsMatch(name);

    [GeneratedRegex(@"-\d+w\.webp$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex VariantPattern();
}
