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
        await Page.OpenAsync(site.At("/"));

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
                - region "Example Site":
                    - heading "Example Site" [level=1]
                    - paragraph: A sample Weesite
                - region "Latest posts":
                    - heading "Latest posts" [level=2]
                    - heading "2026" [level=2]
                    - list:
                        - listitem:
                            - heading "Moving from Bicep to OpenTofu" [level=3]
                        - listitem:
                            - heading "TOML, YAML and the configs in between" [level=3]
                    - link "All posts, by year"
                - complementary "More on this site":
                    - region "Tags":
                        - heading "Tags" [level=2]
                    - region "Subscribe":
                        - heading "Subscribe" [level=2]
            """);
    }

    [Test]
    public async Task Reader_goes_from_home_to_a_post()
    {
        await Page.OpenAsync(site.At("/"));

        await Page.GetByRole(AriaRole.Link, new() { Name = "Moving from Bicep to OpenTofu" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(site.At("/blog/moving-from-bicep-to-opentofu"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Moving from Bicep to OpenTofu", Level = 1 })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Post_shows_related_posts_by_meaning()
    {
        await Page.OpenAsync(site.At("/blog/hello-world"));

        var related = Page.GetByRole(AriaRole.Region, new() { Name = "Related posts" });
        await Expect(related.GetByRole(AriaRole.Link, new() { Name = "Notes on static rendering" })).ToBeVisibleAsync();
        await Expect(related.GetByRole(AriaRole.Link, new() { Name = "Hello, world" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Code_blocks_get_a_copy_button_and_posts_their_contents()
    {
        // Without the permission, headless Chromium can leave the clipboard write waiting on a
        // prompt nobody answers, so the status never changes (F-0041).
        await Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await Page.OpenAsync(site.At("/blog/notes-on-static-rendering"));
        var block = Page.Locator(".code-block").First;

        await Expect(block.Locator(".bar")).ToContainTextAsync("csharp");
        await block.GetByRole(AriaRole.Button, new() { Name = "Copy" }).ClickAsync();
        await Expect(block.GetByRole(AriaRole.Status)).ToHaveTextAsync("Code copied");

    }

    [Test]
    public async Task On_this_page_links_stay_on_the_post_and_mark_the_section_being_read()
    {
        await Page.OpenAsync(site.At("/blog/notes-on-static-rendering"));
        var contents = Page.GetByRole(AriaRole.Navigation, new() { Name = "On this page" });

        await Expect(contents.GetByRole(AriaRole.Link, new() { Name = "A list" })).ToHaveAttributeAsync("aria-current", "true");

        await contents.GetByRole(AriaRole.Link, new() { Name = "Some code" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(site.At("/blog/notes-on-static-rendering#some-code"));
        await Expect(contents.GetByRole(AriaRole.Link, new() { Name = "Some code" })).ToHaveAttributeAsync("aria-current", "true");
        await Expect(contents.GetByRole(AriaRole.Link, new() { Name = "A list" })).Not.ToHaveAttributeAsync("aria-current", "true");
    }

    [Test]
    public async Task Reader_searches_from_the_header()
    {
        await Page.OpenAsync(site.At("/"));

        var search = Page.GetByRole(AriaRole.Search, new() { Name = "Search the site" });
        await search.GetByRole(AriaRole.Searchbox).FillAsync("who writes this");
        await search.GetByRole(AriaRole.Searchbox).PressAsync("Enter");

        await Expect(Page).ToHaveURLAsync(site.At("/search?q=who+writes+this"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Results for “who writes this”" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "About" }).Last).ToBeVisibleAsync();
    }

    [Test]
    public async Task Menu_marks_the_current_page()
    {
        await Page.OpenAsync(site.At("/"));

        await Page.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "About" }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "About", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "About" })).ToHaveAttributeAsync("aria-current", "page");
    }

    [Test]
    public async Task Unknown_path_shows_the_not_found_page()
    {
        var response = await Page.OpenAsync(site.At("/no-such-page"));

        await Assert.That(response?.Status).IsEqualTo(404);
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Page not found" })).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Go to the home page." }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(site.At("/"));
    }

    [Test]
    public async Task Skip_link_is_the_first_stop_and_visible_when_focused()
    {
        await Page.OpenAsync(site.At("/"));

        await Page.Keyboard.PressAsync("Tab");

        var skipLink = Page.GetByRole(AriaRole.Link, new() { Name = "Skip to content" });
        await Expect(skipLink).ToBeFocusedAsync();
        await Expect(skipLink).ToBeInViewportAsync();
    }

    [Test]
    public async Task Skip_link_stays_on_the_page()
    {
        await Page.OpenAsync(site.At("/blog"));

        await Page.Keyboard.PressAsync("Tab");
        await Page.Keyboard.PressAsync("Enter");

        await Expect(Page).ToHaveURLAsync(site.At("/blog#main"));
    }

    [Test]
    public async Task Theme_switch_changes_the_mode_without_reloading_the_page()
    {
        await Page.OpenAsync(site.At("/blog"));
        await Page.EvaluateAsync("() => { window.notReloaded = true; }");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Dark" }).ClickAsync();

        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Dark" })).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "System" })).ToHaveAttributeAsync("aria-pressed", "false");
        await Assert.That(await Page.EvaluateAsync<bool>("() => window.notReloaded === true")).IsTrue();

        // The choice is kept: a fresh load renders it from the cookie.
        await Page.ReloadAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");

        await Page.GetByRole(AriaRole.Button, new() { Name = "System" }).ClickAsync();
        await Expect(Page.Locator("html")).Not.ToHaveAttributeAsync("data-theme", "dark");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "System" })).ToHaveAttributeAsync("aria-pressed", "true");
    }

    [Test]
    [Arguments("/")]
    [Arguments("/blog/notes-on-static-rendering")]
    public async Task Page_has_no_axe_violations_in_dark_mode(string path)
    {
        await Page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await Page.OpenAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    [Arguments("/")]
    [Arguments("/blog")]
    [Arguments("/blog/hello-world")]
    [Arguments("/blog/notes-on-static-rendering")]
    [Arguments("/blog/tags")]
    [Arguments("/blog/tags/web")]
    [Arguments("/blog/walking-the-south-coast-of-crete")]
    [Arguments("/blog/notes-on-dialing-in-espresso")]
    [Arguments("/blog/a-small-rego-baseline-for-infrastructure")]
    [Arguments("/search?q=rendering")]
    [Arguments("/admin/sign-in")]
    [Arguments("/about")]
    [Arguments("/no-such-page")]
    public async Task Page_has_no_axe_violations(string path)
    {
        await Page.OpenAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }
}
