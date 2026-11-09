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
        await Page.OpenAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");

        await body.FillAsync("plain word");
        await body.EvaluateAsync("t => t.setSelectionRange(6, 10)");
        await Page.GetByRole(AriaRole.Toolbar, new() { Name = "Formatting" }).GetByRole(AriaRole.Button, new() { Name = "Bold" }).ClickAsync();

        await Expect(body).ToHaveValueAsync("plain **word**");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" }).Locator("strong")).ToHaveTextAsync("word");
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

        await body.FillAsync("");
        await Press("Table");
        await Expect(body).ToHaveValueAsync("| Column | Column |\n| --- | --- |\n| Text | Text |");
        await Assert.That(await body.EvaluateAsync<string>("t => t.value.slice(t.selectionStart, t.selectionEnd)")).IsEqualTo("Column");
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

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("saved=1$"));
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
        await Page.WaitForFunctionAsync("() => (localStorage.getItem('weesite:unsaved:posts/hello-world.md') ?? '').includes('An unsaved sentence.')");

        await Page.ReopenAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();

        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("An unsaved sentence\\.$"));
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

        await dialog.GetByLabel("File").SetInputFilesAsync(new FilePayload { Name = "Reading List.txt", MimeType = "text/plain", Buffer = "Books to read."u8.ToArray() });
        await Expect(dialog.Locator(".chosen")).ToHaveTextAsync("Reading List.txt");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Insert", Exact = true }).ClickAsync();

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(@"^\[reading-list\.txt\]\(/media/[0-9a-f]{12}-reading-list\.txt\)\n$"));
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
        await Page.GetByRole(AriaRole.Button, new() { Name = "Remove web" }).ClickAsync();
        await Expect(tags).ToHaveValueAsync("notes");
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
        await Expect(Page.GetByText("Only editors see it. Pick Published")).ToBeVisibleAsync();
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

        await Expect(Page.GetByText("From Other Site, exported 2026-11-10T09:00:00Z.")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Overwrite Hello, world")).Not.ToBeCheckedAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Import", Exact = true }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("Imported: 1 added, 0 overwritten, 1 kept as they were.");
        await Page.OpenAsync(site.At("/blog/from-a-backup"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "From a backup", Level = 1 })).ToBeVisibleAsync();
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
    public async Task Editor_page_has_no_axe_violations(string path)
    {
        await Page.OpenAsync(site.At(path));

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }
}
