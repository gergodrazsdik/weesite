using System.Text.Json.Nodes;
using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>The tags page's form (ADR 0006): for each tag, a colour from a fixed palette and a description.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227", Justification = "Form binding sets the list.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
public sealed class TagsForm
{
    /// <summary>Gets or sets the version of <c>site.json</c> opened.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets one row per tag in use.</summary>
    public List<TagRow> Tags { get; set; } = [];

    /// <summary>Gets a form filled from the settings and the tags in use.</summary>
    /// <param name="opened">The settings and their version.</param>
    /// <param name="tags">The tags in use.</param>
    /// <returns>The form.</returns>
    public static TagsForm From(OpenedSettings opened, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(opened);
        var site = opened.Settings;
        return new TagsForm
        {
            ETag = opened.ETag,
            Tags = [.. tags.Order(StringComparer.Ordinal).Select(t => new TagRow { Tag = t, Colour = site.ColourOf(t) ?? "", Description = site.DescriptionOf(t) ?? "" })],
        };
    }

    /// <summary>Gets the field the last error is about, if any (F-0081).</summary>
    public FieldError? ErrorField { get; private set; }

    /// <summary>Checks the form and returns the change to apply to <c>site.json</c>.</summary>
    /// <param name="stored">The settings as stored, to keep details of tags no post uses now.</param>
    /// <param name="error">What's wrong, if anything.</param>
    /// <returns>The change, or null with <paramref name="error"/> set.</returns>
    public Action<JsonObject>? ToChange(SiteSettings stored, out string? error)
    {
        ArgumentNullException.ThrowIfNull(stored);
        error = null;
        ErrorField = null;
        var rows = (Tags ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Tag)).ToList();
        for (var i = 0; i < (Tags ?? []).Count && error is null; i++)
        {
            var row = Tags![i];
            if (string.IsNullOrWhiteSpace(row.Tag))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(row.Colour) && !TagDetails.Colours.Contains(row.Colour))
            {
                (error, ErrorField) = ($"#{row.Tag} has a colour that isn't one of the choices.", new($"tag-colour-{i}"));
            }
            else if ((row.Description ?? "").Trim().Length > 160)
            {
                (error, ErrorField) = ($"The description of #{row.Tag} can be at most 160 characters.", new($"tag-description-{i}"));
            }
        }

        if (error is not null)
        {
            return null;
        }

        var shown = rows.Select(r => r.Tag!.Trim()).ToHashSet(StringComparer.Ordinal);
        var details = stored.Tags.Where(t => !shown.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var colour = string.IsNullOrEmpty(row.Colour) ? null : row.Colour;
            var description = string.IsNullOrWhiteSpace(row.Description) ? null : row.Description.Trim();
            if (colour is not null || description is not null)
            {
                details[row.Tag!.Trim()] = new TagDetails { Colour = colour, Description = description };
            }
        }

        return json =>
        {
            json.Remove("topics");
            if (details.Count == 0)
            {
                json.Remove("tags");
            }
            else
            {
                json["tags"] = SiteSettingsEditor.ToNode(details.OrderBy(d => d.Key, StringComparer.Ordinal).ToDictionary(d => d.Key, d => d.Value));
            }
        };
    }
}

/// <summary>A tag's row.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
public sealed class TagRow
{
    /// <summary>Gets or sets the tag.</summary>
    public string? Tag { get; set; }

    /// <summary>Gets or sets the colour: one of <see cref="TagDetails.Colours"/>, or empty for none.</summary>
    public string? Colour { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }
}
