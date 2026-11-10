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

    /// <summary>Gets or sets the logo: empty for Weesite's own, or a <c>/media/</c> image, SVG or raster (T-0116).</summary>
    public string? Logo { get; set; }

    /// <summary>Gets or sets the home page photo: empty for the logo, or a <c>/media/</c> image (T-0117).</summary>
    public string? HomePhoto { get; set; }

    /// <summary>Gets or sets the favicon: empty for the logo, or a <c>/media/</c> image, SVG or raster (T-0118).</summary>
    public string? Favicon { get; set; }

    /// <summary>Gets or sets the privacy policy page's slug, or empty for none.</summary>
    public string? PrivacyPage { get; set; }

    /// <summary>Gets or sets the terms page's slug, or empty for none.</summary>
    public string? TermsPage { get; set; }

    /// <summary>Gets or sets the footer's copyright line, with <c>{year}</c> for the current year; empty for the default.</summary>
    public string? Copyright { get; set; }

    /// <summary>Gets or sets a value indicating whether the footer says "built with Weesite".</summary>
    public bool ShowBuiltWith { get; set; } = true;

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
            Logo = site.Logo,
            HomePhoto = site.HomePhoto,
            Favicon = site.Favicon,
            PrivacyPage = site.PrivacyPage,
            TermsPage = site.TermsPage,
            Copyright = site.Copyright,
            ShowBuiltWith = !site.HideBuiltWith,
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
        var logo = Blank(Logo);
        var photo = Blank(HomePhoto);
        var favicon = Blank(Favicon);
        var name = (Name ?? "").Trim();
        var tagline = Blank(Tagline);
        var language = (Language ?? "").Trim();
        var email = Blank(ContactEmail);
        var format = (DateFormat ?? "").Trim();
        var show = HomeModes.Contains(HomeShow) ? HomeShow : null;
        var page = Blank(HomePage);
        var privacy = Blank(PrivacyPage);
        var terms = Blank(TermsPage);
        var copyright = Blank(Copyright);

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
        else if (logo is not null && !SiteLogo.IsLogoFile(logo, media))
        {
            error = "The logo must be empty, or an image from Media (SVG, PNG, JPEG, WebP or AVIF), such as /media/1a2b3c4d5e6f-logo.svg.";
        }
        else if (favicon is not null && !SiteLogo.IsLogoFile(favicon, media))
        {
            error = "The favicon must be empty, or an image from Media (SVG, PNG, JPEG, WebP or AVIF), such as /media/1a2b3c4d5e6f-icon.svg.";
        }
        else if (photo is not null && !(photo.StartsWith("/media/", StringComparison.Ordinal) && media.Find(photo["/media/".Length..]) is not null))
        {
            error = "The home page photo must be empty, or an image from Media, such as /media/1a2b3c4d5e6f-me.jpg.";
        }

        else if (copyright?.Length > 160)
        {
            error = "The copyright line can be at most 160 characters.";
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
            Set(json, "logo", logo);
            Set(json, "homePhoto", photo);
            Set(json, "favicon", favicon);
            Set(json, "privacyPage", privacy);
            Set(json, "termsPage", terms);
            Set(json, "copyright", copyright);
            if (ShowBuiltWith)
            {
                json.Remove("hideBuiltWith");
            }
            else
            {
                json["hideBuiltWith"] = true;
            }
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
