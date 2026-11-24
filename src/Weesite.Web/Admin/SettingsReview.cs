using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>A setting an imported <c>site.json</c> would change.</summary>
/// <param name="Key">Which setting: its resource string is <c>ImportSetting</c> followed by it.</param>
/// <param name="From">Its value on the site now; null when it's not set.</param>
/// <param name="To">Its value in the backup; null when it's not set.</param>
internal sealed record SettingChange(string Key, string? From, string? To);

/// <summary>
/// What an imported <c>site.json</c> would change (T-0177), shown in the import's review before
/// anything is written: where contact messages go and the other settings that send visitors' data
/// or the site's somewhere, or change what the site publishes. The admins and the sign-in come
/// from the deployment's configuration, not from <c>site.json</c>, so an import can't change them.
/// </summary>
internal static class SettingsReview
{
    // Each setting: its key and its value as the review shows it.
    private static readonly (string Key, Func<SiteSettings, string?> Value)[] Shown =
    [
        ("Name", s => s.Name),
        ("ContactForm", s => OnOff(s.ContactForm?.Enabled == true)),
        ("ContactRecipient", s => s.ContactForm?.Recipient),
        ("ContactPage", s => s.ContactForm?.Page),
        ("ContactEmail", s => s.ContactEmail),
        ("Comments", s => OnOff(s.Comments)),
        ("Reactions", s => OnOff(s.Reactions)),
        ("VisitorCountries", s => OnOff(s.VisitorCountries)),
        ("IndexNow", s => OnOff(s.IndexNow?.Enabled == true)),
        ("PrivacyPage", s => s.PrivacyPage),
        ("TermsPage", s => s.TermsPage),
        ("Links", s => Join(s.Links.Select(l => l.Address?.ToString()))),
        ("MenuLinks", s => Join(s.Menu.Select(m => m.Href))),
        ("Language", s => s.Language),
        ("TimeZone", s => s.TimeZone),
    ];

    /// <summary>Gets the shown settings that differ between the site's <c>site.json</c> and an imported one.</summary>
    /// <param name="current">The site's file, or null when it has none (after a reset): then the defaults.</param>
    /// <param name="imported">The imported file, already checked to parse.</param>
    /// <returns>The changes, in the review's order.</returns>
    public static IReadOnlyList<SettingChange> Compare(string? current, string imported)
    {
        var before = Read(current);
        var after = Read(imported);
        return [.. Shown
            .Select(s => new SettingChange(s.Key, Blank(s.Value(before)), Blank(s.Value(after))))
            .Where(c => !string.Equals(c.From, c.To, StringComparison.Ordinal))];
    }

    private static SiteSettings Read(string? json)
    {
        try
        {
            return SiteSettings.Parse(json ?? "{}");
        }
        catch (System.Text.Json.JsonException)
        {
            return SiteSettings.Parse("{}");
        }
    }

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string? Join(IEnumerable<string?> values) =>
        string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v))) is { Length: > 0 } joined ? joined : null;

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
