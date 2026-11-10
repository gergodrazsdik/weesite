using Microsoft.AspNetCore.Mvc.Testing;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The media page's count of the posts and pages using each file (T-0112).</summary>
internal sealed class MediaUsesTests
{
    [Test]
    public async Task Each_file_says_where_its_used_and_unused_ones_can_be_listed_alone()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        admin.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        var all = await admin.GetStringAsync(new Uri("/admin/media", UriKind.Relative));
        var unused = await admin.GetStringAsync(new Uri("/admin/media?show=unused", UriKind.Relative));

        var diagram = Card(all, "0e3d1bf78702-diagram.svg");
        await Assert.That(diagram).Contains("<summary>Used in 1</summary>");
        await Assert.That(diagram).Contains("<a href=\"/admin/edit?path=posts%2Fnotes-on-static-rendering.md\">Notes on static rendering</a>");
        await Assert.That(unused).DoesNotContain(">0e3d1bf78702-diagram.svg<");
        await Assert.That(unused).Contains("aria-current=\"page\">Unused <span>");
        await Assert.That(Count(unused, "Not used")).IsEqualTo(Count(all, "Not used"));
        await Assert.That(unused).DoesNotContain("<summary>Used in");
    }

    private static string Card(string page, string name)
    {
        var start = page.IndexOf($">{name}<", StringComparison.Ordinal);
        var end = page.IndexOf("</li>", page.IndexOf("class=\"copy\"", start, StringComparison.Ordinal), StringComparison.Ordinal);
        return page[start..end];
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;
}
