using NetVips;
using Weesite.Content.Markdown;
using Weesite.Content.Media;

namespace Weesite.Content.Tests;

internal sealed class ImageTests
{
    private static readonly string MediaPath = Path.Combine(FindRepoRoot(), "seed", "media");

    [Test]
    [Arguments("0e3d1bf78702-diagram.svg", 120)]
    [Arguments("c0f10a37852f-gradient.jpg", 215)]
    [Arguments("b368a21f9e93-sample.png", 32)]
    public async Task A_logo_becomes_a_square_transparent_png_of_any_size(string file, int size)
    {
        // T-0116: SVG or raster, fitted and centred.
        var icon = ImageProcessor.Icon(await File.ReadAllBytesAsync(Path.Combine(MediaPath, file)), size);

        using var image = NetVips.Image.NewFromBuffer(icon);
        await Assert.That(icon.AsSpan(0, 8).SequenceEqual((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])).IsTrue();
        await Assert.That((image.Width, image.Height)).IsEqualTo((size, size));
        await Assert.That(image.HasAlpha()).IsTrue();
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

        await Assert.That(rendered.Problems).Contains("image /media/abc123-photo.jpg has no alt text");
    }

    private static bool HasExif(byte[] data)
    {
        using var image = Image.NewFromBuffer(data);
        return image.GetFields().Any(f => f.StartsWith("exif", StringComparison.Ordinal) || f.StartsWith("gps", StringComparison.Ordinal));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Weesite.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Weesite.slnx not found above the test binaries.");
    }
}
