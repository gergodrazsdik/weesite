using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>The profile page's form: an editor's byline as text fields (ADR 0005).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
public sealed class ProfileForm
{
    /// <summary>The number of link rows the form shows.</summary>
    public const int LinkRows = 4;

    /// <summary>The longest name a profile has.</summary>
    public const int MaxName = 80;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the short bio.</summary>
    public string? Bio { get; set; }

    /// <summary>Gets or sets the links, one row each; empty rows are ignored.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227", Justification = "Form binding sets the list.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
    public List<LinkRow> Links { get; set; } = [];

    /// <summary>Gets a form filled from a profile, or with the sign-in name when there's none.</summary>
    /// <param name="profile">The stored profile, if any.</param>
    /// <param name="signInName">The name the sign-in gave.</param>
    /// <returns>The form, with <see cref="LinkRows"/> link rows.</returns>
    public static ProfileForm From(Author? profile, string? signInName)
    {
        var form = new ProfileForm
        {
            Name = profile?.Name ?? signInName ?? "",
            Bio = profile?.Bio,
            Links = LinkRow.For(profile?.Links, LinkRows),
        };
        return form;
    }

    /// <summary>Fills the link rows up to <see cref="LinkRows"/>.</summary>
    public void Pad()
    {
        Links ??= [];
        LinkRow.Pad(Links, LinkRows);
    }

    /// <summary>Turns the form into a profile; what's malformed becomes a message instead.</summary>
    /// <param name="authorId">The editor's author ID.</param>
    /// <param name="error">What's wrong, if anything.</param>
    /// <returns>The profile, or null with <paramref name="error"/> set.</returns>
    public Author? ToAuthor(string authorId, out string? error)
    {
        var name = (Name ?? "").Trim();
        var bio = string.IsNullOrWhiteSpace(Bio) ? null : Bio.Trim();
        error = name.Length switch
        {
            0 => "Enter the name to show on your posts.",
            > MaxName => $"The name can be at most {MaxName} characters.",
            _ => bio?.Length > 300 ? "The bio can be at most 300 characters." : null,
        };

        var links = LinkRow.Validate(Links, LinkRows, ref error);

        return error is null ? new Author { Id = authorId, Name = name, Bio = bio, Links = links } : null;
    }
}

/// <summary>One link row of a form (profile, site settings): a label and an address.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
public sealed class LinkRow
{
    /// <summary>Gets or sets the label, such as <c>Website</c>.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the address.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056", Justification = "A form field holds the text as typed.")]
    public string? Url { get; set; }

    /// <summary>Gets rows for links, padded with empty rows up to <paramref name="count"/>.</summary>
    /// <param name="links">The stored links.</param>
    /// <param name="count">The number of rows to show.</param>
    /// <returns>The rows.</returns>
    public static List<LinkRow> For(IEnumerable<SiteLink>? links, int count)
    {
        List<LinkRow> rows = [.. (links ?? []).Select(l => new LinkRow { Label = l.Label, Url = l.Address?.ToString() })];
        Pad(rows, count);
        return rows;
    }

    /// <summary>Adds empty rows up to <paramref name="count"/>.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="count">The number of rows to show.</param>
    public static void Pad(List<LinkRow> rows, int count)
    {
        ArgumentNullException.ThrowIfNull(rows);
        while (rows.Count < count)
        {
            rows.Add(new LinkRow());
        }
    }

    /// <summary>Turns rows into links: empty rows are skipped; a missing label or a non-web address sets <paramref name="error"/>.</summary>
    /// <param name="rows">The posted rows.</param>
    /// <param name="count">The most rows read.</param>
    /// <param name="error">The first problem found, kept if already set.</param>
    /// <returns>The valid links.</returns>
    public static IReadOnlyList<SiteLink> Validate(IEnumerable<LinkRow>? rows, int count, ref string? error)
    {
        var links = new List<SiteLink>();
        foreach (var row in (rows ?? []).Take(count))
        {
            var label = (row.Label ?? "").Trim();
            var url = (row.Url ?? "").Trim();
            if (label.Length == 0 && url.Length == 0)
            {
                continue;
            }

            if (label.Length is 0 or > 40)
            {
                error ??= "Each link needs a label of at most 40 characters.";
            }
            else if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
            {
                error ??= $"The link '{label}' needs an address starting with https://.";
            }
            else
            {
                links.Add(new SiteLink { Label = label, Address = address });
            }
        }

        return links;
    }
}
