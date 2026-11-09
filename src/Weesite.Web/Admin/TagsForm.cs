using System.Text.Json.Nodes;
using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>
/// The tags page's form (ADR 0006): a name for each topic colour slot, and for each tag its topic
/// (a slot) and description.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227", Justification = "Form binding sets the lists.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
public sealed class TagsForm
{
    /// <summary>Gets or sets the version of <c>site.json</c> opened.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets one row per colour slot, in <see cref="Topic.Colours"/> order.</summary>
    public List<TopicRow> Topics { get; set; } = [];

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
            Topics = [.. Topic.Colours.Select(c => new TopicRow { Colour = c, Name = site.Topics.FirstOrDefault(t => t.Colour == c)?.Name ?? "" })],
            Tags = [.. tags.Order(StringComparer.Ordinal).Select(t => new TagRow { Tag = t, Topic = site.TopicOf(t)?.Colour ?? "", Description = site.DescriptionOf(t) ?? "" })],
        };
    }

    /// <summary>Checks the form and returns the change to apply to <c>site.json</c>.</summary>
    /// <param name="stored">The settings as stored, to keep descriptions of tags no post uses now.</param>
    /// <param name="error">What's wrong, if anything.</param>
    /// <returns>The change, or null with <paramref name="error"/> set.</returns>
    public Action<JsonObject>? ToChange(SiteSettings stored, out string? error)
    {
        ArgumentNullException.ThrowIfNull(stored);
        error = null;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in Topics ?? [])
        {
            var name = (row.Name ?? "").Trim();
            if (!Topic.Colours.Contains(row.Colour) || name.Length == 0)
            {
                continue;
            }

            if (name.Length > 20)
            {
                error ??= "A topic's name can be at most 20 characters.";
            }
            else if (names.ContainsValue(name))
            {
                error ??= $"Two topics are called '{name}'.";
            }

            names[row.Colour!] = name;
        }

        var rows = (Tags ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Tag)).ToList();
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.Topic) && !names.ContainsKey(row.Topic))
            {
                error ??= $"#{row.Tag} is in a topic without a name: name the topic or choose another.";
            }

            if ((row.Description ?? "").Trim().Length > 160)
            {
                error ??= $"The description of #{row.Tag} can be at most 160 characters.";
            }
        }

        if (error is not null)
        {
            return null;
        }

        var topics = Topic.Colours.Where(names.ContainsKey).Select(c => new Topic
        {
            Name = names[c],
            Colour = c,
            Tags = [.. rows.Where(r => r.Topic == c).Select(r => r.Tag!.Trim())],
        }).ToList();
        var shown = rows.Select(r => r.Tag!.Trim()).ToHashSet(StringComparer.Ordinal);
        var details = stored.Tags.Where(t => !shown.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Description)))
        {
            details[row.Tag!.Trim()] = new TagDetails { Description = row.Description!.Trim() };
        }

        return json =>
        {
            if (topics.Count == 0)
            {
                json.Remove("topics");
            }
            else
            {
                json["topics"] = SiteSettingsEditor.ToNode(topics);
            }

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

/// <summary>A topic colour slot's row.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
public sealed class TopicRow
{
    /// <summary>Gets or sets the slot: one of <see cref="Topic.Colours"/>.</summary>
    public string? Colour { get; set; }

    /// <summary>Gets or sets the topic's name; empty leaves the slot unused.</summary>
    public string? Name { get; set; }
}

/// <summary>A tag's row.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
public sealed class TagRow
{
    /// <summary>Gets or sets the tag.</summary>
    public string? Tag { get; set; }

    /// <summary>Gets or sets the topic's colour slot, or empty for none.</summary>
    public string? Topic { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }
}
