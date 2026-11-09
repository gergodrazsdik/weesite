using System.Security.Cryptography;
using System.Text;
using Weesite.Content.Sources;

namespace Weesite.Content.Media;

/// <summary>A stored media file.</summary>
/// <param name="Name">Its name, such as <c>1a2b3c4d5e6f-photo.jpg</c>.</param>
/// <param name="IsImage">Whether it's an image the site shows inline.</param>
/// <param name="Width">An image's width, if known.</param>
/// <param name="Height">An image's height, if known.</param>
public sealed record MediaItem(string Name, bool IsImage, int? Width = null, int? Height = null)
{
    /// <summary>Gets the path the site serves it at, such as <c>/media/1a2b3c4d5e6f-photo.jpg</c>.</summary>
    public string Path => "/media/" + Name;
}

/// <summary>What happened to an upload: <see cref="MediaUploaded"/> or <see cref="MediaRejected"/>.</summary>
public abstract record UploadOutcome;

/// <summary>The file is stored (or was already).</summary>
/// <param name="Item">The stored file.</param>
public sealed record MediaUploaded(MediaItem Item) : UploadOutcome;

/// <summary>The file was refused; nothing was stored.</summary>
/// <param name="Reason">Why.</param>
public sealed record MediaRejected(string Reason) : UploadOutcome;

/// <summary>
/// Uploads for the editor (brief, Images and attachments): checks each file (an allowed type, a
/// size limit, and contents that match the type), stores it under a name that carries its hash,
/// runs images through <see cref="ImageProcessor"/> (metadata stripped, WebP variants), and
/// reloads, so the new file's sizes are known at once. SVG and other attachments are only ever
/// served as downloads (the media endpoint).
/// </summary>
public sealed class MediaLibrary(IMediaStore media, IContentStore content, ContentLoader loader, ContentReloader reloader, ContentCatalogProvider catalog)
{
    /// <summary>The largest file accepted, in bytes (20 MB).</summary>
    public const long MaxBytes = 20 * 1024 * 1024;

