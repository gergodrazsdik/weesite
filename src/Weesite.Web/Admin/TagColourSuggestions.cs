using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>A tag and the colour suggested for it.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="Current">Its colour now, or null.</param>
/// <param name="Suggested">The colour its group gets.</param>
internal sealed record SuggestedTag(string Tag, string? Current, string Suggested);

/// <summary>Tags about the same things, and their colour.</summary>
/// <param name="Colour">The group's colour, one of <see cref="TagDetails.Colours"/>.</param>
/// <param name="Tags">Its tags, by name.</param>
internal sealed record SuggestedGroup(string Colour, IReadOnlyList<SuggestedTag> Tags);

/// <summary>
/// Turns groups of tags into suggested colours (T-0142): one colour a group, the largest group
/// first. A group keeps the colour most of its tags already have, if no larger group took it, so
/// suggesting again doesn't reshuffle the colours; the others get the next free colour in an order
/// that puts different hues side by side.
/// </summary>
internal static class TagColourSuggestions
{
    /// <summary>Gets the colours in the order new groups get them: neighbours differ in hue.</summary>
    public static IReadOnlyList<string> Order { get; } = ["blue", "amber", "teal", "violet", "red", "green", "pink", "orange", "cyan", "slate"];

    /// <summary>Suggests a colour for each group.</summary>
    /// <param name="groups">The groups, the largest first, at most as many as there are colours.</param>
    /// <param name="site">The settings, for the colours tags have now.</param>
    /// <returns>The groups with their colours, in the same order.</returns>
    public static IReadOnlyList<SuggestedGroup> Suggest(IReadOnlyList<IReadOnlyList<string>> groups, SiteSettings site)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(site);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SuggestedGroup>();
        foreach (var group in groups.Take(Order.Count))
        {
            var colour = group
                .Select(site.ColourOf)
                .OfType<string>()
                .Where(c => !taken.Contains(c))
                .GroupBy(c => c, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => IndexOf(g.Key))
                .Select(g => g.Key)
                .FirstOrDefault()
                ?? Order.First(c => !taken.Contains(c));
            taken.Add(colour);
            result.Add(new SuggestedGroup(colour, [.. group.Order(StringComparer.Ordinal).Select(t => new SuggestedTag(t, site.ColourOf(t), colour))]));
        }

        return result;
    }

    /// <summary>The same groups with the colours an editor chose for them (T-0144): any colour that isn't a choice keeps the suggestion.</summary>
    /// <param name="groups">The suggested groups.</param>
    /// <param name="chosen">The colours chosen, by group, as posted.</param>
    /// <returns>The groups with their colours.</returns>
    public static IReadOnlyList<SuggestedGroup> WithColours(IReadOnlyList<SuggestedGroup> groups, IReadOnlyList<string?> chosen)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(chosen);
        return [.. groups.Select((g, i) => i < chosen.Count && chosen[i] is { } colour && TagDetails.Colours.Contains(colour)
            ? new SuggestedGroup(colour, [.. g.Tags.Select(t => t with { Suggested = colour })])
            : g)];
    }

    private static int IndexOf(string colour)
    {
        for (var i = 0; i < Order.Count; i++)
        {
            if (Order[i] == colour)
            {
                return i;
            }
        }

        return Order.Count;
    }
}

/// <summary>The suggested colours as posted: the rows and which of them to apply.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227", Justification = "Form binding sets the list.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
public sealed class TagColoursForm
{
    /// <summary>Gets or sets the version of <c>site.json</c> the suggestions were made from.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets the groups, each with its colour.</summary>
    public List<TagGroupChoice> Groups { get; set; } = [];

    /// <summary>Gets or sets the rows.</summary>
    public List<TagColourRow> Rows { get; set; } = [];

    /// <summary>Turns the ticked rows into a change of <c>site.json</c>: their groups' colours, descriptions kept.</summary>
    /// <param name="stored">The settings as stored now.</param>
    /// <param name="error">Why the rows can't be applied.</param>
    /// <returns>The change, or null with <paramref name="error"/>.</returns>
    internal Action<System.Text.Json.Nodes.JsonObject>? ToChange(SiteSettings stored, out string? error)
    {
        ArgumentNullException.ThrowIfNull(stored);
        error = null;
        var details = stored.Tags.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        foreach (var row in Rows.Where(r => r.Apply))
        {
            var colour = row.Group >= 0 && row.Group < Groups.Count ? Groups[row.Group].Colour : null;
            if (string.IsNullOrWhiteSpace(row.Tag) || row.Tag.Length > 100 || colour is null || !TagDetails.Colours.Contains(colour))
            {
                error = "A group's colour isn't one of the choices; suggest the colours again.";
                return null;
            }

            details[row.Tag] = (details.GetValueOrDefault(row.Tag) ?? new TagDetails()) with { Colour = colour };
        }

        return json => json["tags"] = SiteSettingsEditor.ToNode(details.OrderBy(d => d.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value));
    }
}

/// <summary>A suggested colour as posted.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
public sealed class TagColourRow
{
    /// <summary>Gets or sets the tag.</summary>
    public string? Tag { get; set; }

    /// <summary>Gets or sets the tag's group: its index in <see cref="TagColoursForm.Groups"/>.</summary>
    public int Group { get; set; }

    /// <summary>Gets or sets a value indicating whether to apply its group's colour.</summary>
    public bool Apply { get; set; }
}

/// <summary>A group's colour as posted.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
public sealed class TagGroupChoice
{
    /// <summary>Gets or sets the colour, one of <see cref="TagDetails.Colours"/>.</summary>
    public string? Colour { get; set; }
}
