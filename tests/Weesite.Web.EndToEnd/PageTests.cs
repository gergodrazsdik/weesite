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

        await Expect(Page).ToHaveTitleAsync("Example Site");
        await Expect(Page.GetByRole(AriaRole.Banner)).ToMatchAriaSnapshotAsync("""
            - banner:
                - link "Example Site"
                - navigation "Main":
                    - list:
                        - listitem:
                            - link "Blog"
                        - listitem:
                            - link "About"
            """);
        await Expect(Page.GetByRole(AriaRole.Main)).ToMatchAriaSnapshotAsync("""
            - main:
                - heading "Example Site" [level=1]
                - paragraph: A sample Weesite
                - region "Latest posts":
                    - heading "Latest posts" [level=2]
                    - list:
                        - listitem:
                            - article:
                                - heading "Notes on static rendering" [level=3]
                        - listitem:
                            - article:
                                - heading "Hello, world" [level=3]
            """);
    }

    [Test]
    public async Task Reader_goes_from_home_to_a_post()
    {
        await Page.GotoAsync(site.At("/"));

        await Page.GetByRole(AriaRole.Link, new() { Name = "Hello, world" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(site.At("/blog/hello-world"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Hello, world", Level = 1 })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Menu_marks_the_current_page()
    {
        await Page.GotoAsync(site.At("/"));

        await Page.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "About" }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "About", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "About" })).ToHaveAttributeAsync("aria-current", "page");
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
    public async Task Theme_switch_changes_the_mode_without_javascript_help()
    {
        await Page.GotoAsync(site.At("/blog"));

        await Page.GetByRole(AriaRole.Button, new() { Name = "Dark" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(site.At("/blog"));
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Dark" })).ToHaveAttributeAsync("aria-pressed", "true");
    }

    [Test]
    [Arguments("/")]
    [Arguments("/blog/notes-on-static-rendering")]
    public async Task Page_has_no_axe_violations_in_dark_mode(string path)
    {
        await Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await Page.GotoAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    [Arguments("/")]
    [Arguments("/blog")]
    [Arguments("/blog/hello-world")]
    [Arguments("/blog/notes-on-static-rendering")]
    [Arguments("/about")]
    [Arguments("/no-such-page")]
    public async Task Page_has_no_axe_violations(string path)
    {
        await Page.GotoAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }
}
