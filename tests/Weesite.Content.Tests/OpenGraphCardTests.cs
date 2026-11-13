using NetVips;
using Weesite.Content.Media;

namespace Weesite.Content.Tests;

internal sealed class OpenGraphCardTests
{
    [Test]
    [Arguments("Hello, world")]
    [Arguments("Árvíztűrő tükörfúrógép")]
    [Arguments("<b>Tags</b> & \"markup\" aren't parsed")]
    [Arguments("A very long title that goes on and on, far beyond what a card can hold at its largest size, so it has to shrink step by step and finally be cut short with an ellipsis at the end of it all")]
    public async Task Renders_a_1200_by_630_png(string title)
    {
        var png = OpenGraphCard.Render(title, "Example Site", CardColours.Default);

        using var image = Image.NewFromBuffer(png);
        await Assert.That((image.Width, image.Height)).IsEqualTo((1200, 630));
        await Assert.That(image.Bands).IsEqualTo(3);
        await Assert.That(png.AsSpan(1, 3).SequenceEqual("PNG"u8)).IsTrue();
    }

    [Test]
    [Arguments("Hello 👋", "Hello")]
    [Arguments("🚀 Launch day 🎉🎉", "Launch day")]
    [Arguments("Family 👨‍👩‍👧 trip", "Family trip")]
    [Arguments("Flags 🇭🇺 and 🏴󠁧󠁢󠁳󠁣󠁴󠁿", "Flags and")]
    [Arguments("Keycap 1️⃣ ❤️ ☀", "Keycap 1")]
    [Arguments("Árvíztűrő tükörfúrógép – 100 % (ok)", "Árvíztűrő tükörfúrógép – 100 % (ok)")]
    [Arguments("日本語のタイトル", "日本語のタイトル")]
    public async Task Emoji_are_left_out_of_the_card_text(string text, string expected) =>
        await Assert.That(OpenGraphCard.WithoutEmoji(text)).IsEqualTo(expected);

    [Test]
    public async Task A_title_with_emoji_draws_as_the_title_without_them()
    {
        // F-0085: no empty box where Inter has no glyph.
        var withEmoji = OpenGraphCard.Render("Shipping it 🚀", "Example Site ✨", CardColours.Default);
        var without = OpenGraphCard.Render("Shipping it", "Example Site", CardColours.Default);

        await Assert.That(withEmoji.AsSpan().SequenceEqual(without)).IsTrue();
    }

    [Test]
    public async Task Draws_the_background_accent_bar_and_text_in_the_given_colours()
    {
        var colours = new CardColours("#ffffff", "#000000", "#ff0000");

        using var image = Image.NewFromBuffer(OpenGraphCard.Render("Title", "Site", colours));

        await Assert.That(image.Getpoint(600, 5)).IsEquivalentTo([255.0, 0, 0]);
        await Assert.That(image.Getpoint(1190, 620)).IsEquivalentTo([255.0, 255, 255]);
        using var text = image.Crop(80, 112, 400, 120);
        await Assert.That(text.Min()).IsLessThan(64);
    }

    [Test]
    public async Task Key_changes_with_title_site_name_and_colours()
    {
        var key = OpenGraphCard.Key("A", "S", CardColours.Default);

        await Assert.That(key).Matches("^[0-9a-f]{16}$");
        await Assert.That(OpenGraphCard.Key("A", "S", CardColours.Default)).IsEqualTo(key);
        await Assert.That(OpenGraphCard.Key("B", "S", CardColours.Default)).IsNotEqualTo(key);
        await Assert.That(OpenGraphCard.Key("A", "T", CardColours.Default)).IsNotEqualTo(key);
        await Assert.That(OpenGraphCard.Key("A", "S", CardColours.Default with { Accent = "#000000" })).IsNotEqualTo(key);
    }

    [Test]
    public async Task Colours_come_from_the_dark_theme_and_ignore_invalid_values()
    {
        var site = new SiteSettings
        {
            Theme = new ThemeSettings
            {
                Dark = new Dictionary<string, string> { ["bg"] = "#000000", ["accent"] = "red", ["text"] = " #fafafa " },
            },
        };

        var colours = CardColours.For(site);

        await Assert.That(colours).IsEqualTo(new CardColours("#000000", "#fafafa", CardColours.Default.Accent));
        await Assert.That(CardColours.For(SiteSettings.Empty)).IsEqualTo(CardColours.Default);
    }

    [Test]
    [Arguments("#fff", true)]
    [Arguments("#1A2b3C", true)]
    [Arguments(" #123456 ", true)]
    [Arguments("#12345", false)]
    [Arguments("#ggg", false)]
    [Arguments("#-12345", false)]
    public async Task Colours_are_read_as_the_theme_takes_them(string value, bool valid)
    {
        // F-0120: the theme takes #rgb, and the card fell back to its own colours for it.
        await Assert.That(OpenGraphCard.TryParseHex(value, out var rgb)).IsEqualTo(valid);
        if (value == "#fff")
        {
            await Assert.That(rgb).IsEquivalentTo([255.0, 255.0, 255.0]);
        }
    }
}
