using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Pages in a real browser: structure (accessibility-tree snapshots), axe checks and keyboard use.
/// Locators go by role and name, so the tests double as accessibility checks.
/// </summary>
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed class PageTests(SiteServer site) : PageTest
{
    [Test]
    public async Task Home_page_has_the_expected_structure()
    {
        await Page.GotoAsync(site.At("/"));

        await Expect(Page).ToHaveTitleAsync("Weesite");
        await Expect(Page.Locator("body")).ToMatchAriaSnapshotAsync("""
            - link "Skip to content"
            - banner:
                - link "Weesite"
            - main:
                - heading "Weesite" [level=1]
                - paragraph
            """);
    }

    [Test]
    public async Task Unknown_path_shows_the_not_found_page()
    {
        var response = await Page.GotoAsync(site.At("/no-such-page"));

        await Assert.That(response?.Status).IsEqualTo(404);
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Page not found" })).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Go to the home page." }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(site.At("/"));
    }

    [Test]
    public async Task Skip_link_is_the_first_stop_and_visible_when_focused()
    {
        await Page.GotoAsync(site.At("/"));

        await Page.Keyboard.PressAsync("Tab");

        var skipLink = Page.GetByRole(AriaRole.Link, new() { Name = "Skip to content" });
        await Expect(skipLink).ToBeFocusedAsync();
        await Expect(skipLink).ToBeInViewportAsync();
    }

    [Test]
    [Arguments("/")]
    [Arguments("/no-such-page")]
    public async Task Page_has_no_axe_violations(string path)
    {
        await Page.GotoAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }
}
