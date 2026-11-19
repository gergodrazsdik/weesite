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
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/menu");
        await Assert.That(MenuLinks(home)).IsEquivalentTo(["/about Who", "/blog Writing", "/blog/tags Topics"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(json).Contains("\"page\": \"about\"");
        await Assert.That(json).DoesNotContain("null");
    }

    [Test]
    public async Task The_menu_goes_to_the_contact_form_and_shows_each_rows_address()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory, EditorRole.Admin);
        using var visitor = factory.CreateClient();

        var page = await admin.GetStringAsync(new Uri("/admin/menu", UriKind.Relative));
        using var saved = await Save(admin, page,
            ("Rows[0].Label", "Write to me"), ("Rows[0].Target", "/contact"), ("Rows[0].Position", "1"),
            ("Rows[1].Label", "Who"), ("Rows[1].Target", "page:about"), ("Rows[1].Position", "2"));
        var home = await visitor.GetStringAsync(new Uri("/", UriKind.Relative));
        var json = await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"));
        var again = await admin.GetStringAsync(new Uri("/admin/menu", UriKind.Relative));

        await Assert.That(page).Contains("<option value=\"/contact\" data-address=\"/contact\">Contact form</option>");
        await Assert.That(MenuLinks(home)).IsEquivalentTo(["/contact Write to me", "/about Who"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(json).Contains("\"href\": \"/contact\"");
        await Assert.That(again).Contains("<option value=\"/contact\" data-address=\"/contact\" selected>Contact form</option>");

        // A row with a target shows where it goes, not the example; only address rows keep that.
        await Assert.That(again).Contains("id=\"menu-href-0\" class=\"mono-input\" name=\"Form.Rows[0].Href\" value=\"/contact\" maxlength=\"300\" />");
        await Assert.That(again).Contains("id=\"menu-href-1\" class=\"mono-input\" name=\"Form.Rows[1].Href\" value=\"/about\" maxlength=\"300\" />");
        await Assert.That(again).Contains("id=\"menu-href-2\" class=\"mono-input\" name=\"Form.Rows[2].Href\" value=\"\" maxlength=\"300\" placeholder=\"/tags\" />");
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
    [Arguments("Rows[0].Href", "/\\evil.example/", "needs a page, the blog, or an address")]
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

        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(WebUtility.HtmlDecode(html)).Contains(message);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json"))).IsEqualTo(before);
        await FieldErrors.AssertMarkedAsync(html, field switch { "Rows[0].Href" => "menu-href-0", "Rows[0].Target" => "menu-target-0", _ => "menu-label-0" });
    }

    [Test]
    public async Task Authors_cant_change_the_menu()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var author = Client(factory, EditorRole.Author);

        using var response = await author.GetAsync(new Uri("/admin/menu", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
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
            ["__RequestVerificationToken"] = Forms.Token().Match(page).Groups[1].Value,
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

    [GeneratedRegex("name=\"Form.ETag\" value=\"([^\"]*)\"")]
    private static partial Regex ETag();
}
