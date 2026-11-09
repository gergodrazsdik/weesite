using System.Text.Json.Nodes;
using Weesite.Content;
using Weesite.Web.Theming;

namespace Weesite.Web.Admin;

/// <summary>
/// The theme page's form (T-0049): one hex colour field per token and mode, in
/// <see cref="ThemePalette.Tokens"/> order; an empty field keeps the default.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227", Justification = "Form binding sets the lists.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
public sealed class ThemeForm
{
    /// <summary>Gets or sets the version of <c>site.json</c> opened.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets the light colours, by token index.</summary>
    public List<string?> Light { get; set; } = [];

    /// <summary>Gets or sets the dark colours, by token index.</summary>
    public List<string?> Dark { get; set; } = [];

    /// <summary>Gets a form filled from the settings.</summary>
    /// <param name="opened">The settings and their version.</param>
    /// <returns>The form.</returns>
    public static ThemeForm From(OpenedSettings opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        var theme = opened.Settings.Theme;
        return new ThemeForm
        {
            ETag = opened.ETag,
            Light = [.. ThemePalette.Tokens.Select(t => theme?.Light.GetValueOrDefault(t))],
            Dark = [.. ThemePalette.Tokens.Select(t => theme?.Dark.GetValueOrDefault(t))],
        };
    }

    /// <summary>Gets the list for a mode.</summary>
    /// <param name="mode"><c>light</c> or <c>dark</c>.</param>
    /// <returns>The list, padded to one entry per token.</returns>
    public List<string?> For(string mode)
    {
        var list = mode == "dark" ? (Dark ??= []) : (Light ??= []);
        while (list.Count < ThemePalette.Tokens.Count)
        {
            list.Add(null);
        }

        return list;
    }

    /// <summary>Fills every field from a palette (a preset), or clears them all (null: the defaults).</summary>
    /// <param name="palette">The colours per mode and token, or null.</param>
    public void Fill(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? palette)
    {
        foreach (var mode in ThemePalette.Modes)
        {
            var list = For(mode);
            for (var i = 0; i < ThemePalette.Tokens.Count; i++)
            {
                list[i] = palette?[mode][ThemePalette.Tokens[i]];
            }
        }
    }

    /// <summary>Reads the colours set; anything that isn't a hex colour becomes a message.</summary>
    /// <param name="error">What's wrong, if anything.</param>
    /// <returns>The overrides per mode and token (normalized <c>#rrggbb</c>).</returns>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Overrides(out string? error)
    {
        error = null;
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var mode in ThemePalette.Modes)
        {
            var set = new Dictionary<string, string>(StringComparer.Ordinal);
            var list = For(mode);
            for (var i = 0; i < ThemePalette.Tokens.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(list[i]))
                {
                    continue;
                }

                if (ThemePalette.NormalizeHex(list[i]) is { } hex)
                {
                    set[ThemePalette.Tokens[i]] = hex;
                }
                else
                {
                    error ??= $"{ThemePalette.Tokens[i]} ({mode}) isn't a colour such as #1d4ed8.";
                }
            }

            result[mode] = set;
        }

        return result;
    }

    /// <summary>Gets the change that writes the overrides to <c>site.json</c>.</summary>
    /// <param name="overrides">The checked overrides.</param>
    /// <returns>The change.</returns>
    public static Action<JsonObject> ToChange(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        return json =>
        {
            if (overrides.Values.All(m => m.Count == 0))
            {
                json.Remove("theme");
                return;
            }

            var theme = new JsonObject();
            foreach (var (mode, colours) in overrides)
            {
                theme[mode] = SiteSettingsEditor.ToNode(colours);
            }

            json["theme"] = theme;
        };
    }
}