    /// <summary>The most pixels an image may have (50 megapixels): more is likely a decompression bomb.</summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>Gets the file types accepted, as extensions.</summary>
    public static IReadOnlySet<string> AllowedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".pdf", ".svg", ".txt", ".md", ".csv", ".zip",
    };

    /// <summary>Checks and stores a file.</summary>
    /// <param name="fileName">The name it was uploaded with.</param>
    /// <param name="data">Its bytes.</param>
    /// <param name="cancellationToken">Stops the upload.</param>
    /// <returns>What happened.</returns>
    public async Task<UploadOutcome> UploadAsync(string fileName, byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(data);
        var extension = System.IO.Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(extension))
        {
            return new MediaRejected($"{extension} files aren't accepted; allowed: {string.Join(", ", AllowedExtensions.Order(StringComparer.Ordinal))}.");
        }

        if (data.Length == 0 || data.Length > MaxBytes)
        {
            return new MediaRejected($"The file must be between 1 byte and {MaxBytes / (1024 * 1024)} MB.");
        }

        if (Check(extension, data) is { } problem)
        {
            return new MediaRejected(problem);
        }

        var name = $"{Convert.ToHexStringLower(SHA256.HashData(data))[..12]}-{SafeName(fileName)}";
        var isImage = ImageProcessor.IsProcessable(name);
        if (await media.OpenAsync(name, cancellationToken) is { } existing)
        {
            await existing.Content.DisposeAsync();
            return new MediaUploaded(Describe(name, isImage));
        }

        if (isImage)
        {
            // Variants first, the original last: an upload cut short leaves nothing that looks done.
            var image = ImageProcessor.Process(name, data);
            foreach (var variant in image.Variants)
            {
                await media.WriteAsync(variant.Name, variant.Data, cancellationToken);
            }

            await media.WriteAsync(name, image.Original, cancellationToken);
        }
        else
        {
            await media.WriteAsync(name, data, cancellationToken);
        }

        reloader.Acknowledge(await content.BumpVersionAsync(cancellationToken));
        await loader.LoadAsync(cancellationToken);
        return new MediaUploaded(Describe(name, isImage));
    }

    /// <summary>Lists the stored files, without the generated variants, newest names last.</summary>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The files, by name.</returns>
    public async Task<IReadOnlyList<MediaItem>> ListAsync(CancellationToken cancellationToken)
    {
        var names = await media.ListAsync(cancellationToken);
        return [.. names
            .Where(n => !n.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || !System.Text.RegularExpressions.Regex.IsMatch(n, @"-\d+w\.webp$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
            .Order(StringComparer.Ordinal)
            .Select(n => Describe(n, ImageProcessor.IsProcessable(n)))];
    }

    /// <summary>Turns an uploaded file's name into a safe media name: lowercase letters, digits, dots, hyphens.</summary>
    /// <param name="fileName">The uploaded name.</param>
    /// <returns>Such as <c>my-photo.jpg</c>.</returns>
    internal static string SafeName(string fileName)
    {
        var extension = System.IO.Path.GetExtension(fileName).ToUpperInvariant() switch
        {
            ".JPEG" => ".jpg",
            var e => e.ToLowerInvariantSafe(),
        };
        var stem = new StringBuilder();
        foreach (var ch in System.IO.Path.GetFileNameWithoutExtension(fileName).Normalize(NormalizationForm.FormD))
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                stem.Append(char.ToLowerInvariant(ch));
            }
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark
                && stem.Length > 0 && stem[^1] != '-')
            {
                stem.Append('-');
            }
        }

        var safe = stem.ToString().Trim('-');
        if (safe.Length > 60)
        {
            safe = safe[..60].TrimEnd('-');
        }

        return (safe.Length == 0 ? "file" : safe) + extension;
    }

    // Contents must match the type: an image must decode and not be huge, other types must start
    // the way their format does.
    /// <summary>
    /// Why a file can't be stored, or null when it can: the type must be allowed and match the
    /// contents (images must decode and stay under <see cref="MaxPixels"/>). The backup import
    /// checks files with it too.
    /// </summary>
    /// <param name="fileName">The file's name, for its type.</param>
    /// <param name="data">Its bytes.</param>
    /// <returns>The problem, or null.</returns>
    public static string? Problem(string fileName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(data);
        var extension = System.IO.Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(extension))
        {
            return $"{extension} files aren't accepted.";
        }

        return data.Length == 0 || data.Length > MaxBytes ? "The file is empty or larger than 20 MB." : Check(extension, data);
    }

    private static string? Check(string extension, byte[] data)
    {
        switch (extension.ToUpperInvariant())
        {
            case ".JPG" or ".JPEG" or ".PNG" or ".WEBP":
                try
                {
                    var (width, height) = ImageProcessor.Measure(data);
                    return (long)width * height > MaxPixels ? $"The image is too large ({width} × {height} pixels; at most {MaxPixels / 1_000_000} megapixels)." : null;
                }
                catch (NetVips.VipsException)
                {
                    return "The file isn't a readable image.";
                }

            case ".PDF":
                return Starts(data, "%PDF-"u8) ? null : "The file isn't a PDF.";
            case ".ZIP":
                return Starts(data, "PK\u0003\u0004"u8) ? null : "The file isn't a ZIP archive.";
            case ".SVG":
                return Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(data.Length, 4096))).Contains("<svg", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "The file isn't an SVG image.";
            default:
                // Text: valid UTF-8 without NUL bytes.
                try
                {
                    return new UTF8Encoding(false, true).GetString(data).Contains('\0', StringComparison.Ordinal) ? "The file isn't plain text." : null;
                }
                catch (DecoderFallbackException)
                {
                    return "The file isn't UTF-8 text.";
                }
        }
    }

    private static bool Starts(byte[] data, ReadOnlySpan<byte> signature) => data.AsSpan().StartsWith(signature);

    private MediaItem Describe(string name, bool isImage) =>
        catalog.Current.Media.Find(name) is { } info
            ? new MediaItem(name, isImage, info.Width, info.Height)
            : new MediaItem(name, isImage);
}

/// <summary>String helpers the analyzers accept.</summary>
internal static class MediaNameExtensions
{
    /// <summary>Lower-cases an ASCII extension (CA1308 prefers upper case for comparisons; this is for names).</summary>
    /// <param name="value">The extension.</param>
    /// <returns>It in lower case.</returns>
    public static string ToLowerInvariantSafe(this string value) =>
        string.Create(value.Length, value, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.ToLowerInvariant(source[i]);
            }
        });
}
