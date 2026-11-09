using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Editor profiles: each editor is an author with a byline (ADR 0005).</summary>
internal sealed partial class ProfileTests
{
    [Test]
    public async Task An_editors_profile_becomes_the_byline_of_their_posts()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);
        using var visitor = factory.CreateClient();
        var id = EditorRights.AuthorId("editor@example.com");
        await File.WriteAllTextAsync(
            Path.Combine(copy.Root, "content", "posts", "by-an-editor.md"),
            $"---\ntitle: By an editor\nslug: by-an-editor\ndate: 2026-11-09\nauthor: {id}\n---\nText.\n");
        await factory.Services.GetRequiredService<Weesite.Content.ContentLoader>().LoadAsync(CancellationToken.None);

        var before = await visitor.GetStringAsync(new Uri("/blog/by-an-editor", UriKind.Relative));
        var form = await author.GetStringAsync(new Uri("/admin/profile", UriKind.Relative));
        using var saved = await Save(author, form, ("Name", "Ada Example"), ("Bio", "Writes about <b>tests</b>."), ("Links[0].Label", "Website"), ("Links[0].Url", "https://example.org/ada"));
        var after = await visitor.GetStringAsync(new Uri("/blog/by-an-editor", UriKind.Relative));
        var again = await author.GetStringAsync(new Uri("/admin/profile", UriKind.Relative));
        var stored = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "authors.json"));

        await Assert.That(before).DoesNotContain("Ada Example");
        await Assert.That(form).Contains("value=\"Test Editor\"");
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/profile?saved=1");
        await Assert.That(after).Contains("by Ada Example");
        await Assert.That(after).Contains("Writes about &lt;b&gt;tests&lt;/b&gt;.");
        await Assert.That(after).Contains("<a href=\"https://example.org/ada\" rel=\"me\">Website <span aria-hidden=\"true\">↗</span></a>");
        await Assert.That(after).Contains("\"name\":\"Ada Example\"");
        await Assert.That(again).Contains("value=\"https://example.org/ada\"");
        await Assert.That(stored).Contains(id);
        await Assert.That(stored).DoesNotContain("editor@example.com");
    }

    [Test]
    [Arguments("Name", "", "Enter the name")]
    [Arguments("Links[0].Url", "javascript:alert(1)", "needs an address starting with https://")]
    [Arguments("Links[0].Label", "", "needs a label")]
    public async Task Invalid_profiles_are_refused_with_a_reason(string field, string value, string message)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);
        var form = await author.GetStringAsync(new Uri("/admin/profile", UriKind.Relative));
        (string, string)[] fields = [("Name", "Ada"), ("Links[0].Label", "Website"), ("Links[0].Url", "https://example.org/")];

        using var response = await Save(author, form, [.. fields.Where(f => f.Item1 != field), (field, value)]);
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(WebUtility.HtmlDecode(html)).Contains(message);
        await Assert.That(File.Exists(Path.Combine(copy.Root, "settings", "authors.json"))).IsFalse();
    }

    [Test]
    public async Task Signed_out_visitors_cant_open_the_profile_page()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var visitor = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await visitor.GetAsync(new Uri("/admin/profile", UriKind.Relative));

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
            ["_handler"] = "profile",
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value,
        };
        foreach (var (name, value) in fields)
        {
            values["Form." + name] = value;
        }

        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(new Uri("/admin/profile", UriKind.Relative), form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
