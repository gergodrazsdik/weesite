using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>The editor in a real browser, signed in as an admin: ws-md-editor and accessibility.</summary>
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed class EditorTests(SiteServer site) : PageTest
{
    [Before(Test)]
    public async Task SignInAsAdmin() =>
        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { ["X-Test-Role"] = "Admin" });

    [Test]
    public async Task Toolbar_formats_the_selection_and_the_live_preview_renders_it()
    {
        await Page.GotoAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");

        await body.FillAsync("plain word");
        await body.EvaluateAsync("t => t.setSelectionRange(6, 10)");
        await Page.GetByRole(AriaRole.Toolbar, new() { Name = "Formatting" }).GetByRole(AriaRole.Button, new() { Name = "Bold" }).ClickAsync();

        await Expect(body).ToHaveValueAsync("plain **word**");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" }).Locator("strong")).ToHaveTextAsync("word");
    }

    [Test]
    public async Task Control_s_saves_and_publishes()
    {
        await Page.GotoAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").FillAsync("Written in the browser");
        await Page.GetByLabel("Slug").FillAsync("written-in-the-browser");
        await Page.GetByLabel("Published").CheckAsync();
        await Page.GetByLabel("Text (Markdown)").FillAsync("Saved with the keyboard.");

        await Page.GetByLabel("Text (Markdown)").PressAsync("Control+s");

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("saved=1$"));
        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
        await Page.GotoAsync(site.At("/blog/written-in-the-browser"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Written in the browser", Level = 1 })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Unsaved_text_is_offered_back_after_a_reload()
    {
        await Page.GotoAsync(site.At("/admin/edit?path=posts%2Fhello-world.md"));
        var body = Page.GetByLabel("Text (Markdown)");
        await body.PressAsync("Control+End");
        await body.PressSequentiallyAsync(" An unsaved sentence.");
        await Page.WaitForFunctionAsync("() => (localStorage.getItem('weesite:unsaved:posts/hello-world.md') ?? '').includes('An unsaved sentence.')");

        await Page.ReloadAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();

        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("An unsaved sentence\\.$"));
    }

    [Test]
    [Arguments("/admin")]
    [Arguments("/admin/posts")]
    [Arguments("/admin/new/post")]
    [Arguments("/admin/edit?path=posts%2Fnotes-on-static-rendering.md")]
    [Arguments("/admin/editors")]
    [Arguments("/admin/media")]
    public async Task Editor_page_has_no_axe_violations(string path)
    {
        await Page.GotoAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }
}
