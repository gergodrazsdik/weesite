using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>The profile page's form: an editor's byline as text fields (ADR 0005).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
public sealed class ProfileForm
{
    /// <summary>The number of link rows the form shows.</summary>
    public const int LinkRows = 4;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the short bio.</summary>
    public string? Bio { get; set; }

    /// <summary>Gets or sets the links, one row each; empty rows are ignored.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227", Justification = "Form binding sets the list.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
    public List<ProfileLink> Links { get; set; } = [];

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
            Links = [.. (profile?.Links ?? []).Select(l => new ProfileLink { Label = l.Label, Url = l.Address?.ToString() })],
        };
        form.Pad();
        return form;
    }

    /// <summary>Fills the link rows up to <see cref="LinkRows"/>.</summary>
    public void Pad()
    {
        Links ??= [];
        while (Links.Count < LinkRows)
        {
            Links.Add(new ProfileLink());
        }
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
            > 80 => "The name can be at most 80 characters.",
            _ => bio?.Length > 300 ? "The bio can be at most 300 characters." : null,
        };

        var links = new List<SiteLink>();
        foreach (var row in (Links ?? []).Take(LinkRows))
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

        return error is null ? new Author { Id = authorId, Name = name, Bio = bio, Links = links } : null;
    }
}

/// <summary>One link row of the profile form.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "Part of a Razor component's form parameter.")]
public sealed class ProfileLink
{
    /// <summary>Gets or sets the label, such as <c>Website</c>.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the address.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056", Justification = "A form field holds the text as typed.")]
    public string? Url { get; set; }
}
