using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The menu editor (T-0049).</summary>
internal sealed partial class MenuTests
{
    [Test]
    public async Task Admins_set_the_menu_in_the_order_of_its_positions()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var page = await admin.GetStringAsync(new Uri("/admin/menu", UriKind.Relative));
        using var saved = await Save(admin, page,
            ("Rows[0].Label", "Writing"), ("Rows[0].Target", "/blog"), ("Rows[0].Position", "2"),
            ("Rows[1].Label", "Who"), ("Rows[1].Target", "page:about"), ("Rows[1].Position", "1"),
            ("Rows[2].Label", "Topics"), ("Rows[2].Href", "/blog/tags"), ("Rows[2].Position", "3"));
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var json = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));

        await Assert.That(page).Contains("value=\"Blog\"");
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/menu?saved=1");
        await Assert.That(MenuLinks(home)).IsEquivalentTo(["/about Who", "/blog Writing", "/blog/tags Topics"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(json).Contains("\"page\": \"about\"");
        await Assert.That(json).DoesNotContain("null");
    }

    [Test]
    public async Task An_empty_menu_falls_back_to_the_blog_and_menu_pages()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var page = await admin.GetStringAsync(new Uri("/admin/menu", UriKind.Relative));
        using var saved = await Save(admin, page, ("Rows[0].Label", ""), ("Rows[0].Target", ""), ("Rows[1].Label", ""), ("Rows[1].Target", ""));
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));

        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).DoesNotContain("\"menu\"");
        await Assert.That(MenuLinks(home)).Contains("/blog Blog");
    }

    [Test]
    [Arguments("Rows[0].Href", "javascript:alert(1)", "needs a page, the blog, or an address")]
    [Arguments("Rows[0].Href", "//evil.example/", "needs a page, the blog, or an address")]
    [Arguments("Rows[0].Target", "page:no-such-page", "points to a page that isn't published")]
    [Arguments("Rows[0].Label", "", "needs a label")]
    public async Task Invalid_items_are_refused_with_a_reason(string field, string value, string message)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        var before = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));
        (string, string)[] fields = [("Rows[0].Label", "Item"), ("Rows[0].Target", ""), ("Rows[0].Href", "/x")];

        var page = await admin.GetStringAsync(new Uri("/admin/menu", UriKind.Relative));
        using var response = await Save(admin, page, [.. fields.Where(f => f.Item1 != field), (field, value), ("Rows[1].Label", ""), ("Rows[1].Target", "")]);

        await Assert.That(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync())).Contains(message);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).IsEqualTo(before);
    }

    [Test]
    public async Task Authors_cant_change_the_menu()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        using var response = await author.GetAsync(new Uri("/admin/menu", UriKind.Relative));

        await Assert.That(response.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
    }

    private static List<string> MenuLinks(string html) =>
        [.. MenuLink().Matches(Nav().Match(html).Value).Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")];

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
            ["_handler"] = "menu",
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value,
            ["Form.ETag"] = WebUtility.HtmlDecode(ETag().Match(page).Groups[1].Value),
        };
        foreach (var (name, value) in fields)
        {
            values["Form." + name] = value;
        }

        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(new Uri("/admin/menu", UriKind.Relative), form);
    }

    [GeneratedRegex("<nav class=\"site-menu\".*?</nav>", RegexOptions.Singleline)]
    private static partial Regex Nav();

    [GeneratedRegex("<a href=\"([^\"]+)\"[^>]*>([^<]+)</a>")]
    private static partial Regex MenuLink();

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("name=\"Form.ETag\" value=\"([^\"]*)\"")]
    private static partial Regex ETag();
}
