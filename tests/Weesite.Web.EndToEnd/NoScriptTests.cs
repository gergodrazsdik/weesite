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

    [Test]
    public async Task A_post_has_no_share_button_without_javascript()
    {
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();

        await page.OpenAsync(site.At("/blog/hello-world"));

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Hello, world", Level = 1 })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Share", Exact = true })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "LinkedIn (opens in a new tab)" })).ToBeHiddenAsync();
    }

    [Test]
    public async Task The_theme_switch_posts_its_form_without_javascript()
    {
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();

        await page.OpenAsync(site.At("/about"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Light" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(site.At("/about"));
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Light" })).ToHaveAttributeAsync("aria-pressed", "true");
    }
}
