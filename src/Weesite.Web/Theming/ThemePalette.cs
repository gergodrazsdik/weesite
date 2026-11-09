using System.Globalization;

namespace Weesite.Web.Theming;

/// <summary>One contrast requirement: a foreground token on a background token.</summary>
/// <param name="Foreground">The foreground token, such as <c>text</c>.</param>
/// <param name="Background">The background token, such as <c>bg</c>.</param>
/// <param name="Minimum">The lowest ratio allowed: 4.5 for text (WCAG AA), 3 for the focus ring.</param>
internal sealed record ContrastPair(string Foreground, string Background, double Minimum);

/// <summary>A pair's result in one mode.</summary>
/// <param name="Mode"><c>light</c> or <c>dark</c>.</param>
/// <param name="Pair">The requirement.</param>
/// <param name="Ratio">The contrast ratio, or null when a colour isn't a hex colour.</param>
internal sealed record ContrastResult(string Mode, ContrastPair Pair, double? Ratio)
{
    /// <summary>Gets whether the pair meets its minimum.</summary>
    public bool Passes => Ratio >= Pair.Minimum;
}

/// <summary>
/// The default palette (the same values as app.css's tokens layer), the built-in presets and the
/// WCAG contrast check the theme editor runs before a save (brief, Theme editor).
/// </summary>
internal static class ThemePalette
{
    /// <summary>The modes, in the order the editor shows them.</summary>
    public static readonly IReadOnlyList<string> Modes = ["light", "dark"];

    /// <summary>The tokens, in the order the editor shows them.</summary>
    public static readonly IReadOnlyList<string> Tokens = ["bg", "surface", "text", "muted", "accent", "link", "border", "code-bg", "focus"];

    /// <summary>The pairs checked in each mode.</summary>
    public static readonly IReadOnlyList<ContrastPair> Pairs =
    [
        new("text", "bg", 4.5),
        new("text", "surface", 4.5),
        new("text", "code-bg", 4.5),
        new("muted", "bg", 4.5),
        new("muted", "surface", 4.5),
        new("link", "bg", 4.5),
        new("link", "surface", 4.5),
        new("accent", "bg", 4.5),
        new("focus", "bg", 3),
    ];

    /// <summary>Gets the default colours per mode and token (app.css).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Defaults { get; } = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
    {
        ["light"] = Palette("#ffffff", "#f5f7fa", "#1a1d23", "#4b5563", "#1d4ed8", "#1d4ed8", "#d1d5db", "#f3f4f6", "#1d4ed8"),
        ["dark"] = Palette("#111318", "#1a1d24", "#e6e8eb", "#a1a8b3", "#8ab4ff", "#8ab4ff", "#2d323c", "#1a1d24", "#8ab4ff"),
    };

    /// <summary>Gets the built-in presets by name: full palettes for both modes, all passing the check.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Presets { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal)
        {
            ["Forest"] = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["light"] = Palette("#ffffff", "#f3f7f4", "#1b2420", "#4a5a52", "#17663a", "#17663a", "#cfdcd4", "#eef3f0", "#17663a"),
                ["dark"] = Palette("#0f1512", "#17201b", "#e3ebe6", "#9fb1a7", "#7fd3a0", "#7fd3a0", "#2a3830", "#17201b", "#7fd3a0"),
            },
            ["Plum"] = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["light"] = Palette("#ffffff", "#f8f5fa", "#221a27", "#594e60", "#6b2fa3", "#6b2fa3", "#ddd3e3", "#f2edf5", "#6b2fa3"),
                ["dark"] = Palette("#141117", "#1e1a22", "#ebe5ef", "#b0a4b8", "#cfa8f5", "#cfa8f5", "#352d3c", "#1e1a22", "#cfa8f5"),
            },
        };

    /// <summary>Checks every pair in both modes, with the defaults where nothing is set.</summary>
    /// <param name="overrides">The colours set, per mode and token.</param>
    /// <returns>The results, light first.</returns>
    public static IReadOnlyList<ContrastResult> Check(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var results = new List<ContrastResult>();
        foreach (var mode in Modes)
        {
            string Colour(string token) =>
                overrides.TryGetValue(mode, out var set) && set.TryGetValue(token, out var value) && !string.IsNullOrWhiteSpace(value) ? value : Defaults[mode][token];

            foreach (var pair in Pairs)
            {
                results.Add(new ContrastResult(mode, pair, Ratio(Colour(pair.Foreground), Colour(pair.Background))));
            }
        }

        return results;
    }

    /// <summary>Gets the WCAG contrast ratio of two hex colours.</summary>
    /// <param name="foreground">Such as <c>#1a1d23</c>.</param>
    /// <param name="background">Such as <c>#ffffff</c>.</param>
    /// <returns>The ratio (1 to 21), or null when either isn't a hex colour.</returns>
    public static double? Ratio(string foreground, string background)
    {
        if (Luminance(foreground) is not { } a || Luminance(background) is not { } b)
        {
            return null;
        }

        var (light, dark) = a > b ? (a, b) : (b, a);
        return (light + 0.05) / (dark + 0.05);
    }

    /// <summary>Normalizes <c>#rgb</c> and <c>#rrggbb</c> to lower-case <c>#rrggbb</c>.</summary>
    /// <param name="value">The colour.</param>
    /// <returns>The colour, or null when it isn't one of those forms.</returns>
    public static string? NormalizeHex(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 4 && v[0] == '#' && v[1..].All(char.IsAsciiHexDigit))
        {
            v = $"#{v[1]}{v[1]}{v[2]}{v[2]}{v[3]}{v[3]}";
        }

        return v.Length == 7 && v[0] == '#' && v[1..].All(char.IsAsciiHexDigit) ? v.ToLowerInvariantHex() : null;
    }

    private static double? Luminance(string colour)
    {
        if (NormalizeHex(colour) is not { } hex)
        {
            return null;
        }

        static double Channel(string hex, int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(hex, 1)) + (0.7152 * Channel(hex, 3)) + (0.0722 * Channel(hex, 5));
    }

    private static Dictionary<string, string> Palette(params string[] colours) =>
        Tokens.Zip(colours).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);

    private static string ToLowerInvariantHex(this string value) =>
        string.Create(value.Length, value, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.ToLowerInvariant(source[i]);
            }
        });
}
