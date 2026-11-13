using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The editor in a real browser, signed in as an admin: ws-md-editor and accessibility. These tests
/// publish posts, import a backup and change the settings, so they get a site of their own: the
/// other tests and the screenshots expect the seed as it is (F-0071).
/// </summary>
[ClassDataSource<SiteServer>(Shared = SharedType.Keyed, Key = "editor")]
internal sealed class EditorTests(SiteServer site) : PageTest
{
    [Before(Test)]
    public async Task SignInAsAdmin() =>
        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { ["X-Test-Role"] = "Admin" });

    [Test]
    public async Task Toolbar_formats_the_selection_and_the_live_preview_renders_it()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");

        await body.FillAsync("plain word");
        await body.EvaluateAsync("t => t.setSelectionRange(6, 10)");
        await Page.GetByRole(AriaRole.Toolbar, new() { Name = "Formatting" }).GetByRole(AriaRole.Button, new() { Name = "Bold" }).ClickAsync();

        await Expect(body).ToHaveValueAsync("plain **word**");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" }).Locator("strong")).ToHaveTextAsync("word");

        // Pressed again, Bold takes its marks off (F-0091 L3).
        var toolbar = Page.GetByRole(AriaRole.Toolbar, new() { Name = "Formatting" });
        await toolbar.GetByRole(AriaRole.Button, new() { Name = "Bold" }).ClickAsync();
        await Expect(body).ToHaveValueAsync("plain word");
        await body.EvaluateAsync("t => t.setSelectionRange(0, 5)");
        await toolbar.GetByRole(AriaRole.Button, new() { Name = "Italic" }).ClickAsync();
        await toolbar.GetByRole(AriaRole.Button, new() { Name = "Italic" }).ClickAsync();
        await Expect(body).ToHaveValueAsync("plain word");

        // A link around chosen text selects the address, the part left to type.
        await body.EvaluateAsync("t => t.setSelectionRange(6, 10)");
        await toolbar.GetByRole(AriaRole.Button, new() { Name = "Link" }).ClickAsync();
        await Expect(body).ToHaveValueAsync("plain [word](https://)");
        await Assert.That(await body.EvaluateAsync<string>("t => t.value.slice(t.selectionStart, t.selectionEnd)")).IsEqualTo("https://");
    }

    [Test]
    public async Task The_toolbar_is_one_tab_stop_with_arrow_keys()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var toolbar = Page.GetByRole(AriaRole.Toolbar, new() { Name = "Formatting" });

        // F-0091 L27: one button takes Tab; the arrows, Home and End move along.
        await Assert.That(await toolbar.EvaluateAsync<int>("t => [...t.querySelectorAll('button')].filter(b => b.tabIndex === 0).length")).IsEqualTo(1);
        await toolbar.GetByRole(AriaRole.Button, new() { Name = "Bold" }).FocusAsync();
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(toolbar.GetByRole(AriaRole.Button, new() { Name = "Italic" })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("End");
        await Expect(toolbar.GetByRole(AriaRole.Button, new() { Name = "Table" })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(toolbar.GetByRole(AriaRole.Button, new() { Name = "Bold" })).ToBeFocusedAsync();
    }

    [Test]
    public async Task Toolbar_adds_numbered_and_task_lists_rules_and_tables()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");
        var toolbar = Page.GetByRole(AriaRole.Toolbar, new() { Name = "Formatting" });
        Task Press(string name) => toolbar.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();

        await body.FillAsync("first\nsecond");
        await body.EvaluateAsync("t => t.setSelectionRange(0, t.value.length)");
        await Press("Numbered list");
        await Expect(body).ToHaveValueAsync("1. first\n2. second");

        await body.FillAsync("buy milk");
        await body.EvaluateAsync("t => t.setSelectionRange(0, t.value.length)");
        await Press("Task list");
        await Expect(body).ToHaveValueAsync("- [ ] buy milk");

        await body.FillAsync("Above");
        await body.EvaluateAsync("t => t.setSelectionRange(t.value.length, t.value.length)");
        await Press("Horizontal rule");
        await Expect(body).ToHaveValueAsync("Above\n\n---");

        // The table's size is asked first (F-0100).
        await body.FillAsync("");
        await Press("Table");
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert a table" });
        await Expect(dialog.GetByLabel("Columns")).ToBeFocusedAsync();
        await dialog.GetByLabel("Columns").FillAsync("2");
        await dialog.GetByLabel("Rows").FillAsync("1");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Insert" }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(body).ToHaveValueAsync("| Column 1 | Column 2 |\n| --- | --- |\n|   |   |");
        await Assert.That(await body.EvaluateAsync<string>("t => t.value.slice(t.selectionStart, t.selectionEnd)")).IsEqualTo("Column 1");
    }

    [Test]
    public async Task The_code_block_dialog_finds_a_language_and_unknown_ones_are_warned_about()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");
        var codeBlock = Page.GetByRole(AriaRole.Toolbar, new() { Name = "Formatting" }).GetByRole(AriaRole.Button, new() { Name = "Code block", Exact = true });
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert a code block" });
        var search = dialog.GetByRole(AriaRole.Combobox, new() { Name = "Language" });

        // The selection goes into a fence for the language found by searching.
        await body.FillAsync("x = 1");
        await body.EvaluateAsync("t => t.setSelectionRange(0, t.value.length)");
        await codeBlock.ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(search).ToBeFocusedAsync();
        var axe = await Page.RunAxe();
        await search.FillAsync("pyth");
        await Expect(dialog.GetByRole(AriaRole.Option, new() { Selected = true })).ToContainTextAsync("Python");
        await search.PressAsync("Enter");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(body).ToHaveValueAsync("```python\nx = 1\n```");

        // An exact fence name comes first; without a selection the placeholder is selected to type over.
        await body.EvaluateAsync("t => t.setSelectionRange(t.value.length, t.value.length)");
        await codeBlock.ClickAsync();
        await search.FillAsync("cs");
        await search.PressAsync("Enter");
        await Expect(body).ToHaveValueAsync("```python\nx = 1\n```\n\n```csharp\ncode\n```");
        await Assert.That(await body.EvaluateAsync<string>("t => t.value.slice(t.selectionStart, t.selectionEnd)")).IsEqualTo("code");

        // Nothing found is said in the list; one Escape closes it, even after typing, and changes nothing (F-0091 L4).
        await codeBlock.ClickAsync();
        await search.FillAsync("zzzz");
        await Expect(dialog.Locator(".code-languages .none")).ToContainTextAsync("No language matches.");
        await search.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(body).ToHaveValueAsync("```python\nx = 1\n```\n\n```csharp\ncode\n```");
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();

        // A fence naming a language without a grammar is warned about after saving.
        await Page.GetByLabel("Title").FillAsync("Fences");
        await Page.GetByLabel("Slug").FillAsync("fences");
        await body.FillAsync("```python\nx = 1\n```\n\n```cobol\nIF X\n```");
        await body.PressAsync("Control+s");

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("saved=1&created=1$"));
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "No highlighting for cobol" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "python" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_new_posts_slug_follows_its_title_until_edited_and_control_s_saves_from_any_field()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var title = Page.GetByLabel("Title");
        var slug = Page.GetByLabel("Slug");

        await title.FillAsync("Első bejegyzés: hello, world!");
        await Expect(slug).ToHaveValueAsync("elso-bejegyzes-hello-world");
        await slug.FillAsync("from-the-title");
        await title.FillAsync("Another title");
        await Expect(slug).ToHaveValueAsync("from-the-title");

        // F-0086: Save is in reach from the title too, and the bar stays at the top.
        await Expect(Page.Locator(".editor-top")).ToHaveCSSAsync("position", "sticky");
        await title.PressAsync("Control+s");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("path=posts%2Ffrom-the-title\\.md&saved=1&created=1$"));
    }

    [Test]
    public async Task Signing_out_clears_the_unsaved_text_kept_in_the_browser()
    {
        await Page.OpenAsync(site.At("/admin/posts"));
        await Page.EvaluateAsync("() => { localStorage.setItem('weesite:unsaved:editor-94f6dcc0f4cf:new-post', '{}'); localStorage.setItem('weesite:unsaved:posts/old-key.md', '{}'); localStorage.setItem('something-else', 'kept'); }");

        // F-0089: the next person to sign in on this browser doesn't find it.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Page.WaitForURLAsync(site.At("/"));

        await Assert.That(await Page.EvaluateAsync<int>("() => Object.keys(localStorage).filter(k => k.startsWith('weesite:unsaved:')).length")).IsEqualTo(0);
        await Assert.That(await Page.EvaluateAsync<string?>("() => localStorage.getItem('something-else')")).IsEqualTo("kept");
    }

    [Test]
    public async Task Control_s_saves_and_publishes()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").FillAsync("Written in the browser");
        await Page.GetByLabel("Slug").FillAsync("written-in-the-browser");
        await Page.GetByLabel("Published").CheckAsync();
        await Page.GetByLabel("Text (Markdown)").FillAsync("Saved with the keyboard.");

        await Page.GetByLabel("Text (Markdown)").PressAsync("Control+s");

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("saved=1&created=1$"));
        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Saved.");
        await Page.OpenAsync(site.At("/blog/written-in-the-browser"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Written in the browser", Level = 1 })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Unsaved_text_is_offered_back_after_a_reload()
    {
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fhello-world.md"));
        var body = Page.GetByLabel("Text (Markdown)");
        await body.PressAsync("Control+End");
        await body.PressSequentiallyAsync(" An unsaved sentence.");
        await Page.WaitForFunctionAsync("() => (localStorage.getItem('weesite:unsaved:editor-94f6dcc0f4cf:posts/hello-world.md') ?? '').includes('An unsaved sentence.')");

        await Page.ReopenAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();

        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("An unsaved sentence\\.$"));
    }

    [Test]
    public async Task The_title_summary_and_tags_are_kept_too_and_cleared_text_isnt_offered()
    {
        const string Key = "weesite:unsaved:editor-94f6dcc0f4cf:posts/notes-on-static-rendering.md";
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fnotes-on-static-rendering.md"));

        // F-0091 L20: the fields come back with the text.
        await Page.GetByLabel("Title").FillAsync("A title not saved");
        await Page.GetByLabel("Add a tag").FillAsync("unsaved");
        await Page.GetByLabel("Add a tag").PressAsync("Enter");
        await Page.WaitForFunctionAsync($"() => (localStorage.getItem('{Key}') ?? '').includes('unsaved')");
        await Page.ReopenAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();
        await Expect(Page.GetByLabel("Title")).ToHaveValueAsync("A title not saved");
        await Expect(Page.Locator("input[name='Form.Tags']")).ToHaveValueAsync("web, performance, unsaved");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Remove unsaved" })).ToBeVisibleAsync();

        // F-0091 L21: back to what's stored, or the text only cleared, nothing is kept or offered.
        await Page.ReopenAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Discard" }).ClickAsync();
        await Page.GetByLabel("Text (Markdown)").FillAsync("Changed.");
        await Page.WaitForFunctionAsync($"() => (localStorage.getItem('{Key}') ?? '').includes('Changed.')");
        await Page.GetByLabel("Text (Markdown)").FillAsync("");
        await Page.WaitForFunctionAsync($"() => localStorage.getItem('{Key}') === null");
        await Page.ReopenAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restore" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_new_posts_unsaved_text_is_cleared_by_its_first_save()
    {
        // F-0050: the text of a new post was kept under "new post" after it was saved, and offered
        // on the next new post.
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").FillAsync("Saved once");
        await Page.GetByLabel("Slug").FillAsync("saved-once");
        var body = Page.GetByLabel("Text (Markdown)");
        await body.PressSequentiallyAsync("Typed before the first save.");
        await Page.WaitForFunctionAsync("() => (localStorage.getItem('weesite:unsaved:editor-94f6dcc0f4cf:new-post') ?? '').includes('first save')");

        await body.PressAsync("Control+s");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("saved=1&created=1$"));
        await Page.OpenAsync(site.At("/admin/new/post"));

        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync("");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restore" })).ToHaveCountAsync(0);
        await Assert.That(await Page.EvaluateAsync<string?>("() => localStorage.getItem('weesite:unsaved:editor-94f6dcc0f4cf:new-post')")).IsNull();
    }

    [Test]
    public async Task Unsaved_text_offered_back_shows_its_time_in_utc()
    {
        // F-0050: the time was in the browser's own format; the editor shows the others as
        // yyyy-MM-dd HH:mm UTC.
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.EvaluateAsync("() => localStorage.setItem('weesite:unsaved:editor-94f6dcc0f4cf:new-post', JSON.stringify({ text: 'Kept.', etag: '', at: '2026-11-10T16:33:13.000Z' }))");

        await Page.ReopenAsync();

        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Unsaved changes" })).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^Unsaved changes from 2026-11-10 16:33 UTC\\."));
    }

    [Test]
    public async Task The_social_card_image_is_chosen_in_a_dialog()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));

        // F-0098: a dialog of images (no SVG), filling the image and its description.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Choose an image…" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Choose the image" });
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "0e3d1bf78702-diagram.svg" })).ToHaveCountAsync(0);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "b368a21f9e93-sample.png" }).ClickAsync();
        await dialog.GetByLabel("Description of the image (alt text)").FillAsync("A blue square");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Use", Exact = true }).ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(Page.GetByLabel("Image", new() { Exact = true })).ToHaveValueAsync("/media/b368a21f9e93-sample.png");
        await Expect(Page.GetByLabel("Image description (alt text)")).ToHaveValueAsync("A blue square");
        await Expect(Page.Locator("img.og-card")).ToHaveAttributeAsync("src", "/media/b368a21f9e93-sample.png");

        // The body's own picker is still the one in the text's toolbar.
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" })).ToHaveCountAsync(1);
    }

    [Test]
    public async Task The_media_picker_inserts_a_stored_image_with_its_description()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");
        await body.FillAsync("Before.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        var sample = dialog.GetByRole(AriaRole.Button, new() { Name = "b368a21f9e93-sample.png" });

        var insert = dialog.GetByRole(AriaRole.Button, new() { Name = "Insert", Exact = true });
        await insert.ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Choose a file to upload, or a stored file.");
        await sample.ClickAsync();
        await Expect(sample).ToHaveAttributeAsync("aria-pressed", "true");
        await insert.ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Describe the image first (alt text).");
        await dialog.GetByLabel("Description of the image (alt text)").FillAsync("A blue square");
        await insert.ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(body).ToHaveValueAsync("Before.\n![A blue square](/media/b368a21f9e93-sample.png)\n");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" }).GetByRole(AriaRole.Img, new() { Name = "A blue square" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_media_picker_uploads_and_inserts_a_file()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });

        await dialog.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "Reading List.txt", MimeType = "text/plain", Buffer = "Books to read."u8.ToArray() });
        await Expect(dialog.Locator(".chosen")).ToHaveTextAsync("Reading List.txt");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Insert", Exact = true }).ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(@"^\[reading-list\.txt\]\(/media/[0-9a-f]{12}-reading-list\.txt\)\n$"));
    }

    [Test]
    public async Task A_pasted_image_opens_the_media_picker_with_it_chosen()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");
        await body.FillAsync("Before.");

        // What a browser sends for a pasted screenshot: a paste event carrying a file.
        await body.EvaluateAsync("""
            t => {
                const data = new DataTransfer();
                data.items.add(new File([new Uint8Array([137, 80, 78, 71])], "image.png", { type: "image/png" }));
                t.dispatchEvent(new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true }));
            }
            """);

        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator(".chosen")).ToHaveTextAsync("image.png");
        await Expect(body).ToHaveValueAsync("Before.");

        // Dropping a file on the text does the same (it once did nothing: F-0045).
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await Page.Locator(".md-panes").EvaluateAsync("""
            panes => {
                const data = new DataTransfer();
                data.items.add(new File(["notes"], "dropped.txt", { type: "text/plain" }));
                panes.dispatchEvent(new DragEvent("drop", { dataTransfer: data, bubbles: true, cancelable: true }));
            }
            """);
        await Expect(dialog.Locator(".chosen")).ToHaveTextAsync("dropped.txt");
    }

    [Test]
    public async Task The_media_picker_has_no_axe_violations()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "b368a21f9e93-sample.png" })).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens", "media-picker-1280-light.png") });

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task The_menu_editor_moves_items_and_renumbers_their_positions()
    {
        await Page.OpenAsync(site.At("/admin/menu"));
        var rows = Page.Locator(".menu-row");
        var first = rows.Nth(0);

        await Expect(first.GetByLabel("Position")).ToBeHiddenAsync();
        await Expect(first.GetByRole(AriaRole.Button, new() { Name = "Move up" })).ToBeDisabledAsync();
        await first.GetByRole(AriaRole.Button, new() { Name = "Move down" }).ClickAsync();

        await Expect(rows.Nth(1).GetByLabel("Label")).ToHaveValueAsync("Blog");
        await Expect(rows.Nth(0).GetByLabel("Label")).ToHaveValueAsync("About");
        await Expect(rows.Nth(1).GetByLabel("Position", new() { Exact = true })).ToHaveValueAsync("2");
        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Blog: position 2 of 5");
        await Expect(Page.Locator(".menu-preview li").First).ToHaveTextAsync("About");
        await Expect(rows.Nth(0).GetByLabel("Address")).ToBeDisabledAsync();
        await rows.Nth(0).GetByLabel("Goes to").SelectOptionAsync("");
        await Expect(rows.Nth(0).GetByLabel("Address")).ToBeEnabledAsync();
    }

    [Test]
    public async Task The_menu_editor_reorders_rows_by_dragging_their_number()
    {
        await Page.OpenAsync(site.At("/admin/menu"));
        var rows = Page.Locator(".menu-row");

        // Drag Blog's number below About's row, with the mouse as a person would.
        await rows.Nth(0).Locator(".num").DragToAsync(rows.Nth(1), new() { TargetPosition = new() { X = 20, Y = 40 } });

        await Expect(rows.Nth(0).GetByLabel("Label")).ToHaveValueAsync("About");
        await Expect(rows.Nth(1).GetByLabel("Label")).ToHaveValueAsync("Blog");
        await Expect(rows.Nth(1).GetByLabel("Position", new() { Exact = true })).ToHaveValueAsync("2");
        await Expect(rows.Nth(1).Locator(".num")).ToHaveTextAsync("2");
        await Expect(Page.Locator(".menu-preview li").First).ToHaveTextAsync("About");
        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Blog: position 2 of 5");
    }

    [Test]
    public async Task The_theme_editor_checks_contrast_and_previews_as_colours_change()
    {
        await Page.OpenAsync(site.At("/admin/theme"));
        var light = Page.GetByRole(AriaRole.Group, new() { Name = "Light" });
        var textOnBackground = Page.Locator("tr[data-mode=light][data-fg=text][data-bg=bg]");

        await Expect(textOnBackground.Locator("td").Nth(3)).ToHaveTextAsync("Passes");
        await light.GetByLabel("Text", new() { Exact = true }).FillAsync("#999999");

        await Expect(textOnBackground.Locator("td").Nth(3)).ToHaveTextAsync("Too low");
        await Expect(Page.Locator("[data-summary]")).ToHaveTextAsync("3 of 18 checks fail");
        await Expect(textOnBackground.Locator("td").Nth(2)).ToContainTextAsync("2.8 : 1");
        await Expect(light.GetByLabel("Text (Light): colour picker", new() { Exact = true })).ToHaveValueAsync("#999999");
        var sampleText = await Page.Locator(".theme-sample[data-mode=light]").EvaluateAsync<string>("e => getComputedStyle(e).color");
        await Assert.That(sampleText).IsEqualTo("rgb(153, 153, 153)");
    }

    [Test]
    public async Task The_editor_switches_layouts_counts_the_summary_and_edits_tags_as_labels()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var preview = Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" });
        var layouts = Page.GetByRole(AriaRole.Group, new() { Name = "Layout" });
        var tags = Page.Locator("input[name='Form.Tags']");

        await layouts.GetByRole(AriaRole.Button, new() { Name = "Write" }).ClickAsync();
        await Expect(preview).ToBeHiddenAsync();
        await layouts.GetByRole(AriaRole.Button, new() { Name = "Split" }).ClickAsync();
        await Expect(preview).ToBeVisibleAsync();

        await Page.GetByLabel("Summary").FillAsync("Twelve chars");
        await Expect(Page.Locator("[data-count-for=summary]")).ToHaveTextAsync("12 / 160");

        var entry = Page.GetByLabel("Add a tag");
        await entry.FillAsync("Web");
        await entry.PressAsync("Enter");
        await entry.FillAsync("#notes");
        await entry.PressAsync(",");
        await Expect(tags).ToHaveValueAsync("web, notes");
        await Expect(Page.Locator(".chips .tag.teal")).ToHaveTextAsync("#web×");

        // The entry keeps the focus after each tag, so the next one is typed straight on (F-0090).
        await entry.PressSequentiallyAsync("one");
        await entry.PressAsync("Enter");
        await Page.Keyboard.TypeAsync("two");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(tags).ToHaveValueAsync("web, notes, one, two");
        await Expect(entry).ToBeFocusedAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Remove web" }).ClickAsync();
        await Expect(tags).ToHaveValueAsync("notes, one, two");
    }

    [Test]
    public async Task The_social_card_previews_only_stored_media_files()
    {
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fnotes-on-static-rendering.md"));
        var card = Page.Locator("img.og-card");
        var image = Page.GetByLabel("Image", new() { Exact = true });

        await image.FillAsync("/media/b368a21f9e93-sample.png");
        await image.DispatchEventAsync("change");
        await Expect(card).ToHaveAttributeAsync("src", "/media/b368a21f9e93-sample.png");

        foreach (var other in new[] { "/media/../admin", "javascript:alert(1)", "https://example.com/a.png" })
        {
            await image.FillAsync(other);
            await image.DispatchEventAsync("change");
            await Expect(card).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("^/og/[0-9a-f]{16}\\.png$"));
        }
    }

    [Test]
    public async Task The_status_help_follows_the_chosen_status()
    {
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fnotes-on-static-rendering.md"));
        var status = Page.GetByRole(AriaRole.Radiogroup, new() { Name = "Status" });

        await Expect(Page.GetByText("Live at /blog/notes-on-static-rendering.")).ToBeVisibleAsync();
        await status.GetByRole(AriaRole.Radio, new() { Name = "Scheduled" }).CheckAsync();
        await Expect(Page.GetByText("Goes public at the time below")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Live at /blog/notes-on-static-rendering.")).ToBeHiddenAsync();
        await status.GetByRole(AriaRole.Radio, new() { Name = "Draft" }).CheckAsync();

        // A draft has no help line (F-0099): none of the others shows.
        await Expect(Page.GetByText("Goes public at the time below")).ToBeHiddenAsync();
        await Expect(Page.Locator(".status-help:visible")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task The_tags_page_colours_the_select_as_a_colour_is_chosen()
    {
        await Page.OpenAsync(site.At("/admin/tags"));
        var select = Page.GetByLabel("Colour of #web");

        await select.SelectOptionAsync("pink");

        await Expect(select).ToHaveClassAsync("colour-select pink");
    }

    [Test]
    public async Task The_backup_streams_from_the_real_server_as_a_valid_zip()
    {
        // Kestrel refuses synchronous writes, which the in-memory test server allows: this
        // checks the zip is written asynchronously all the way to its last byte.
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, site.At("/admin/backup/file"));
        request.Headers.Add("X-Test-Role", "Admin");

        using var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));

        await Assert.That(response.IsSuccessStatusCode).IsTrue();
        await Assert.That(zip.GetEntry("content/posts/hello-world.md")).IsNotNull();
        await Assert.That(zip.Entries.Count(e => e.FullName.StartsWith("media/", StringComparison.Ordinal))).IsGreaterThan(10);
    }

    [Test]
    public async Task A_backup_imports_through_the_review_without_scripts()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[]
            {
                ("weesite-backup.json", """{"format":1,"site":"Other Site","exportedAt":"2026-11-10T09:00:00Z"}"""),
                ("content/posts/from-a-backup.md", "---\ntitle: \"From a backup\"\nslug: from-a-backup\ndate: 2026-11-01\n---\n\nImported text.\n"),
                ("content/posts/hello-world.md", "---\ntitle: \"Hello, world\"\nslug: hello-world\ndate: 2026-01-15\n---\n\nA different text.\n"),
            })
            {
                await using var stream = await zip.CreateEntry(name).OpenAsync();
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
            }
        }

        await Page.OpenAsync(site.At("/admin/backup"));
        await Page.GetByLabel("Backup zip").SetInputFilesAsync(new FilePayload { Name = "backup.zip", MimeType = "application/zip", Buffer = buffer.ToArray() });
        await Page.GetByRole(AriaRole.Button, new() { Name = "Upload and review" }).ClickAsync();

        await Expect(Page.GetByText("From Other Site, exported 2026-11-10 09:00 UTC.")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Overwrite Hello, world")).Not.ToBeCheckedAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Import", Exact = true }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Imported: 1 added, 0 overwritten, 1 kept as they were.");
        await Page.OpenAsync(site.At("/blog/from-a-backup"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "From a backup", Level = 1 })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Leaving_with_unsaved_changes_asks_first_and_saving_doesnt()
    {
        // T-0109: the browser's "Leave site?" question, only when something changed.
        var asked = new List<string>();
        Page.Dialog += (_, dialog) =>
        {
            asked.Add(dialog.Type);
            _ = dialog.AcceptAsync();
        };

        // Unchanged: leaves without asking.
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Posts" }).First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/posts$"));
        await Assert.That(asked).IsEmpty();

        // Changed: asks, and leaves once accepted.
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").FillAsync("Never saved");
        await Page.GetByRole(AriaRole.Link, new() { Name = "Posts" }).First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/posts$"));
        await Assert.That(asked).IsEquivalentTo(["beforeunload"]);

        // Saved: no question.
        asked.Clear();
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").FillAsync("Saved before leaving");
        await Page.GetByLabel("Slug").FillAsync("saved-before-leaving");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("saved=1&created=1$"));
        await Page.GetByRole(AriaRole.Link, new() { Name = "Posts" }).First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/posts$"));
        await Assert.That(asked).IsEmpty();
    }

    [Test]
    public async Task Site_settings_save_twice_in_a_row()
    {
        await Page.OpenAsync(site.At("/admin/settings"));
        var tagline = Page.GetByLabel("Tagline");
        var before = await tagline.InputValueAsync();

        await tagline.FillAsync(before + " (1)");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saved" })).ToBeVisibleAsync();
        await Page.GetByLabel("Tagline").FillAsync(before);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Saved" })).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Tagline")).ToHaveValueAsync(before);
    }

    [Test]
    public async Task The_media_page_opens_its_upload_and_asks_before_losing_a_chosen_file()
    {
        var asked = new List<string>();
        Page.Dialog += (_, dialog) =>
        {
            asked.Add(dialog.Type);
            _ = dialog.DismissAsync();
        };
        await Page.OpenAsync(site.At("/admin/media"));
        var open = Page.GetByRole(AriaRole.Button, new() { Name = "Upload a file" });
        var area = Page.Locator("form#upload");

        // F-0094: closed until asked for; Cancel closes it and clears it.
        await Expect(area).ToBeHiddenAsync();
        await open.ClickAsync();
        await Expect(area).ToBeVisibleAsync();
        await Expect(open).ToHaveAttributeAsync("aria-expanded", "true");
        await area.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(area).ToBeHiddenAsync();
        await Expect(open).ToBeFocusedAsync();

        // F-0095: a chosen file not uploaded yet asks before a reload; dismissed, the page stays.
        await open.ClickAsync();
        await Page.Locator("#file").SetInputFilesAsync(Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png"));
        await Page.GetByRole(AriaRole.Link, new() { Name = "Refresh" }).ClickAsync();
        await Assert.That(asked).IsEquivalentTo(["beforeunload"]);
        await Expect(Page.Locator("form#upload .chosen")).ToHaveTextAsync("b368a21f9e93-sample.png");
    }

    [Test]
    public async Task Deleting_a_file_asks_in_a_dialog_and_keeps_the_lists_filter()
    {
        // A file of its own, so the other tests keep theirs.
        var text = Path.Combine(Path.GetTempPath(), $"dialog-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(text, "A file for the delete dialog.");
        await Page.OpenAsync(site.At("/admin/media"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Upload a file" }).ClickAsync();
        await Page.Locator("#file").SetInputFilesAsync(text);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Upload", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("uploaded="));
        var name = await Page.Locator(".media-grid .name").Filter(new() { HasText = "dialog-" }).First.EvaluateAsync<string>("a => a.firstChild.textContent");
        File.Delete(text);

        // F-0096: Cancel leaves the filtered list as it was; Delete comes back to it.
        await Page.OpenAsync(site.At("/admin/media?q=dialog-"));
        var delete = Page.GetByRole(AriaRole.Link, new() { Name = $"Delete {name}" });
        await delete.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = $"Delete {name}" });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByLabel($"Type the file's name, {name}, to confirm")).ToBeFocusedAsync();
        var axe = await Page.RunAxe();
        await dialog.GetByRole(AriaRole.Link, new() { Name = "Cancel" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/media\\?q=dialog-$"));
        await Expect(delete).ToBeFocusedAsync();

        await delete.ClickAsync();
        await dialog.GetByLabel($"Type the file's name, {name}, to confirm").FillAsync(name);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Delete permanently" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/media\\?q=dialog-&deleted="));
        await Expect(Page.GetByRole(AriaRole.Status)).ToContainTextAsync($"Deleted {name}.");
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task Removing_an_editor_asks_first()
    {
        await Page.OpenAsync(site.At("/admin/editors"));
        await Page.GetByLabel("Email address").FillAsync("asked@example.com");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
        var remove = Page.GetByRole(AriaRole.Button, new() { Name = "Remove asked@example.com" });
        await Expect(remove).ToBeVisibleAsync();

        // F-0091 L13: a dialog asks; Cancel keeps the editor, the dialog's Remove removes them.
        await remove.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Alertdialog);
        await Expect(dialog).ToContainTextAsync("Remove asked@example.com?");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" })).ToBeFocusedAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(remove).ToBeFocusedAsync();

        await remove.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("status=removed$"));
        await Expect(remove).ToHaveCountAsync(0);
    }

    [Test]
    public async Task The_media_picker_filters_the_stored_files_by_name()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "b368a21f9e93-sample.png" })).ToBeVisibleAsync();

        // F-0101: typing narrows the list; Enter doesn't insert.
        var filter = dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" });
        await filter.FillAsync("SAMPLE");
        await filter.PressAsync("Enter");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "b368a21f9e93-sample.png" })).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "0e3d1bf78702-diagram.svg" })).ToBeHiddenAsync();
        await Expect(dialog.Locator(".count")).ToContainTextAsync("1 of ");
    }

    [Test]
    public async Task A_chosen_image_is_previewed_before_upload()
    {
        // T-0120: on the media page and in the editor's media picker.
        var png = Path.Combine(SiteFactory.SeedPath, "media", "b368a21f9e93-sample.png");
        await Page.OpenAsync(site.At("/admin/media"));
        await Page.Locator("#file").SetInputFilesAsync(png);
        await Expect(Page.Locator("label.drop img.preview")).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("^data:image/png;base64,"));

        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        await dialog.Locator("#media-picker-file").SetInputFilesAsync(png);
        await Expect(dialog.Locator("img.preview")).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "b368a21f9e93-sample.png", Exact = true }).ClickAsync();
        await Expect(dialog.Locator("img.preview")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task The_home_page_selector_shows_only_when_the_home_page_has_a_page()
    {
        // F-0053.
        await Page.OpenAsync(site.At("/admin/settings"));
        var page = Page.GetByLabel("Page", new() { Exact = true });

        await Page.GetByRole(AriaRole.Radio, new() { Name = "The latest posts", Exact = true }).CheckAsync();
        await Expect(page).ToBeHiddenAsync();
        await Page.GetByRole(AriaRole.Radio, new() { Name = "A page, then the latest posts", Exact = true }).CheckAsync();
        await Expect(page).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Radio, new() { Name = "A page", Exact = true }).CheckAsync();
        await Expect(page).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_sidebar_keeps_the_editors_box_in_view()
    {
        // T-0107: at the bottom of a long page, sign out is still there without scrolling back.
        await Page.SetViewportSizeAsync(1280, 600);
        await Page.OpenAsync(site.At("/admin/settings"));
        var signOut = Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" });
        await Expect(signOut).ToBeInViewportAsync();

        await Page.EvaluateAsync("() => window.scrollTo(0, document.documentElement.scrollHeight)");

        await Expect(signOut).ToBeInViewportAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Dark" })).ToBeInViewportAsync();
    }

    [Test]
    public async Task The_media_picker_scrolls_only_its_body()
    {
        // F-0052: the dialog and its body both had a scrollbar.
        await Page.SetViewportSizeAsync(1280, 700);
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        await Expect(dialog).ToBeVisibleAsync();

        var overflow = await dialog.EvaluateAsync<int>("d => d.scrollHeight - d.clientHeight");
        var body = await dialog.Locator(".picker-body").EvaluateAsync<bool>("b => b.scrollHeight > b.clientHeight");

        await Assert.That(overflow).IsLessThanOrEqualTo(0);
        await Assert.That(body).IsTrue();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Insert", Exact = true })).ToBeInViewportAsync();
    }

    [Test]
    public async Task The_search_index_section_keeps_its_distance_from_the_save_button()
    {
        // F-0049: .section's margin: 0, later in admin.css, removed the section's top margin, so its
        // divider touched the settings' Save button.
        await Page.OpenAsync(site.At("/admin/settings"));
        var save = await Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).BoundingBoxAsync();
        var index = await Page.GetByRole(AriaRole.Region, new() { Name = "Search index" }).BoundingBoxAsync();

        await Assert.That(index!.Y - (save!.Y + save.Height)).IsGreaterThanOrEqualTo(24);
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
    [Arguments("/admin/theme")]
    [Arguments("/admin/tags")]
    [Arguments("/admin/backup")]
    [Arguments("/admin/preview/post/notes-for-a-future-post")]
    [Arguments("/admin/analytics")]
    [Arguments("/admin/export?path=posts%2Fnotes-on-static-rendering.md")]
    [Arguments("/admin/versions?path=posts%2Fnotes-on-static-rendering.md")]
    [Arguments("/admin/delete?path=posts%2Fnotes-on-static-rendering.md")]
    [Arguments("/admin/media/delete?name=0e3d1bf78702-diagram.svg")]
    [Arguments("/admin/media/archive?name=0e3d1bf78702-diagram.svg")]
    [Arguments("/admin/media?show=archived")]
    [Arguments("/admin/posts?status=archived")]
    public async Task Editor_page_has_no_axe_or_csp_violations(string path)
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
