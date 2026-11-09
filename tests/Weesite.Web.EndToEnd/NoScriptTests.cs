using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>The public site works without JavaScript; scripts may only enhance it.</summary>
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed class NoScriptTests(SiteServer site) : BrowserTest
{
    [Test]
    public async Task Pages_work_with_JavaScript_disabled()
    {
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();

        await page.OpenAsync(site.At("/"));

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Example Site", Level = 1 })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Example Site" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(site.At("/"));
    }
}
