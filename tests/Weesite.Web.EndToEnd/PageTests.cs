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
    public async Task The_contents_stay_in_view_while_the_post_scrolls()
    {
        await Page.SetViewportSizeAsync(1280, 600);
        await Page.OpenAsync(site.At("/blog/notes-on-static-rendering"));
        var contents = Page.GetByRole(AriaRole.Navigation, new() { Name = "On this page" });

        await Page.GetByRole(AriaRole.Link, new() { Name = "An image and a download" }).ClickAsync();
        await Page.Mouse.WheelAsync(0, 400);

        await Expect(contents).ToBeInViewportAsync();
    }

    [Test]
    public async Task The_built_with_link_opens_in_a_new_tab_and_says_so()
    {
        await Page.OpenAsync(site.At("/"));
        var link = Page.GetByRole(AriaRole.Contentinfo).GetByRole(AriaRole.Link, new() { Name = "Weesite (opens in a new tab)" });

        await Expect(link).ToHaveAttributeAsync("target", "_blank");
        await Expect(link).ToHaveAttributeAsync("rel", "noopener");
    }

    [Test]
    public async Task A_link_to_a_file_has_a_download_icon()
    {
        await Page.OpenAsync(site.At("/blog/notes-on-static-rendering"));
        var link = Page.GetByRole(AriaRole.Link, new() { Name = "vector drawing" });

        var icon = await link.EvaluateAsync<string>("a => getComputedStyle(a, '::before').maskImage");

        await Assert.That(icon).StartsWith("url(\"data:image/svg+xml");
        await Expect(link).ToHaveAttributeAsync("target", "_blank");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "vector drawing (download, opens in a new tab)" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task A_printed_post_is_its_content_only_in_light_colours()
    {
        // T-0074: printing leaves out the header, the side column and the pager, in the light
        // colours even when the reader chose dark, and code wraps instead of scrolling.
        await Page.OpenAsync(site.At("/blog/notes-on-static-rendering"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Dark" }).ClickAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");

        await Page.EmulateMediaAsync(new() { Media = Media.Print });

        await Expect(Page.GetByRole(AriaRole.Banner)).ToBeHiddenAsync();
        await Expect(Page.GetByRole(AriaRole.Complementary)).ToBeHiddenAsync();
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Older and newer posts" })).ToBeHiddenAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Notes on static rendering", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Contentinfo).GetByText("Built with")).ToBeHiddenAsync();
        await Assert.That(await Page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).colorScheme")).IsEqualTo("light");
        await Assert.That(await Page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('pre.code')).whiteSpace")).IsEqualTo("pre-wrap");
    }

    [Test]
    public async Task A_printed_page_shows_the_address_of_each_outside_link()
    {
        await Page.OpenAsync(site.At("/terms"));
        await Page.EmulateMediaAsync(new() { Media = Media.Print });

        var after = await Page.GetByRole(AriaRole.Link, new() { Name = "Creative Commons Attribution 4.0 International licence (CC BY 4.0)" })
            .EvaluateAsync<string>("link => getComputedStyle(link, '::after').content");

        await Assert.That(after).IsEqualTo("\" (https://creativecommons.org/licenses/by/4.0/)\"");
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
    public async Task Hungarian_and_other_subsets_load_their_font_files()
    {
        var fonts = new List<string>();
        Page.RequestFinished += (_, request) =>
        {
            if (request.ResourceType == "font")
            {
                fonts.Add(new Uri(request.Url).AbsolutePath);
            }
        };

        await Page.OpenAsync(site.At("/typography"));
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // T-0098: the subsets a page uses load, each once; ő and ű come from Latin2.
        await Assert.That(fonts).Contains("/fonts/plex-sans-1.1.0/IBMPlexSans-Regular-Latin2.woff2");
        await Assert.That(fonts).Contains("/fonts/plex-sans-1.1.0/IBMPlexSans-Bold-Latin2.woff2");
        await Assert.That(fonts).Contains("/fonts/plex-mono-2.5.0/IBMPlexMono-Regular-Latin2.woff2");
        await Assert.That(fonts).Contains("/fonts/plex-sans-1.1.0/IBMPlexSans-Regular-Latin3.woff2");
        await Assert.That(fonts).Contains("/fonts/plex-sans-1.1.0/IBMPlexSans-Regular-Pi.woff2");
        await Assert.That(fonts).HasDistinctItems();
        await Assert.That(await Page.EvaluateAsync<bool>("() => document.fonts.check('16px \"IBM Plex Sans\"', 'őűŐŰáéíóöúü')")).IsTrue();
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
    [Arguments("/typography")]
    [Arguments("/privacy")]
    [Arguments("/terms")]
    [Arguments("/no-such-page")]
    public async Task Page_has_no_axe_or_csp_violations(string path)
    {
        await Page.WatchCspAsync();
        var response = await Page.OpenAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();

        // T-0053: the strict policy is sent, and nothing on the page breaks it.
        await Assert.That(await response!.HeaderValueAsync("content-security-policy")).Contains("script-src 'self'");
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();
    }
}
