using System.Text.Json.Nodes;
using Microsoft.Extensions.Localization;
using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>Something that uses a media file, as the media pages list it.</summary>
/// <param name="Title">A post's or page's title, or the site setting, such as <c>Site settings: Logo</c>.</param>
/// <param name="Href">Where to change it: the editor, or the setting's field.</param>
internal sealed record MediaUse(string Title, string Href);

/// <summary>
/// What uses a media file (T-0112, F-0237): the posts and pages from the catalog, and the site
/// settings that point at it (the logo, the favicon, the banner, the home page photo). The media
/// page's Used and Unused tabs, the delete and archive warnings and the replacement all count
/// both, so the logo is never "unused".
/// </summary>
internal static class MediaUses
{
    // The keys of site.json that hold a media address, with the settings page's field and label.
    private static readonly (string Key, string Field, string Label, Func<SiteSettings, string?> Value)[] Settings =
    [
        ("logo", "logo", "LogoLabel", s => s.Logo),
        ("favicon", "favicon", "FaviconLabel", s => s.Favicon),
        ("banner", "banner", "BannerLabel", s => s.Banner),
        ("homePhoto", "home-photo", "HomePhotoLabel", s => s.HomePhoto),
    ];

    /// <summary>Gets the keys of <c>site.json</c> that use a file, such as <c>logo</c>.</summary>
    /// <param name="site">The site settings.</param>
    /// <param name="name">The file's name.</param>
    /// <returns>The keys; empty when no setting uses it.</returns>
    public static IReadOnlyList<string> SettingsKeys(SiteSettings site, string name)
    {
        ArgumentNullException.ThrowIfNull(site);
        var address = "/media/" + name;
        return [.. Settings.Where(s => string.Equals(s.Value(site)?.Trim(), address, StringComparison.Ordinal)).Select(s => s.Key)];
    }

    /// <summary>Gets whether anything that isn't archived uses a file: a post or page, or a site setting.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="name">The file's name.</param>
    /// <returns>True when it's used.</returns>
    public static bool IsUsed(ContentCatalog catalog, string name)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.ActiveUsesOf(name).Count > 0 || SettingsKeys(catalog.Site, name).Count > 0;
    }

    /// <summary>Lists what uses a file: the site settings first, then the posts and pages.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="name">The file's name.</param>
    /// <param name="text">The admin's strings, for the settings' labels.</param>
    /// <param name="archived">Whether archived posts and pages count too (a replacement changes them as well).</param>
    /// <returns>The uses; empty when nothing uses it.</returns>
    public static IReadOnlyList<MediaUse> Of(ContentCatalog catalog, string name, IStringLocalizer text, bool archived = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(text);
        var keys = SettingsKeys(catalog.Site, name);
        var settings = Settings.Where(s => keys.Contains(s.Key)).Select(s => new MediaUse(text["SettingsUse", text[s.Label]], "/admin/settings#" + s.Field));
        var items = (archived ? catalog.UsesOf(name) : catalog.ActiveUsesOf(name))
            .Select(i => new MediaUse(i.Title, "/admin/edit?path=" + Uri.EscapeDataString(i.SourcePath)));
        return [.. settings, .. items];
    }

    /// <summary>
    /// Gets a change to <c>site.json</c> that points the settings using one address at another
    /// (F-0237), for <see cref="SiteSettingsEditor.SaveAsync"/>. It works on the file as read, so
    /// a setting changed meanwhile is left as it now is.
    /// </summary>
    /// <param name="from">The old address, such as <c>/media/1a2b3c4d5e6f-logo.svg</c>.</param>
    /// <param name="to">The new address.</param>
    /// <returns>The change.</returns>
    public static Action<JsonObject> PointSettingsAt(string from, string to) => json =>
    {
        foreach (var (key, _, _, _) in Settings)
        {
            if (json[key] is JsonValue value && value.TryGetValue<string>(out var text) && string.Equals(text.Trim(), from, StringComparison.Ordinal))
            {
                json[key] = to;
            }
        }
    };
}
