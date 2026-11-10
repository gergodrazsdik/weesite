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
