using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Weesite.Web.Tests;

/// <summary>The addresses sign-in providers link to (T-0072).</summary>
internal sealed class LegalLinksTests
{
    [Test]
    [Arguments("/legal/privacy", "/privacy")]
    [Arguments("/legal/terms", "/terms")]
    public async Task Legal_addresses_redirect_to_the_pages_chosen_in_settings(string path, string page)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo(page);
    }

    [Test]
    public async Task Without_a_chosen_page_they_are_not_found()
    {
        using var copy = new SiteCopy();
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"privacyPage\": \"privacy\"", "\"privacyPage\": \"no-such-page\"", StringComparison.Ordinal));
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/legal/privacy", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task The_microsoft_association_file_names_the_sign_in_app()
    {
        await using var site = new SiteFactory();
        await using var configured = site.WithWebHostBuilder(b => b.UseSetting("Authentication:Microsoft:ClientId", "11111111-2222-3333-4444-555555555555"));
        using var client = configured.CreateClient();
        using var plain = site.CreateClient();

        using var response = await client.GetAsync(new Uri("/.well-known/microsoft-identity-association.json", UriKind.Relative));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var missing = await plain.GetAsync(new Uri("/.well-known/microsoft-identity-association.json", UriKind.Relative));

        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/json");
        await Assert.That(json.RootElement.GetProperty("associatedApplications")[0].GetProperty("applicationId").GetString()).IsEqualTo("11111111-2222-3333-4444-555555555555");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}

/// <summary>Links that leave the page open in a new tab, and say so (T-0115).</summary>
internal sealed class NewTabLinksTests
{
    [Test]
    public async Task Profile_and_feed_links_open_in_a_new_tab_but_email_doesnt()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        var home = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        var post = await client.GetStringAsync(new Uri("/blog/notes-on-static-rendering", UriKind.Relative));

        await Assert.That(home).Contains("<a href=\"https://example.com/\" rel=\"me noopener\" target=\"_blank\">Website");
        await Assert.That(home).Contains("Website<span class=\"visually-hidden\"> (opens in a new tab)</span></a></li>");
        await Assert.That(home).Contains("target=\"_blank\" rel=\"noopener\">Atom<span class=\"visually-hidden\"> (opens in a new tab)</span></a>");
        await Assert.That(home).Contains("target=\"_blank\" rel=\"noopener\">JSON Feed<span class=\"visually-hidden\"> (opens in a new tab)</span></a>");
        await Assert.That(home).DoesNotContain("href=\"mailto:author@example.com\" target");
        await Assert.That(post).Contains("rel=\"me noopener\" target=\"_blank\">Website <span aria-hidden=\"true\">↗</span><span class=\"visually-hidden\"> (opens in a new tab)</span></a>");
    }
}
