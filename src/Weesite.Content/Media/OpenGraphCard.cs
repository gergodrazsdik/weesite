using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using NetVips;

namespace Weesite.Content.Media;

/// <summary>The colours of a generated card, as <c>#rrggbb</c>.</summary>
/// <param name="Background">The background.</param>
/// <param name="Text">The title.</param>
/// <param name="Accent">The bar along the top and the site name.</param>
public sealed record CardColours(string Background, string Text, string Accent)
{
    /// <summary>Gets the default colours: the dark theme's background, text and accent.</summary>
    public static CardColours Default { get; } = new("#0f1115", "#e6e8ed", "#8ab4ff");

    /// <summary>Gets the colours for a site: the default ones, overridden by the site's dark theme.</summary>
    /// <param name="site">The site settings.</param>
    /// <returns>The colours.</returns>
    public static CardColours For(SiteSettings site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var dark = site.Theme?.Dark;
        return new(
            Pick(dark, "bg", Default.Background),
            Pick(dark, "text", Default.Text),
            Pick(dark, "accent", Default.Accent));
    }

    private static string Pick(IReadOnlyDictionary<string, string>? colours, string token, string fallback) =>
        colours is not null && colours.TryGetValue(token, out var value) && OpenGraphCard.TryParseHex(value.Trim(), out _)
            ? value.Trim()
            : fallback;
}

/// <summary>
/// Renders an Open Graph card: a 1200 × 630 PNG with the title in large type and the site name
/// below it, in Inter from <c>Fonts/</c> (ADR 0002). Titles shrink to fit; very long ones are
/// cut with an ellipsis.
/// </summary>
public static class OpenGraphCard
{
    /// <summary>The card width in pixels.</summary>
    public const int Width = 1200;

    /// <summary>The card height in pixels.</summary>
    public const int Height = 630;

    // Bump when the layout changes, so card URLs (which carry the key) change too.
    private const string LayoutVersion = "1";
    private const int Margin = 80;
    private const int BarHeight = 12;
    private const int MaxTitleLength = 160;
    private const int SiteNameSize = 32;
    private static readonly int[] TitleSizes = [72, 64, 56, 48, 40];

    private static readonly string FontDirectory = Path.Combine(AppContext.BaseDirectory, "Fonts");
    private static readonly string BoldFont = Path.Combine(FontDirectory, "Inter-Bold.ttf");
    private static readonly string RegularFont = Path.Combine(FontDirectory, "Inter-Regular.ttf");

    /// <summary>Gets a short key that changes whenever the card's content or layout does.</summary>
    /// <param name="title">The title.</param>
    /// <param name="siteName">The site name.</param>
    /// <param name="colours">The colours.</param>
    /// <returns>16 lowercase hex digits.</returns>
    public static string Key(string title, string siteName, CardColours colours)
    {
        ArgumentNullException.ThrowIfNull(colours);
        var input = string.Join('\n', LayoutVersion, title, siteName, colours.Background, colours.Text, colours.Accent);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
    }

    /// <summary>Renders a card.</summary>
    /// <param name="title">The title (a post's title, or the site's tagline).</param>
    /// <param name="siteName">The site name.</param>
    /// <param name="colours">The colours.</param>
    /// <returns>The PNG.</returns>
    public static byte[] Render(string title, string siteName, CardColours colours)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(siteName);
        ArgumentNullException.ThrowIfNull(colours);

        var titleWidth = Width - (2 * Margin);
        var nameTop = Height - Margin - SiteNameSize;
        var titleTop = Margin + BarHeight + 20;
        var titleMaxHeight = nameTop - titleTop - 40;

        using var background = Solid(Width, Height, colours.Background);
        using var bar = Solid(Width, BarHeight, colours.Accent);
        using var titleMask = FitTitle(Shorten(title.Trim()), titleWidth, titleMaxHeight);
        using var nameMask = Text(siteName.Trim(), $"Inter {SiteNameSize}", RegularFont, titleWidth);
        using var titleLayer = Colourize(titleMask, colours.Text);
        using var nameLayer = Colourize(nameMask, colours.Accent);
        // Each step is disposed: an undisposed libvips image holds native memory until the .NET
        // finalizer runs, which an idle site rarely does (F-0064).
        using var withBar = background.Insert(bar, 0, 0);
        using var withTitle = withBar.Composite2(titleLayer, Enums.BlendMode.Over, x: Margin, y: titleTop);
        using var card = withTitle.Composite2(nameLayer, Enums.BlendMode.Over, x: Margin, y: nameTop);
        using var flat = card.Flatten();
        return flat.PngsaveBuffer(compression: 9, keep: Enums.ForeignKeep.None);
    }

    /// <summary>Parses a <c>#rrggbb</c> colour.</summary>
    /// <param name="value">The colour.</param>
    /// <param name="rgb">Red, green and blue.</param>
    /// <returns>Whether it parsed.</returns>
    internal static bool TryParseHex(string value, out double[] rgb)
    {
        rgb = [];
        if (value.Length != 7 || value[0] != '#'
            || !int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
        {
            return false;
        }

        rgb = [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
        return true;
    }

    private static double[] Rgb(string hex) =>
        TryParseHex(hex, out var rgb) ? rgb : throw new ArgumentException($"'{hex}' isn't a #rrggbb colour.", nameof(hex));

    // Cut at the last word boundary that fits, with an ellipsis.
    private static string Shorten(string title)
    {
        if (title.Length <= MaxTitleLength)
        {
            return title;
        }

        var cut = title.AsSpan(0, MaxTitleLength).LastIndexOf(' ');
        var head = title.AsSpan(0, cut > MaxTitleLength / 2 ? cut : MaxTitleLength - 1).TrimEnd(" ,;:.-–—");
        return string.Concat(head, "…");
    }

    // The largest size at which the title fits; the smallest one otherwise, cropped to the space.
    private static Image FitTitle(string title, int width, int maxHeight)
    {
        foreach (var size in TitleSizes.AsSpan(0, TitleSizes.Length - 1))
        {
            var mask = Text(title, $"Inter Bold {size}", BoldFont, width, spacing: size / 6);
            if (mask.Height <= maxHeight)
            {
                return mask;
            }

            mask.Dispose();
        }

        var smallest = TitleSizes[^1];
        using var last = Text(title, $"Inter Bold {smallest}", BoldFont, width, spacing: smallest / 6);
        return last.Crop(0, 0, last.Width, Math.Min(last.Height, maxHeight));
    }

    // Text as a one-band mask. Pango reads markup, so the text is escaped.
    private static Image Text(string text, string font, string fontFile, int width, int spacing = 0) =>
        Image.Text(
            SecurityElement.Escape(text),
            font: font,
            fontfile: fontFile,
            width: width,
            dpi: 72,
            spacing: spacing,
            wrap: Enums.TextWrap.WordChar);

    private static Image Solid(int width, int height, string hex)
    {
        var rgb = Rgb(hex);
        using var black = Image.Black(width, height, bands: 3);
        using var filled = black.NewFromImage(rgb);
        return filled.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    // A mask becomes an RGBA layer: the colour, with the mask as alpha.
    private static Image Colourize(Image mask, string hex)
    {
        var rgb = Rgb(hex);
        using var colour = mask.NewFromImage(rgb);
        using var joined = colour.Bandjoin(mask);
        return joined.Copy(interpretation: Enums.Interpretation.Srgb);
    }
}
