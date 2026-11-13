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
        await Assert.That(after).Contains("<a href=\"https://example.org/ada\" rel=\"me noopener\" target=\"_blank\">Website <span aria-hidden=\"true\">↗</span>");
        await Assert.That(after).Contains("\"name\":\"Ada Example\"");
        await Assert.That(again).Contains("value=\"https://example.org/ada\"");
        await Assert.That(stored).Contains(id);
        await Assert.That(stored).DoesNotContain("editor@example.com");

        // F-0077: audited, by address, naming the profile by its id.
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Actor == "editor@example.com" && e.Action == "profile-saved" && e.Target == $"authors.json ({id})")).IsTrue();
    }

    [Test]
    [Arguments("Name", "", "Enter the name")]
    [Arguments("Links[0].Url", "javascript:alert(1)", "needs an address starting with https://")]
    [Arguments("Links[0].Url", "http://example.org/", "needs an address starting with https://")]
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
        await FieldErrors.AssertMarkedAsync(html, field switch { "Name" => "name", "Links[0].Url" => "link-url-0", _ => "link-label-0" });
    }

    [Test]
    public async Task The_first_sign_in_gives_a_byline_and_a_saved_profile_is_kept()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        var profiles = site.Services.GetRequiredService<Weesite.Content.AuthorProfiles>();
        var id = EditorRights.AuthorId("new@example.com");
        await File.WriteAllTextAsync(
            Path.Combine(copy.Root, "content", "posts", "by-a-new-editor.md"),
            $"---\ntitle: By a new editor\nslug: by-a-new-editor\ndate: 2026-11-09\nauthor: {id}\n---\nText.\n");
        await site.Services.GetRequiredService<Weesite.Content.ContentLoader>().LoadAsync(CancellationToken.None);

        // F-0088: no profile yet, so the sign-in name becomes one; later sign-ins leave it alone.
        await SignIn.EnsureProfileAsync(profiles, "New@Example.com", "  Grace Example ", CancellationToken.None);
        await SignIn.EnsureProfileAsync(profiles, "new@example.com", "Another Name", CancellationToken.None);
        await SignIn.EnsureProfileAsync(profiles, "nameless@example.com", null, CancellationToken.None);
        await SignIn.EnsureProfileAsync(profiles, "mail@example.com", "mail@example.com", CancellationToken.None);
        var post = await site.CreateClient().GetStringAsync(new Uri("/blog/by-a-new-editor", UriKind.Relative));

        await Assert.That((await profiles.FindAsync(id, CancellationToken.None))?.Name).IsEqualTo("Grace Example");
        await Assert.That(await profiles.FindAsync(EditorRights.AuthorId("nameless@example.com"), CancellationToken.None)).IsNull();
        await Assert.That(await profiles.FindAsync(EditorRights.AuthorId("mail@example.com"), CancellationToken.None)).IsNull();
        await Assert.That(post).Contains("by Grace Example");
    }

    [Test]
    [Arguments("Ada Example", "Ada Example")]
    [Arguments(null, null)]
    [Arguments("ada@example.com", null)]
    public async Task Only_a_name_the_provider_gave_is_offered_for_the_byline(string? name, string? expected)
    {
        // Without a name, the identity's name is the email address (F-0092).
        var user = EditorClaims.Principal("Google", "sub", "ada@example.com", name, EditorRole.Author);

        await Assert.That(EditorClaims.ProvidedName(user)).IsEqualTo(expected);
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
