using System.Globalization;
using System.Net.Mail;
using System.Text.Json.Nodes;
using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>The site settings page's form: identity, contact, links, dates and the home page (T-0049).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
public sealed class SettingsForm
{
    /// <summary>The number of link rows the form shows.</summary>
    public const int LinkRows = 6;

    /// <summary>The values <see cref="HomeShow"/> takes.</summary>
    public static readonly IReadOnlyList<string> HomeModes = ["latest-posts", "page", "page-and-posts"];

    /// <summary>Gets or sets the version of <c>site.json</c> opened.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets the site's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the tagline.</summary>
    public string? Tagline { get; set; }

    /// <summary>Gets or sets the language code, such as <c>en</c>.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets or sets the contact address shown in the footer.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Gets or sets the date format.</summary>
    public string DateFormat { get; set; } = SiteSettings.DefaultDateFormat;

    /// <summary>Gets or sets what the home page shows.</summary>
    public string HomeShow { get; set; } = "latest-posts";

    /// <summary>Gets or sets the home page's page slug.</summary>
    public string? HomePage { get; set; }

    /// <summary>Gets or sets the banner: empty for the built-in drawing, <c>none</c>, or a <c>/media/</c> image.</summary>
    public string? Banner { get; set; }

    /// <summary>Gets or sets the privacy policy page's slug, or empty for none.</summary>
    public string? PrivacyPage { get; set; }

    /// <summary>Gets or sets the terms page's slug, or empty for none.</summary>
    public string? TermsPage { get; set; }

    /// <summary>Gets or sets the site's links.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227", Justification = "Form binding sets the list.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "Form binding fills a list by index.")]
    public List<LinkRow> Links { get; set; } = [];

    /// <summary>Gets a form filled from the settings.</summary>
    /// <param name="opened">The settings and their version.</param>
    /// <returns>The form.</returns>
    public static SettingsForm From(OpenedSettings opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        var site = opened.Settings;
        return new SettingsForm
        {
            ETag = opened.ETag,
            Name = site.Name,
            Tagline = site.Tagline,
            Language = site.Language,
            ContactEmail = site.ContactEmail,
            DateFormat = site.DateFormat,
            HomeShow = site.Home.Show,
            HomePage = site.Home.Page,
            Banner = site.Banner,
            PrivacyPage = site.PrivacyPage,
            TermsPage = site.TermsPage,
            Links = LinkRow.For(site.Links, LinkRows),
        };
    }

    /// <summary>Fills the link rows up to <see cref="LinkRows"/>.</summary>
    public void Pad()
    {
        Links ??= [];
        LinkRow.Pad(Links, LinkRows);
    }

    /// <summary>Checks the form and returns the change to apply to <c>site.json</c>.</summary>
    /// <param name="pages">The slugs of the published pages, for the home page.</param>
    /// <param name="media">The media index, to check the banner is a stored image.</param>
    /// <param name="error">What's wrong, if anything.</param>
    /// <returns>The change, or null with <paramref name="error"/> set.</returns>
    public Action<JsonObject>? ToChange(IReadOnlyCollection<string> pages, Weesite.Content.Media.MediaIndex media, out string? error)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(media);
        var banner = Blank(Banner);
        var name = (Name ?? "").Trim();
        var tagline = Blank(Tagline);
        var language = (Language ?? "").Trim();
        var email = Blank(ContactEmail);
        var format = (DateFormat ?? "").Trim();
        var show = HomeModes.Contains(HomeShow) ? HomeShow : null;
        var page = Blank(HomePage);
        var privacy = Blank(PrivacyPage);
        var terms = Blank(TermsPage);

        error = null;
        if (name.Length is 0 or > 80)
        {
            error = "Enter the site's name, at most 80 characters.";
        }
        else if (tagline?.Length > 160)
        {
            error = "The tagline can be at most 160 characters.";
        }
        else if (!IsLanguage(language))
        {
            error = "The language must be a code such as en, en-GB or hu.";
        }
        else if (email is not null && !IsEmail(email))
        {
            error = "The contact address isn't an email address.";
        }
        else if (!IsDateFormat(format))
        {
            error = "The date format isn't one .NET can use, such as yyyy-MM-dd or d MMMM yyyy.";
        }
        else if (show is null)
        {
            error = "Choose what the home page shows.";
        }
        else if (show != "latest-posts" && (page is null || !pages.Contains(page)))
        {
            error = "Choose a published page for the home page.";
        }
        else if (banner is not null && banner != "none" && !(banner.StartsWith("/media/", StringComparison.Ordinal) && media.Find(banner["/media/".Length..]) is not null))
        {
            error = "The banner must be empty, none, or an image from Media, such as /media/1a2b3c4d5e6f-sea.jpg.";
        }

        else if (privacy is not null && !pages.Contains(privacy))
        {
            error = "Choose a published page for the privacy policy, or none.";
        }
        else if (terms is not null && !pages.Contains(terms))
        {
            error = "Choose a published page for the terms, or none.";
        }

        var links = LinkRow.Validate(Links, LinkRows, ref error);
        if (error is not null)
        {
            return null;
        }

        return json =>
        {
            json["name"] = name;
            Set(json, "tagline", tagline);
            json["language"] = language;
            Set(json, "contactEmail", email);
            json["dateFormat"] = format;
            json["home"] = show == "latest-posts" ? new JsonObject { ["show"] = show } : new JsonObject { ["show"] = show, ["page"] = page };
            json["links"] = SiteSettingsEditor.ToNode(links);
            Set(json, "banner", banner);
            Set(json, "privacyPage", privacy);
            Set(json, "termsPage", terms);
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Set(JsonObject json, string key, string? value)
    {
        if (value is null)
        {
            json.Remove(key);
        }
        else
        {
            json[key] = value;
        }
    }

    private static bool IsLanguage(string code)
    {
        if (code.Length is 0 or > 35)
        {
            return false;
        }

        try
        {
            return !CultureInfo.GetCultureInfo(code, predefinedOnly: true).Equals(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static bool IsEmail(string address) =>
        address.Length <= 254 && MailAddress.TryCreate(address, out var parsed) && parsed.Address == address;

    private static bool IsDateFormat(string format)
    {
        if (format.Length is 0 or > 40)
        {
            return false;
        }

        try
        {
            _ = new DateOnly(2026, 1, 15).ToString(format, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
