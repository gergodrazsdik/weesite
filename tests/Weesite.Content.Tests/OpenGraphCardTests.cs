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
}
