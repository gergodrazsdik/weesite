using NetVips;
using Weesite.Content.Markdown;
using Weesite.Content.Media;

namespace Weesite.Content.Tests;

internal sealed class ImageTests
{
    private static readonly string MediaPath = Path.Combine(Weesite.Tests.RepoPaths.Seed, "media");

    [Test]
    [Arguments("0e3d1bf78702-diagram.svg", 120)]
    [Arguments("c0f10a37852f-gradient.jpg", 215)]
    [Arguments("b368a21f9e93-sample.png", 32)]
    public async Task A_logo_becomes_a_square_transparent_png_of_any_size(string file, int size)
    {
        // T-0116: SVG or raster, fitted and centred.
        var icon = ImageProcessor.Icon(file, await File.ReadAllBytesAsync(Path.Combine(MediaPath, file)), size);

        using var image = NetVips.Image.NewFromBuffer(icon);
        await Assert.That(icon.AsSpan(0, 8).SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])).IsTrue();
        await Assert.That((image.Width, image.Height)).IsEqualTo((size, size));
        await Assert.That(image.HasAlpha()).IsTrue();
    }

    [Test]
    public async Task Libvips_reads_only_the_sites_image_formats()
    {
        // T-0177: every other loader is blocked in the whole process, and an SVG is decoded only
        // for a logo whose name says SVG, never to measure an image.
        var svg = await File.ReadAllBytesAsync(Path.Combine(MediaPath, "0e3d1bf78702-diagram.svg"));
        using var black = Image.Black(8, 8);
        var tiff = black.TiffsaveBuffer();
        var gif = black.GifsaveBuffer();
        var jpeg = black.JpegsaveBuffer();
        ImageProcessor.IsProcessable("x.png");

        await Assert.That(() => Image.NewFromBuffer(tiff)).Throws<VipsException>();
        await Assert.That(() => Image.NewFromBuffer(gif)).Throws<VipsException>();
        await Assert.That(() => ImageProcessor.Measure(svg)).Throws<VipsException>();
        await Assert.That(() => ImageProcessor.Icon("x.png", svg, 32)).Throws<InvalidDataException>();
        await Assert.That(() => ImageProcessor.Icon("x.svg", jpeg, 32)).Throws<InvalidDataException>();
        await Assert.That(ImageProcessor.Measure(jpeg)).IsEqualTo((8, 8));
        await Assert.That(ImageProcessor.Icon("x.svg", svg, 32).Length).IsGreaterThan(0);
    }

    [Test]
    public async Task The_media_index_knows_every_file_but_measures_only_images()
    {
        var index = new MediaIndex(new Dictionary<string, ImageInfo> { ["0123456789ab-photo.jpg"] = new(10, 10, []) }, ["0123456789ab-photo.jpg", "abcdef012345-logo.svg"]);

        await Assert.That(index.Has("abcdef012345-logo.svg")).IsTrue();
        await Assert.That(index.Find("abcdef012345-logo.svg")).IsNull();
        await Assert.That(index.Has("0123456789ab-photo.jpg")).IsTrue();
        await Assert.That(index.Has("ffffffffffff-missing.png")).IsFalse();
    }

    [Test]
    public async Task Processing_strips_metadata_and_makes_smaller_webp_variants()
    {
        var data = await File.ReadAllBytesAsync(Path.Combine(MediaPath, "c0f10a37852f-gradient.jpg"));
        await Assert.That(HasExif(data)).IsTrue();

        var processed = ImageProcessor.Process("c0f10a37852f-gradient.jpg", data);

        await Assert.That(HasExif(processed.Original.ToArray())).IsFalse();
        await Assert.That((processed.Width, processed.Height)).IsEqualTo((1800, 1200));
        await Assert.That(processed.Variants.Select(v => v.Width)).IsEquivalentTo([480, 960, 1600]);
        await Assert.That(processed.Variants[0].Name).IsEqualTo("c0f10a37852f-gradient-480w.webp");
        foreach (var variant in processed.Variants)
        {
            using var image = Image.NewFromBuffer(variant.Data.ToArray());
            await Assert.That(image.Width).IsEqualTo(variant.Width);
            await Assert.That(HasExif(variant.Data.ToArray())).IsFalse();
        }
    }

    [Test]
    [Arguments(".jpg")]
    [Arguments(".webp")]
    public async Task An_image_without_metadata_compressed_harder_is_stored_as_uploaded(string extension)
    {
        // F-0172: re-encoding at quality 85 made such a file larger and a little worse.
        var data = Picture(extension, quality: 60);

        var processed = ImageProcessor.Process("photo" + extension, data);

        await Assert.That(processed.Original.ToArray().SequenceEqual(data)).IsTrue();
        await Assert.That(processed.Variants).IsNotEmpty();
    }

    [Test]
    public async Task An_image_without_metadata_is_re_encoded_when_that_makes_it_smaller()
    {
        var data = Picture(".jpg", quality: 100);

        var processed = ImageProcessor.Process("photo.jpg", data);

        await Assert.That(processed.Original.Length).IsLessThan(data.Length);
    }

    [Test]
    [Arguments("comment")]
    [Arguments("trailer")]
    [Arguments("exif")]
    public async Task A_jpeg_with_anything_but_the_picture_is_re_encoded(string extra)
    {
        var clean = Picture(".jpg", quality: 60);
        var secret = "Taken at 47.4979 N, 19.0402 E"u8.ToArray();
        byte[] data = extra switch
        {
            // A COM segment right after the start marker.
            "comment" => [.. clean[..2], 0xFF, 0xFE, .. Length(secret.Length + 2), .. secret, .. clean[2..]],
            // Bytes after the end marker, where phones put a second picture.
            "trailer" => [.. clean, .. secret],
            // An APP1 segment, as EXIF and XMP are.
            _ => [.. clean[..2], 0xFF, 0xE1, .. Length(secret.Length + 8), .. "Exif\0\0"u8, .. secret, .. clean[2..]],
        };

        var processed = ImageProcessor.Process("photo.jpg", data).Original.ToArray();

        await Assert.That(processed.SequenceEqual(data)).IsFalse();
        await Assert.That(processed.AsSpan().IndexOf(secret)).IsEqualTo(-1);
    }

    [Test]
    public async Task A_webp_with_a_metadata_chunk_is_re_encoded()
    {
        var clean = Picture(".webp", quality: 60);
        byte[] xmp = [.. "XMP "u8, 4, 0, 0, 0, .. "here"u8];
        byte[] data = [.. clean, .. xmp];
        BitConverter.TryWriteBytes(data.AsSpan(4), data.Length - 8);

        var processed = ImageProcessor.Process("photo.webp", data).Original.ToArray();

        await Assert.That(processed.SequenceEqual(data)).IsFalse();
        await Assert.That(processed.AsSpan().IndexOf("XMP "u8)).IsEqualTo(-1);
    }

    [Test]
    public async Task Small_images_get_no_variants()
    {
        var data = await File.ReadAllBytesAsync(Path.Combine(MediaPath, "b368a21f9e93-sample.png"));

        var processed = ImageProcessor.Process("b368a21f9e93-sample.png", data);

        await Assert.That(processed.Variants).IsEmpty();
        await Assert.That(ImageProcessor.Measure(data)).IsEqualTo((96, 96));
    }

    [Test]
    [Arguments("photo.jpg", true)]
    [Arguments("photo.JPEG", true)]
    [Arguments("shot.png", true)]
    [Arguments("pic.webp", true)]
    [Arguments("anim.gif", false)]
    [Arguments("diagram.svg", false)]
    [Arguments("paper.pdf", false)]
    public async Task Only_raster_formats_are_processed(string name, bool expected) =>
        await Assert.That(ImageProcessor.IsProcessable(name)).IsEqualTo(expected);

    [Test]
    public async Task Media_index_pairs_originals_with_their_variants()
    {
        string[] names = ["abc123-photo.jpg", "abc123-photo-480w.webp", "abc123-photo-960w.webp", "def456-doc.pdf"];

        var index = await MediaIndex.BuildAsync(names, (_, _) => Task.FromResult<(int, int)?>((1200, 800)), CancellationToken.None);

        await Assert.That(index.Count).IsEqualTo(1);
        await Assert.That(index.Find("abc123-photo.jpg")!.Variants.Select(v => v.Width)).IsEquivalentTo([480, 960]);
    }

    [Test]
    public async Task A_reload_measures_only_images_the_last_index_did_not_have()
    {
        // F-0064: every reload used to download and measure every original.
        var measured = new List<string>();
        Task<(int, int)?> Measure(string name, CancellationToken _)
        {
            measured.Add(name);
            return Task.FromResult<(int, int)?>((1200, 800));
        }

        var first = await MediaIndex.BuildAsync(["abc123-photo.jpg", "def456-other.png"], Measure, CancellationToken.None);
        measured.Clear();
        var second = await MediaIndex.BuildAsync(["abc123-photo.jpg", "def456-other.png", "abc123-photo-480w.webp", "fed987-new.jpg"], Measure, CancellationToken.None, first);

        await Assert.That(measured).IsEquivalentTo(["fed987-new.jpg"]);
        await Assert.That(second.Find("abc123-photo.jpg")!.Width).IsEqualTo(1200);
        await Assert.That(second.Find("abc123-photo.jpg")!.Variants.Select(v => v.Width)).IsEquivalentTo([480]);
        await Assert.That(second.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Images_render_with_size_srcset_and_lazy_loading()
    {
        var index = new MediaIndex(new Dictionary<string, ImageInfo>
        {
            ["abc123-photo.jpg"] = new(1200, 800, [(480, "abc123-photo-480w.webp"), (960, "abc123-photo-960w.webp")]),
        });
        var renderer = new MarkdownRenderer(new CodeHighlighter());

        var html = renderer.Render("![A photo](/media/abc123-photo.jpg)", index).Html;

        await Assert.That(html).Contains("srcset=\"/media/abc123-photo-480w.webp 480w, /media/abc123-photo-960w.webp 960w, /media/abc123-photo.jpg 1200w\"");
        await Assert.That(html).Contains("width=\"1200\" height=\"800\" alt=\"A photo\" loading=\"lazy\" decoding=\"async\"");
    }

    [Test]
    public async Task Images_without_alt_text_are_reported()
    {
        var renderer = new MarkdownRenderer(new CodeHighlighter());

        var rendered = renderer.Render("![](/media/abc123-photo.jpg)");

        await Assert.That(rendered.Problems).Contains("image /media/abc123-photo.jpg has no alt text (line 1)");
        await Assert.That(rendered.MissingAlt).IsEquivalentTo(["image /media/abc123-photo.jpg has no alt text (line 1)"]);
    }

    [Test]
    public async Task Only_images_without_alt_text_count_as_missing_alt_text()
    {
        // T-0122: a save refused text whose other problems merely read like this one.
        var renderer = new MarkdownRenderer(new CodeHighlighter());

        var rendered = renderer.Render("![A bell](/media/abc123-bell.jpg){ has no alt text (line } and [x](has-no-alt-text-(line-1))");

        await Assert.That(rendered.Problems).IsNotEmpty();
        await Assert.That(rendered.MissingAlt).IsEmpty();
    }

    private static bool HasExif(byte[] data)
    {
        using var image = Image.NewFromBuffer(data);
        return image.GetFields().Any(f => f.StartsWith("exif", StringComparison.Ordinal) || f.StartsWith("gps", StringComparison.Ordinal));
    }

    // A 1000 × 700 colour gradient, without metadata, as an encoder writes it.
    private static byte[] Picture(string extension, int quality)
    {
        using var xy = Image.Xyz(1000, 700);
        using var x = xy[0];
        using var y = xy[1];
        using var red = x * 255.0 / 1000;
        using var green = y * 255.0 / 700;
        using var blue = (x + y) * 255.0 / 1700;
        using var rgb = red.Bandjoin(green, blue);
        using var picture = rgb.Cast(Enums.BandFormat.Uchar);
        using var srgb = picture.Copy(interpretation: Enums.Interpretation.Srgb);
        return extension == ".webp"
            ? srgb.WebpsaveBuffer(q: quality, keep: Enums.ForeignKeep.None)
            : srgb.JpegsaveBuffer(q: quality, keep: Enums.ForeignKeep.None);
    }

    private static byte[] Length(int length) => [(byte)(length >> 8), (byte)length];
}
