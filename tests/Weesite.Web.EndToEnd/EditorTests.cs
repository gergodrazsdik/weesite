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
    public async Task The_media_picker_inserts_a_stored_image_with_its_description()
    {
        await Page.GotoAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");
        await body.FillAsync("Before.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        var sample = dialog.GetByRole(AriaRole.Button, new() { Name = "b368a21f9e93-sample.png" });

        await sample.ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Describe the image first (alt text).");
        await dialog.GetByLabel("Description of the image (alt text)").FillAsync("A blue square");
        await sample.ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(body).ToHaveValueAsync("Before.\n![A blue square](/media/b368a21f9e93-sample.png)\n");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" }).GetByRole(AriaRole.Img, new() { Name = "A blue square" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_media_picker_uploads_and_inserts_a_file()
    {
        await Page.GotoAsync(site.At("/admin/new/post"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });

        await dialog.GetByLabel("File").SetInputFilesAsync(new FilePayload { Name = "Reading List.txt", MimeType = "text/plain", Buffer = "Books to read."u8.ToArray() });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Upload and insert" }).ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(@"^\[reading-list\.txt\]\(/media/[0-9a-f]{12}-reading-list\.txt\)\n$"));
    }

    [Test]
    public async Task The_media_picker_has_no_axe_violations()
    {
        await Page.GotoAsync(site.At("/admin/new/post"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "b368a21f9e93-sample.png" })).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens", "media-picker-1280-light.png") });

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task The_menu_editor_moves_items_and_renumbers_their_positions()
    {
        await Page.GotoAsync(site.At("/admin/menu"));
        var rows = Page.Locator(".menu-row");
        var first = rows.Nth(0);

        await Expect(first.GetByLabel("Position")).ToBeHiddenAsync();
        await Expect(first.GetByRole(AriaRole.Button, new() { Name = "Move up" })).ToBeDisabledAsync();
        await first.GetByRole(AriaRole.Button, new() { Name = "Move down" }).ClickAsync();

        await Expect(rows.Nth(1).GetByLabel("Label")).ToHaveValueAsync("Blog");
        await Expect(rows.Nth(0).GetByLabel("Label")).ToHaveValueAsync("About");
        await Expect(rows.Nth(1).GetByLabel("Position", new() { Exact = true })).ToHaveValueAsync("2");
        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Blog: position 2 of 5");
    }

    [Test]
    [Arguments("/admin")]
    [Arguments("/admin/posts")]
    [Arguments("/admin/new/post")]
    [Arguments("/admin/edit?path=posts%2Fnotes-on-static-rendering.md")]
    [Arguments("/admin/editors")]
    [Arguments("/admin/media")]
    [Arguments("/admin/profile")]
    [Arguments("/admin/settings")]
    [Arguments("/admin/menu")]
    public async Task Editor_page_has_no_axe_violations(string path)
    {
        await Page.GotoAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }
}
