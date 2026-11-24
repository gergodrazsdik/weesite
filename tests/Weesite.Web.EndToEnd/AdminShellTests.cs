using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>The admin's shell in the browser: the sidebar, and its Menu button on a phone (U-35).</summary>
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed partial class AdminShellTests(SiteServer site) : PageTest
{
    [Before(Test)]
    public async Task SignInAsAdmin() =>
        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { ["X-Test-Role"] = "Admin" });

    [Test]
    public async Task On_a_phone_the_menu_folds_behind_a_button_that_opens_and_closes_it()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await Page.OpenAsync(site.At("/admin/media/replace?name=06bd645d47dc-lake-trail.jpg"));
        var button = Page.GetByRole(AriaRole.Button, new() { Name = "Menu", Exact = true });
        var nav = Page.GetByRole(AriaRole.Navigation, new() { Name = "Editor" });

        // Closed at first: the page starts right under the bar.
        await Expect(button).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(button).ToHaveAttributeAsync("aria-controls", "admin-menu");
        await Expect(nav).ToBeHiddenAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" })).ToBeHiddenAsync();

        // A click opens it, with the section of the sub-page marked (U-32), the theme switch and Sign out.
        await button.ClickAsync();
        await Expect(button).ToHaveAttributeAsync("aria-expanded", "true");
        var media = nav.GetByRole(AriaRole.Link, new() { NameRegex = MediaLink() });
        await Expect(media).ToBeVisibleAsync();
        await Expect(media).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" })).ToBeVisibleAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();

        // Escape in the menu closes it and gives the focus back to the button.
        await media.FocusAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(nav).ToBeHiddenAsync();
        await Expect(button).ToBeFocusedAsync();
        await Expect(button).ToHaveAttributeAsync("aria-expanded", "false");

        // And the keyboard opens it again.
        await Page.Keyboard.PressAsync("Enter");
        await Expect(nav).ToBeVisibleAsync();
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();
    }

    [Test]
    public async Task On_a_wide_screen_the_sidebar_shows_without_a_menu_button()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await Page.OpenAsync(site.At("/admin/posts"));

        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Editor" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Menu", Exact = true })).ToBeHiddenAsync();
    }

    [Test]
    public async Task On_a_phone_the_status_tabs_wrap_and_none_is_cut_off()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await Page.OpenAsync(site.At("/admin/posts"));

        var tabs = Page.GetByRole(AriaRole.Navigation, new() { Name = "Status" }).GetByRole(AriaRole.Link);
        await Expect(tabs.Last).ToBeVisibleAsync();
        foreach (var tab in await tabs.AllAsync())
        {
            var box = await tab.BoundingBoxAsync();
            await Assert.That(box).IsNotNull();
            await Assert.That(box!.X + box.Width).IsLessThanOrEqualTo(390);
        }

        var scrolls = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        await Assert.That(scrolls).IsFalse();
    }

    [GeneratedRegex("^Media")]
    private static partial Regex MediaLink();
}
