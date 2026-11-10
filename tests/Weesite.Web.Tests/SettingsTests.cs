using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The site settings editor (T-0049).</summary>
internal sealed partial class SettingsTests
{
    private static readonly (string, string)[] Valid =
    [
        ("Name", "Renamed Site"), ("Tagline", "A new tagline"), ("Language", "en-GB"), ("ContactEmail", "hello@example.org"),
        ("DateFormat", "d MMMM yyyy"), ("HomeShow", "latest-posts"), ("Links[0].Label", "Code"), ("Links[0].Url", "https://example.org/code"),
    ];

    [Test]
    public async Task Admins_change_the_settings_and_the_rest_of_site_json_stays()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var saved = await Save(admin, page, Valid);
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var post = await visitor.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var json = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        await Assert.That(page).Contains("value=\"Example Site\"");
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/settings?saved=1");
        await Assert.That(home).Contains("Renamed Site");
        await Assert.That(home).Contains("<a href=\"https://example.org/code\" rel=\"me\">Code</a>");
        await Assert.That(home).Contains("hello@example.org");
        await Assert.That(home).Contains("lang=\"en-GB\"");
        await Assert.That(post).Contains("January 2026</time>");
        await Assert.That(json).Contains("\"example-author\"");
        await Assert.That(json).Contains("\"menu\"");
        await Assert.That(json).Contains("\"dateFormat\": \"d MMMM yyyy\"");
    }

    [Test]
    public async Task The_privacy_and_terms_pages_are_chosen_in_settings_and_linked_from_the_footer()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        // The sample site sets both (T-0096, T-0097).
        var before = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var privacy = await visitor.GetStringAsync(new Uri("/privacy", UriKind.Relative));
        var terms = await visitor.GetStringAsync(new Uri("/terms", UriKind.Relative));

        // Choosing none removes the link.
        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var saved = await Save(admin, page, [.. Valid, ("PrivacyPage", ""), ("TermsPage", "about")]);
        var after = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var json = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        await Assert.That(before).Contains("<a href=\"/privacy\">Privacy</a>");
        await Assert.That(before).Contains("<a href=\"/terms\">Terms</a>");
        await Assert.That(privacy).Contains("Privacy policy");
        await Assert.That(terms).Contains("CC BY 4.0");
        await Assert.That(page).Contains("<option value=\"privacy\" selected");
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/settings?saved=1");
        await Assert.That(after).DoesNotContain(">Privacy</a>");
        await Assert.That(after).Contains("<a href=\"/about\">Terms</a>");
        await Assert.That(json).DoesNotContain("privacyPage");
        await Assert.That(json).Contains("\"termsPage\": \"about\"");
    }

    [Test]
    [Arguments("Name", "", "Enter the site's name")]
    [Arguments("Language", "xx-not-a-language", "The language must be a code")]
    [Arguments("ContactEmail", "not an address", "isn't an email address")]
    [Arguments("DateFormat", "%", "date format isn't one")]
    [Arguments("HomeShow", "page", "Choose a published page")]
    [Arguments("PrivacyPage", "no-such-page", "Choose a published page for the privacy policy")]
    [Arguments("TermsPage", "no-such-page", "Choose a published page for the terms")]
    [Arguments("Links[0].Url", "ftp://example.org/", "needs an address starting with https://")]
    public async Task Invalid_settings_are_refused_with_a_reason(string field, string value, string message)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var before = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var response = await Save(admin, page, [.. Valid.Where(f => f.Item1 != field), (field, value)]);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync())).Contains(message);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).IsEqualTo(before);
    }

    [Test]
    public async Task A_save_over_someone_elses_is_refused()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var first = await Save(admin, page, Valid);
        using var second = await Save(admin, page, [.. Valid.Where(f => f.Item1 != "Name"), ("Name", "Second")]);

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(WebUtility.HtmlDecode(await second.Content.ReadAsStringAsync())).Contains("Someone saved the settings after you opened them");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).Contains("Renamed Site");
    }

    [Test]
    public async Task Authors_cant_change_the_settings()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        using var response = await author.GetAsync(new Uri("/admin/settings", UriKind.Relative));

        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    private static async Task<HttpResponseMessage> Save(HttpClient client, string page, params (string Name, string Value)[] fields)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "settings",
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value,
            ["Form.ETag"] = WebUtility.HtmlDecode(ETag().Match(page).Groups[1].Value),
        };
        foreach (var (name, value) in fields)
        {
            values["Form." + name] = value;
        }

        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(new Uri("/admin/settings", UriKind.Relative), form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("name=\"Form.ETag\" value=\"([^\"]*)\"")]
    private static partial Regex ETag();
}
