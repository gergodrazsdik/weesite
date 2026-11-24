using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The admin's shell (AdminLayout): the sidebar's marked section and its counts.</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed partial class AdminShellTests(SharedSite shared)
{
    [Test]
    [Arguments("/admin", "/admin/posts")]
    [Arguments("/admin/versions?path=posts%2Fnotes-on-static-rendering.md", "/admin/posts")]
    [Arguments("/admin/export?path=posts%2Fnotes-on-static-rendering.md", "/admin/posts")]
    [Arguments("/admin/delete?path=posts%2Fnotes-on-static-rendering.md", "/admin/posts")]
    [Arguments("/admin/export?path=pages%2Fabout.md", "/admin/pages")]
    [Arguments("/admin/delete?path=pages%2Fabout.md", "/admin/pages")]
    [Arguments("/admin/versions?path=pages%2Fabout.md", "/admin/pages")]
    [Arguments("/admin/new/page", "/admin/pages")]
    [Arguments("/admin/media/replace?name=06bd645d47dc-lake-trail.jpg", "/admin/media")]
    [Arguments("/admin/media/archive?name=06bd645d47dc-lake-trail.jpg", "/admin/media")]
    [Arguments("/admin/media/delete?name=06bd645d47dc-lake-trail.jpg", "/admin/media")]
    [Arguments("/admin/analytics/posts", "/admin/analytics")]
    [Arguments("/admin/tags/suggest", "/admin/tags")]
    [Arguments("/admin/reset", "/admin/settings")]
    public async Task A_sub_page_marks_its_section_in_the_sidebar(string page, string section)
    {
        using var admin = Client(shared.Factory);

        var html = await admin.GetStringAsync(new Uri(page, UriKind.Relative));

        // U-32: the section's link, and only it, says it's the current one.
        var marked = Current().Matches(Sidebar(html)).Select(m => m.Groups[1].Value).ToList();
        await Assert.That(marked).IsEquivalentTo([section]);
    }

    [Test]
    public async Task Totals_are_plain_and_an_action_count_is_a_badge_only_when_something_waits()
    {
        await using var root = new SiteFactory();
        await using var factory = root.WithTestSignIn();
        using var admin = Client(factory);

        var html = Sidebar(await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative)));

        // U-33: totals stay plain numbers; no unread messages, so no badge, but still read out.
        await Assert.That(html).Contains("aria-current=\"page\">Posts<span><span class=\"visually-hidden\">, </span>");
        await Assert.That(html).Contains(">Messages<span class=\"visually-hidden\"><span class=\"visually-hidden\">, </span>0<span class=\"visually-hidden\"> unread</span></span></a>");
        await Assert.That(html).DoesNotContain("class=\"badge\"");
    }

    [Test]
    public async Task The_phone_menu_button_controls_the_menu()
    {
        using var admin = Client(shared.Factory);

        var html = await admin.GetStringAsync(new Uri("/admin/posts", UriKind.Relative));

        // U-35: a disclosure button, closed until the script opens it, for the element it names.
        await Assert.That(html).Contains("<button class=\"btn small menu-toggle\" type=\"button\" aria-expanded=\"false\" aria-controls=\"admin-menu\">");
        await Assert.That(html).Contains("<div class=\"admin-menu\" id=\"admin-menu\">");
        await Assert.That(html).Contains("admin/nav-menu.");
    }

    private static string Sidebar(string html) =>
        html[html.IndexOf("<nav aria-label=\"Editor\">", StringComparison.Ordinal)..html.IndexOf("</nav>", html.IndexOf("<nav aria-label=\"Editor\">", StringComparison.Ordinal), StringComparison.Ordinal)];

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));
        return client;
    }

    [GeneratedRegex("<a href=\"([^\"]+)\" aria-current=\"page\">")]
    private static partial Regex Current();
}
