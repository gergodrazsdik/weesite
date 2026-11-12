using NetVips;

namespace Weesite.Content.Media;

/// <summary>A resized copy of an image.</summary>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Name">Its stored name (<c>{name}-{width}w.webp</c>).</param>
/// <param name="Data">The WebP bytes.</param>
public sealed record ImageVariant(int Width, string Name, ReadOnlyMemory<byte> Data);

/// <summary>An image ready to store: the original without metadata, and its smaller WebP copies.</summary>
/// <param name="Original">The original, re-encoded without EXIF, GPS or other metadata.</param>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Variants">WebP copies at the widths in <see cref="ImageProcessor.Widths"/> smaller than the original.</param>
public sealed record ProcessedImage(ReadOnlyMemory<byte> Original, int Width, int Height, IReadOnlyList<ImageVariant> Variants);

/// <summary>
/// Prepares uploaded images with NetVips (Q-0002): applies the camera's orientation, strips every
/// bit of metadata (camera, GPS location), and writes WebP variants for <c>srcset</c>.
/// </summary>
public static class ImageProcessor
{
    // libvips caches the last 100 operations and their buffers (up to 100 MB, outside the .NET
    // heap). Every image here is processed once, so the cache only holds memory (F-0064).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1810", Justification = "libvips is configured once, before any image is touched.")]
    static ImageProcessor()
    {
        NetVips.Cache.Max = 0;
    }

    /// <summary>Gets the variant widths, in pixels. Never upscaled.</summary>
    public static IReadOnlyList<int> Widths { get; } = [480, 960, 1600];

    private static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>Gets a value indicating whether a file is an image this pipeline processes.</summary>
    /// <param name="name">The file name.</param>
    /// <returns>Whether it is a JPEG, PNG or WebP (not GIF, which may be animated, nor SVG).</returns>
    public static bool IsProcessable(string name) => RasterExtensions.Contains(Path.GetExtension(name));

    /// <summary>Gets the stored name of a variant.</summary>
    /// <param name="originalName">The original's stored name, such as <c>abc123-photo.jpg</c>.</param>
    /// <param name="width">The variant's width.</param>
    /// <returns>The variant's name, such as <c>abc123-photo-480w.webp</c>.</returns>
    public static string VariantName(string originalName, int width) =>
        $"{Path.GetFileNameWithoutExtension(originalName)}-{width}w.webp";

    /// <summary>Processes an image.</summary>
    /// <param name="name">Its stored name (the extension picks the output format).</param>
    /// <param name="data">Its bytes.</param>
    /// <returns>The stripped original and its variants.</returns>
    public static ProcessedImage Process(string name, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(data);

        using var loaded = Image.NewFromBuffer(data);
        using var image = loaded.Autorot();

        var original = Path.GetExtension(name).ToUpperInvariant() switch
        {
            ".PNG" => image.PngsaveBuffer(keep: Enums.ForeignKeep.None),
            ".WEBP" => image.WebpsaveBuffer(q: 85, keep: Enums.ForeignKeep.None),
            _ => image.JpegsaveBuffer(q: 85, keep: Enums.ForeignKeep.None),
        };

        var variants = new List<ImageVariant>();
        foreach (var width in Widths.Where(w => w < image.Width))
        {
            using var resized = image.ThumbnailImage(width);
            variants.Add(new ImageVariant(width, VariantName(name, width), resized.WebpsaveBuffer(q: 80, keep: Enums.ForeignKeep.None)));
        }

        return new ProcessedImage(original, image.Width, image.Height, variants);
    }

    /// <summary>
    /// Draws a square PNG icon of a logo (T-0116): the image, SVG or raster, fitted inside the
    /// square and centred on a transparent background, without metadata.
    /// </summary>
    /// <param name="data">The logo's bytes.</param>
    /// <param name="size">The square's side in pixels.</param>
    /// <returns>The PNG bytes.</returns>
    public static byte[] Icon(byte[] data, int size)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        using var fitted = Image.ThumbnailBuffer(data, size, height: size);
        using var colour = fitted.Colourspace(Enums.Interpretation.Srgb);
        using var opaque = colour.HasAlpha() ? colour.Copy() : colour.Bandjoin(255);
        using var square = opaque.Gravity(Enums.CompassDirection.Centre, size, size, Enums.Extend.Background, background: [0, 0, 0, 0]);
        return square.PngsaveBuffer(keep: Enums.ForeignKeep.None);
    }

    /// <summary>Reads an image's size.</summary>
    /// <param name="data">Its bytes.</param>
    /// <returns>Width and height in pixels, after orientation.</returns>
    public static (int Width, int Height) Measure(byte[] data)
    {
        using var image = Image.NewFromBuffer(data);
        var rotated = image.GetTypeOf("orientation") != 0 && image.Get("orientation") is int orientation && orientation >= 5;
        return rotated ? (image.Height, image.Width) : (image.Width, image.Height);
    }
}
