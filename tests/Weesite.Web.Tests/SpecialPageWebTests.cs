using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Special pages on the site (T-0159): pages without a slug that Site settings place.</summary>
internal sealed class SpecialPageWebTests
{
    [Test]
    public async Task The_privacy_and_terms_pages_are_at_their_addresses_and_nowhere_else()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var privacy = await client.GetStringAsync(new Uri("/privacy", UriKind.Relative));
        var sitemap = await client.GetStringAsync(new Uri("/sitemap.xml", UriKind.Relative));
        using var legal = await client.GetAsync(new Uri("/legal/privacy", UriKind.Relative));

        await Assert.That(privacy).Contains("Privacy policy");
        await Assert.That(legal.Headers.Location?.ToString()).IsEqualTo("/privacy");
        // Placed special pages are public pages: in the sitemap, and in search (T-0159).
        await Assert.That(sitemap).Contains("/privacy</loc>");
        await Assert.That(sitemap).Contains("/contact</loc>");

        // Its link preview card is there too.
        var card = System.Text.RegularExpressions.Regex.Match(privacy, "/og/[^\"]+\\.png").Value;
        using var image = await client.GetAsync(new Uri(card, UriKind.Relative));
        await Assert.That(image.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task The_admin_lists_special_pages_with_their_addresses()
    {
        await using var root = new SiteFactory();
        await using var factory = root.WithTestSignIn();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", EditorRole.Admin.ToString());

        var pages = await admin.GetStringAsync(new Uri("/admin/pages", UriKind.Relative));

        await Assert.That(pages).Contains(">/privacy</span>");
        await Assert.That(pages).Contains(">/terms</span>");
        await Assert.That(pages).Contains(">/contact</span>");
    }

    [Test]
    public async Task A_page_that_left_contact_doesnt_take_the_contact_forms_address_with_it()
    {
        // The maintainer's UAT (T-0159): the sample contact page renamed to contact2 kept
        // "contact" as an earlier slug, and /contact sent visitors to it, away from the form.
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(
            Path.Combine(copy.Root, "content", "pages", "contact2.md"),
            "---\nkind: page\ntitle: Contact two\nslug: contact2\nredirect_from:\n- contact\n---\n\nText.\n");
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var contact = await client.GetAsync(new Uri("/contact", UriKind.Relative));

        await Assert.That(contact.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await contact.Content.ReadAsStringAsync()).Contains("name=\"form-token\"");
    }

    [Test]
    public async Task A_chosen_page_that_isnt_published_stays_chosen()
    {
        using var copy = new SiteCopy();
        var privacy = Path.Combine(copy.Root, "content", "pages", "privacy.md");
        await File.WriteAllTextAsync(privacy, (await File.ReadAllTextAsync(privacy)).Replace("lang: en\n", "lang: en\nstatus: draft\n", StringComparison.Ordinal));
        await using var root = new SiteFactory(copy.Root);
        await using var factory = root.WithTestSignIn();
        using var admin = Admin(factory);
        using var visitor = factory.CreateClient();

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var content = new FormUrlEncodedContent(Forms.Values(page, "settings"));
        using var saved = await admin.PostAsync(new Uri("/admin/settings", UriKind.Relative), content);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json")))!;
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        using var missing = await visitor.GetAsync(new Uri("/privacy", UriKind.Relative));

        // A save of the page as it is keeps the draft (F-0190).
        await Assert.That(page).Contains("<option value=\"pages/privacy.md\" selected>Privacy policy (not published)</option>");
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(json["privacyPage"]?.GetValue<string>()).IsEqualTo("pages/privacy.md");
        await Assert.That(home).DoesNotContain(">Privacy</a>");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_chosen_file_that_isnt_a_special_page_stays_in_the_list_and_is_named()
    {
        using var copy = new SiteCopy();
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"privacyPage\": \"pages/privacy.md\"", "\"privacyPage\": \"privacy\"", StringComparison.Ordinal));
        await using var root = new SiteFactory(copy.Root);
        await using var factory = root.WithTestSignIn();
        using var admin = Admin(factory);

        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var content = new FormUrlEncodedContent(Forms.Values(page, "settings"));
        using var saved = await admin.PostAsync(new Uri("/admin/settings", UriKind.Relative), content);

        // A value from before special pages (a slug) shows, and the save says to choose again.
        await Assert.That(page).Contains("<option value=\"privacy\" selected>privacy: no longer a special page; choose another</option>");
        await Assert.That(WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync())).Contains("Choose a special page (a page without a slug) for the privacy policy");
    }

    [Test]
    public async Task The_editor_saves_a_special_page_and_previews_it()
    {
        using var copy = new SiteCopy();
        await using var root = new SiteFactory(copy.Root);
        await using var factory = root.WithTestSignIn();
        using var admin = Admin(factory);

        var form = await admin.GetStringAsync(new Uri("/admin/new/page", UriKind.Relative));
        var values = Forms.Values(form, "editor").Where(f => f.Key is not ("Form.Title" or "Form.Slug" or "Form.Body" or "Form.Special" or "Form.Status")).ToList();
        values.AddRange([new("Form.Title", "Cookie notice"), new("Form.Special", "true"), new("Form.Body", "No cookies."), new("Form.Status", "draft")]);
        using var content = new FormUrlEncodedContent(values);
        using var saved = await admin.PostAsync(new Uri("/admin/new/page", UriKind.Relative), content);
        var file = Path.Combine(copy.Root, "content", "pages", "cookie-notice.md");
        var preview = await admin.GetStringAsync(new Uri("/admin/preview/special/cookie-notice", UriKind.Relative));
        var list = await admin.GetStringAsync(new Uri("/admin/pages?status=draft", UriKind.Relative));

        await Assert.That(form).Contains("id=\"special\"");
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(File.Exists(file)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(file)).DoesNotContain("slug:");
        await Assert.That(preview).Contains("No cookies.");
        await Assert.That(list).Contains("special page, not placed");
    }

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", EditorRole.Admin.ToString());
        return client;
    }
}
