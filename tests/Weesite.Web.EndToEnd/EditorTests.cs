using Deque.AxeCore.Playwright;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TUnit.Playwright;
using Weesite.Content;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The editor in a real browser, signed in as an admin: ws-md-editor and accessibility. These tests
/// publish posts, import a backup and change the settings, so they get a site of their own: the
/// other tests and the screenshots expect the seed as it is (F-0071).
/// </summary>
[ClassDataSource<SiteServer>(Shared = SharedType.Keyed, Key = "editor")]
internal sealed class EditorTests(SiteServer site) : PageTest
{
    // An upload answers once the content has reloaded (the media index and every page rendered
    // again), which on a busy 2-core CI runner can take longer than Playwright's 5 s default.
    private const float UploadTimeout = 30_000;

    // The bots the analytics tests added to the shared site, taken back after each test.
    private readonly List<string> _bots = [];

    [Before(Test)]
    public async Task SignInAsAdmin() =>
        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { ["X-Test-Role"] = "Admin" });

    [Test]
    public async Task A_save_made_elsewhere_shows_before_saving_and_says_whether_saving_would_conflict()
    {
        // T-0141: an app saves the post while it's open in the editor.
        var editor = site.Services.GetRequiredService<ContentEditor>();
        var draft = new ContentDraft { Title = "Open in two places", Slug = "open-in-two-places", Date = new DateOnly(2026, 11, 18), Status = ContentStatus.Draft, Body = "The first text." };
        var created = (ContentSaved)await editor.SaveAsync(null, draft, null, CancellationToken.None);
        await Page.OpenAsync(site.At($"/admin/edit?path={Uri.EscapeDataString(created.Path)}"));
        await editor.SaveAsync(created.Path, draft with { Body = "Changed by an app." }, created.ETag, CancellationToken.None);

        // The page asks every 30 s, and when it's focused again.
        await Page.EvaluateAsync("() => window.dispatchEvent(new Event('focus'))");
        var notice = Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Someone saved this since you opened it" });
        await Expect(notice.GetByRole(AriaRole.Link, new() { Name = "Load the saved version" })).ToBeVisibleAsync();

        await Page.GetByLabel("Text (Markdown)").FillAsync("My own edit.");
        await Expect(notice).ToContainTextAsync("you have unsaved changes: saving will show the conflict");
        await Expect(notice.GetByRole(AriaRole.Link, new() { Name = "Open the saved version (opens in a new tab)" })).ToBeVisibleAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task Choosing_a_groups_colour_shows_what_applying_would_change()
    {
        // T-0144: with the script, the rows follow the group's colour as it's chosen.
        await Page.OpenAsync(site.At("/admin/tags/suggest"));
        var colour = Page.GetByLabel("Colour of group 1");
        var suggested = await colour.InputValueAsync();
        var other = TagDetails.Colours.First(c => c != suggested);
        var group = Page.GetByRole(AriaRole.Region, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Group 1,") });
        var firstRow = group.GetByRole(AriaRole.Row).Nth(1);

        await colour.SelectOptionAsync(other);

        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Show these colours" })).ToBeHiddenAsync();
        await Expect(firstRow.GetByRole(AriaRole.Cell).First).ToHaveTextAsync(new System.Text.RegularExpressions.Regex($"(to|already) {other}$"));
        await Expect(group.GetByRole(AriaRole.Checkbox, new() { NameRegex = new System.Text.RegularExpressions.Regex($"^Apply {other} to #") }).First).ToBeAttachedAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task Analytics_lists_are_tabs_the_table_opens_in_a_dialog_and_the_report_refreshes_in_place()
    {
        // T-0145.
        await Page.Clock.InstallAsync();
        await Page.OpenAsync(site.At("/admin/analytics"));
        var tabs = Page.GetByRole(AriaRole.Tablist, new() { Name = "Reports" });
        var bots = tabs.GetByRole(AriaRole.Tab, new() { Name = "Bots and AI agents" });

        // Tabs (pages, referring sites, bots and AI agents, countries, reactions): the arrow keys
        // move between them, and one panel shows.
        await Expect(tabs.GetByRole(AriaRole.Tab)).ToHaveCountAsync(5);
        await tabs.GetByRole(AriaRole.Tab, new() { Name = "Top pages" }).FocusAsync();
        await Page.Keyboard.PressAsync("ArrowRight");
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(bots).ToBeFocusedAsync();
        await Expect(bots).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Page.GetByRole(AriaRole.Tabpanel)).ToHaveCountAsync(1);
        await Expect(Page.GetByRole(AriaRole.Tabpanel)).ToContainTextAsync("bot: Googlebot");

        // The per-day table in a dialog; Escape closes it and the focus goes back.
        var show = Page.GetByRole(AriaRole.Button, new() { Name = "Show as a table" });
        await show.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Page views by people, per day" });
        await Expect(dialog.GetByRole(AriaRole.Table)).ToBeVisibleAsync();

        // F-0182: every column fits (reactions made it six), with nothing to scroll sideways.
        await Assert.That(await dialog.Locator(".table-scroll").EvaluateAsync<bool>("r => r.scrollWidth <= r.clientWidth")).IsTrue();

        // F-0184: a close button beside the title, focused first; the one under the table stays.
        var closeButtons = dialog.GetByRole(AriaRole.Button, new() { Name = "Close" });
        await Expect(closeButtons).ToHaveCountAsync(2);
        await Expect(closeButtons.First).ToBeFocusedAsync();
        await Expect(closeButtons.First).ToBeInViewportAsync();
        await closeButtons.First.ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(show).ToBeFocusedAsync();
        await show.ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Table)).ToBeVisibleAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(show).ToBeFocusedAsync();

        // Thirty seconds later the report is fetched again: a new bot shows, in the same tab.
        await AddBotAsync("RefreshCheckBot");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Pause updates" })).ToBeVisibleAsync();
        await Page.SetViewportSizeAsync(1280, 500);
        await Page.EvaluateAsync("() => window.scrollTo(0, document.documentElement.scrollHeight)");
        var scrolled = await Page.EvaluateAsync<double>("() => window.scrollY");
        await RefreshOnceAsync();
        await Expect(Page.GetByRole(AriaRole.Tabpanel)).ToContainTextAsync("bot: RefreshCheckBot");
        await Expect(Page.GetByRole(AriaRole.Tab, new() { Name = "Bots and AI agents" })).ToHaveAttributeAsync("aria-selected", "true");
        await Assert.That(scrolled).IsGreaterThan(0);
        await Assert.That(await Page.EvaluateAsync<double>("() => window.scrollY")).IsEqualTo(scrolled);

        // Paused stays paused on another range, which loads the page again (F-0203), and asks for
        // no report until it's resumed; then the next one comes (F-0228).
        var requests = 0;
        await Page.RouteAsync("**/admin/analytics/report*", async route =>
        {
            Interlocked.Increment(ref requests);
            await route.ContinueAsync();
        });
        await Page.GetByRole(AriaRole.Button, new() { Name = "Pause updates" }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Last 7 days" }).ClickAsync();
        var resume = Page.GetByRole(AriaRole.Button, new() { Name = "Resume updates" });
        await Expect(resume).ToBeVisibleAsync();

        // The new page's own report first, or it's still on its way when the requests are counted (F-0243).
        await Expect(Page.GetByRole(AriaRole.Tabpanel)).ToBeVisibleAsync(new() { Timeout = 30_000 });
        var loaded = Volatile.Read(ref requests);
        await AddBotAsync("PausedCheckBot");
        await Page.Clock.RunForAsync(61_000);
        await Assert.That(loaded).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref requests)).IsEqualTo(loaded);
        await Expect(Page.GetByRole(AriaRole.Tabpanel)).Not.ToContainTextAsync("PausedCheckBot");
        await resume.ClickAsync();
        await RefreshOnceAsync();
        await Expect(Page.GetByRole(AriaRole.Tabpanel)).ToContainTextAsync("bot: PausedCheckBot");
        await Assert.That(Volatile.Read(ref requests)).IsEqualTo(loaded + 1);
    }

    [Test]
    public async Task Analytics_updates_keep_the_focus_say_when_they_fail_and_catch_up_when_the_page_is_shown_again()
    {
        // F-0223. The report swapped in for itself: marked, so a test can tell when it was.
        await Page.Clock.InstallAsync();
        await Page.OpenAsync(site.At("/admin/analytics"));
        var swapped = Page.Locator("section.analytics[data-old]");
        async Task MarkAsync() => await Page.EvaluateAsync("() => { document.querySelector('section.analytics').dataset.old = '1'; }");

        // The focus on an element without an id (the country data's link) stays on it.
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Countries" }).ClickAsync();
        var link = Page.GetByRole(AriaRole.Link, new() { Name = "DB-IP" });
        await link.FocusAsync();
        await MarkAsync();
        await RefreshOnceAsync();
        await Expect(swapped).ToHaveCountAsync(0);
        await Expect(link).ToBeFocusedAsync();

        // Updates that fail say so from the second in a row, the tries go on, and the next one that
        // comes clears it.
        var status = Page.Locator(".analytics-refresh [role=status]");
        await Page.RouteAsync("**/admin/analytics/report*", route => route.FulfillAsync(new() { Status = 302, Headers = new Dictionary<string, string> { ["Location"] = "/admin/sign-in" } }));
        await RefreshOnceAsync();
        await Expect(status).ToBeEmptyAsync();
        await RefreshOnceAsync();
        await Expect(status).ToHaveTextAsync("Updates aren't coming through. Still trying every 30 seconds; if you were signed out, reload the page.");
        await Page.UnrouteAsync("**/admin/analytics/report*");
        await MarkAsync();
        await RefreshOnceAsync();
        await Expect(swapped).ToHaveCountAsync(0);
        await Expect(status).ToBeEmptyAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();

        // Hidden, it doesn't update; shown again after missing one, it updates at once.
        await Page.EvaluateAsync("() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); }");
        await MarkAsync();
        await Page.Clock.RunForAsync(31_000);
        await Expect(Page.Locator("section.analytics[data-old]")).ToHaveCountAsync(1);
        await Page.EvaluateAsync("() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); }");
        await Expect(swapped).ToHaveCountAsync(0);
    }

    // One update of the analytics page on its clock, done: answered and handled. The script marks
    // an update (data-refreshing) as it starts, before it waits for anything, so once the clock has
    // moved it's either marked or done. A tick while the previous update was still on its way was
    // skipped, and its answer could take longer than an assertion's 5 s on a busy runner (F-0243).
    private async Task RefreshOnceAsync()
    {
        await Page.Clock.RunForAsync(31_000);
        await Expect(Page.Locator(".analytics-refresh[data-refreshing]")).ToHaveCountAsync(0, new() { Timeout = 30_000 });
    }

    // A bot's view of the home page, for the analytics tests; taken back once the test is done,
    // as the site is shared with other tests (F-0228).
    private async Task AddBotAsync(string name)
    {
        _bots.Add(name);
        await site.Services.GetRequiredService<Weesite.Web.Analytics.MemoryPageViewStore>().AddAsync(
            [new Weesite.Web.Analytics.PageView(DateTimeOffset.UtcNow, "/", null, Weesite.Web.Analytics.ClientKind.Bot, name, "bot-" + name)], CancellationToken.None);
    }

    [After(Test)]
    public void RemoveBots()
    {
        var names = _bots.ToHashSet(StringComparer.Ordinal);
        if (names.Count > 0)
        {
            site.Services.GetRequiredService<Weesite.Web.Analytics.MemoryPageViewStore>().RemoveWhere(v => v.Client is { } client && names.Contains(client));
        }
    }

    [Test]
    public async Task Split_view_panes_scroll_together()
    {
        // F-0180: side by side, each pane scrolls on its own and the other follows, lined up by the
        // blocks' source lines; images make the preview longer than the text.
        await Page.SetViewportSizeAsync(1600, 800);
        await Page.OpenAsync(site.At("/admin/new/post"));
        List<string> lines = [];
        for (var i = 1; i <= 40; i++)
        {
            lines.AddRange([$"## Section {i}", "", $"The text of section {i}.", ""]);
            if (i % 4 == 0)
            {
                lines.AddRange(["![A](/media/b368a21f9e93-sample.png)", ""]);
            }
        }

        var body = Page.GetByLabel("Text (Markdown)");
        await body.FillAsync(string.Join('\n', lines));
        var preview = Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" });
        await Expect(preview.GetByRole(AriaRole.Heading, new() { Name = "Section 40" })).ToBeAttachedAsync();
        await Expect(preview).ToHaveAttributeAsync("tabindex", "0");

        // Where a source line starts in the textarea: its lines here are short, so none wraps.
        await Assert.That(await body.EvaluateAsync<bool>("t => t.scrollHeight <= parseFloat(getComputedStyle(t).lineHeight) * (t.value.split('\\n').length + 1) + parseFloat(getComputedStyle(t).paddingTop) + parseFloat(getComputedStyle(t).paddingBottom)")).IsTrue();
        int LineOf(string text) => lines.IndexOf(text) + 1;
        const string LineTop = """
            ([t, line]) => {
                const style = getComputedStyle(t);
                return parseFloat(style.paddingTop) + (line - 1) * parseFloat(style.lineHeight);
            }
            """;
        const string HeadingAtTop = """
            ([p, name]) => {
                const h = [...p.querySelectorAll('h2')].find(e => e.textContent === name);
                return Math.abs(h.getBoundingClientRect().top - p.getBoundingClientRect().top - p.clientTop) <= 3;
            }
            """;

        // The text scrolled to a heading's line: the preview shows that heading at its top.
        await body.EvaluateAsync("(t, top) => t.scrollTop = top", await body.EvaluateAsync<double>($"t => ({LineTop})([t, {LineOf("## Section 25")}])"));
        await Page.WaitForFunctionAsync($"p => ({HeadingAtTop})([p, 'Section 25'])", await preview.ElementHandleAsync());

        // The preview scrolled to a heading: the text follows to that heading's line.
        await preview.EvaluateAsync("""
            p => {
                const h = [...p.querySelectorAll('h2')].find(e => e.textContent === 'Section 10');
                p.scrollTop += h.getBoundingClientRect().top - p.getBoundingClientRect().top - p.clientTop;
            }
            """);
        var expected = await body.EvaluateAsync<double>($"t => ({LineTop})([t, {LineOf("## Section 10")}])");
        await Page.WaitForFunctionAsync($"t => Math.abs(t.scrollTop - {expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}) <= 3", await body.ElementHandleAsync());

        // At the end of the text, the preview is at its end too.
        await body.EvaluateAsync("t => t.scrollTop = t.scrollHeight");
        await Page.WaitForFunctionAsync("p => p.scrollTop >= p.scrollHeight - p.clientHeight - 1", await preview.ElementHandleAsync());
    }

    [Test]
    public async Task The_wheel_over_a_split_pane_scrolls_the_page_once_the_pane_cannot()
    {
        // F-0183: a pane with nothing to scroll, or at its end, passes the wheel on to the page,
        // as any scrolling box does; the panes are the largest part of the page.
        await Page.SetViewportSizeAsync(2000, 1000);
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fplain-text-files-and-the-jars-at-pompeii.md"));
        var body = Page.GetByLabel("Text (Markdown)");
        var preview = Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" });
        await Expect(preview.GetByRole(AriaRole.Heading, new() { Name = "Simple containers last" })).ToBeAttachedAsync();
        await Assert.That(await body.EvaluateAsync<bool>("t => t.scrollHeight <= t.clientHeight")).IsTrue();

        // The text fits its pane: the wheel over it scrolls the page.
        await Page.EvaluateAsync("() => window.scrollTo(0, 0)");
        var text = (await body.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(text.X + (text.Width / 2), text.Y + 40);
        await Page.Mouse.WheelAsync(0, 200);
        await Page.WaitForFunctionAsync("() => window.scrollY > 0");

        // The preview at its end: the wheel over it scrolls the page further. Setting scrollTop can
        // stop a fraction short of the end, which the first wheel takes; each wheel is a gesture
        // of its own, as a person's would be.
        await Page.WaitForFunctionAsync("p => [...p.querySelectorAll('img')].every(i => i.complete)", await preview.ElementHandleAsync());
        await Page.EvaluateAsync("() => window.scrollTo(0, 0)");
        await preview.EvaluateAsync("p => p.scrollTop = p.scrollHeight");
        var rendered = (await preview.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(rendered.X + (rendered.Width / 2), rendered.Y + 40);
        var scrolled = false;
        for (var attempt = 0; attempt < 5 && !scrolled; attempt++)
        {
            await Page.Mouse.WheelAsync(0, 200);
            try
            {
                await Page.WaitForFunctionAsync("() => window.scrollY > 0", null, new() { Timeout = 2_000 });
                scrolled = true;
            }
            catch (TimeoutException)
            {
            }
        }

        await Assert.That(scrolled).IsTrue();
        await Assert.That(await preview.EvaluateAsync<bool>("p => p.scrollTop >= p.scrollHeight - p.clientHeight - 1")).IsTrue();
    }

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
    public async Task The_top_bar_counts_words_as_they_are_typed()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var stats = Page.Locator(".editor-top #body-stats");

        // F-0128: beside the saved time, before View; kept current as the text changes.
        // U-45: no reading time for an empty text.
        await Expect(stats).ToHaveTextAsync("no words yet");
        await Page.GetByLabel("Text (Markdown)").FillAsync(string.Join(' ', Enumerable.Repeat("word", 300)));
        await Expect(stats).ToHaveTextAsync("300 words · 2 min read");
        await Expect(Page.Locator(".md-foot")).Not.ToContainTextAsync("words");
        await Page.GetByLabel("Text (Markdown)").FillAsync("");
        await Expect(stats).ToHaveTextAsync("no words yet");

        // F-0181: counted as the site counts, so markup isn't words.
        await Page.GetByLabel("Text (Markdown)").FillAsync("## Setup\n\n- one\n- two\n\n| a | b |\n| - | - |\n| c | d |");
        await Expect(stats).ToHaveTextAsync("7 words · 1 min read");

        // A stored post shows its count as the server rendered it, too.
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fhello-world.md"));
        await Expect(stats).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^[1-9]\d* words · \d+ min read$"));

        // F-0224: when no preview brings the count (offline, refused), the page counts the same way.
        await Page.RouteAsync("**/admin/preview", route => route.AbortAsync());
        await Page.GetByLabel("Text (Markdown)").FillAsync("## Offline\n\n- one *two*\n\n[a link](https://example.org/)");
        await Expect(stats).ToHaveTextAsync("5 words · 1 min read");
    }

    [Test]
    public async Task Control_s_doesnt_save_behind_an_open_dialog()
    {
        // F-0224: the media picker is a modal dialog outside the form.
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").FillAsync("Behind a dialog");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        await Expect(dialog).ToBeVisibleAsync();

        await Page.Keyboard.PressAsync("Control+s");
        await Expect(dialog).ToBeVisibleAsync();
        await Assert.That(Page.Url).EndsWith("/admin/new/post");

        await Page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        await Page.Keyboard.PressAsync("Control+s");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/edit\\?path=posts%2F"));
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

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/edit\\?path=posts%2F[^&]+$"));
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
        await Expect(slug).ToHaveValueAsync("első-bejegyzés-hello-world");
        await Assert.That(await slug.EvaluateAsync<bool>("e => e.validity.valid")).IsTrue();
        await slug.FillAsync("from-the-title");
        await title.FillAsync("Another title");
        await Expect(slug).ToHaveValueAsync("from-the-title");

        // F-0086: Save is in reach from the title too, and the bar stays at the top.
        await Expect(Page.Locator(".editor-top")).ToHaveCSSAsync("position", "sticky");
        await title.PressAsync("Control+s");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("path=posts%2Ffrom-the-title\\.md$"));
    }

    [Test]
    public async Task Ctrl_s_saves_before_any_field_has_the_focus()
    {
        // F-0176: with nothing changed and nothing focused, the browser's "Save page" opened instead.
        var editor = site.Services.GetRequiredService<ContentEditor>();
        var draft = new ContentDraft { Title = "Saved by keyboard", Slug = "saved-by-keyboard", Date = new DateOnly(2026, 11, 18), Status = ContentStatus.Draft, Body = "Text." };
        var created = (ContentSaved)await editor.SaveAsync(null, draft, null, CancellationToken.None);
        await Page.OpenAsync(site.At($"/admin/edit?path={Uri.EscapeDataString(created.Path)}"));
        await Assert.That(await Page.EvaluateAsync<string>("() => document.activeElement?.tagName ?? ''")).IsEqualTo("BODY");

        await Page.Keyboard.PressAsync("Control+s");

        // Saved (the draft gets its author) rather than a browser dialog.
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^(Saved\\.|Nothing had changed)") })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Pasted_tags_with_commas_become_several_and_a_new_post_s_date_is_local()
    {
        // F-0119: "a, b" became one tag, a,-b; a new post's date was the UTC date.
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Add a tag").FillAsync("first, second one");
        await Page.GetByLabel("Add a tag").PressAsync("Enter");
        var local = await Page.EvaluateAsync<string>("() => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; }");

        await Expect(Page.Locator("input[name='Form.Tags']")).ToHaveValueAsync("first, second-one");
        await Expect(Page.GetByLabel("Date", new() { Exact = true })).ToHaveValueAsync(local);
    }

    [Test]
    public async Task A_media_card_s_copy_says_so_in_a_status_region()
    {
        // F-0117: the button's own text changed under its aria-label, which isn't announced.
        await Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await Page.OpenAsync(site.At("/admin/media"));

        await Page.GetByRole(AriaRole.Button, new() { Name = "Copy the Markdown for" }).First.ClickAsync();

        await Expect(Page.Locator("#copy-status")).ToHaveTextAsync("Copied.");
    }

    [Test]
    public async Task A_typed_confirmation_has_copy_for_the_text_to_type()
    {
        // F-0166: typing a media file's long name to confirm was tedious. Nothing is deleted here.
        await Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await Page.OpenAsync(site.At("/admin/delete?path=posts/hello-world.md"));

        await Page.GetByRole(AriaRole.Button, new() { Name = "Copy hello-world" }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Copied." })).ToBeVisibleAsync();
        await Assert.That(await Page.EvaluateAsync<string>("() => navigator.clipboard.readText()")).IsEqualTo("hello-world");
        var screens = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens");
        Directory.CreateDirectory(screens);
        await Page.Locator(".typed-confirm").ScreenshotAsync(new() { Path = Path.Combine(screens, "typed-confirm-1280.png") });
        await Page.SetViewportSizeAsync(390, 844);
        await Page.Locator(".typed-confirm").ScreenshotAsync(new() { Path = Path.Combine(screens, "typed-confirm-390.png") });
    }

    [Test]
    public async Task Clearing_a_list_filter_shows_the_whole_list_again()
    {
        // F-0167: the field's clear button emptied it, but the list stayed filtered until Enter.
        await Page.OpenAsync(site.At("/admin/posts?q=hello"));
        var filter = Page.GetByRole(AriaRole.Searchbox, new() { Name = "Filter Posts" });
        await filter.FillAsync("h");
        await filter.PressAsync("Backspace");

        // Emptied by typing, the page stays as it is; cleared, the whole list comes back.
        await Expect(filter).ToHaveValueAsync("");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/admin/posts\?q=hello$"));
        await filter.FillAsync("hello");
        await filter.PressAsync("Escape");

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(@"/admin/posts\?q=$"));
        await Expect(Page.GetByRole(AriaRole.Searchbox, new() { Name = "Filter Posts" })).ToHaveValueAsync("");
    }

    [Test]
    public async Task Signing_out_clears_the_unsaved_text_kept_in_the_browser()
    {
        await Page.OpenAsync(site.At("/admin/posts"));
        await Page.EvaluateAsync("() => { localStorage.setItem('weesite:unsaved:editor-94f6dcc0f4cf:new-post', '{}'); localStorage.setItem('weesite:unsaved:posts/old-key.md', '{}'); localStorage.setItem('something-else', 'kept'); }");

        // F-0089: the next person to sign in on this browser doesn't find it.
        // Checks the home page's address only, never its load: on a busy CI runner the home
        // page's images kept that past 30 s (F-0104). WaitForURLAsync isn't enough: when the
        // address already matches, it waits for the load state instead. The script clears the
        // keys before the form posts, so they're gone by then.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(site.At("/"));

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

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/edit\\?path=posts%2F[^&]+$"));
        await Expect(Page.Locator(".notice[role=status]")).ToHaveTextAsync("Saved.");
        // U-31: the focus is on the notice, not on the page's top.
        await Expect(Page.Locator(".notice[role=status]")).ToBeFocusedAsync();
        await Page.OpenAsync(site.At("/blog/written-in-the-browser"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Written in the browser", Level = 1 })).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_export_footer_box_applies_as_it_is_ticked()
    {
        // U-46: it needed a separate Update button; now the export changes in place.
        await Page.OpenAsync(site.At("/admin/export?path=posts%2Fnotes-on-static-rendering.md"));
        var box = Page.GetByLabel("End with “Originally published at” and the address");
        var source = Page.Locator("#export-html");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Update" })).ToBeHiddenAsync();
        await Expect(source).Not.ToHaveValueAsync(new System.Text.RegularExpressions.Regex("Originally published at"));

        await box.CheckAsync();
        await Expect(source).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("Originally published at"));
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Download .html" })).ToHaveAttributeAsync("href", new System.Text.RegularExpressions.Regex("footer=1"));
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("footer=1"));
        await Expect(box).ToBeFocusedAsync();

        await box.UncheckAsync();
        await Expect(source).Not.ToHaveValueAsync(new System.Text.RegularExpressions.Regex("Originally published at"));
    }

    [Test]
    public async Task The_top_bar_says_when_there_are_unsaved_changes_and_that_the_text_is_kept()
    {
        // U-41: the bar kept saying "saved …" while the text changed.
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fnotes-on-static-rendering.md"));
        var note = Page.Locator(".editor-top [data-unsaved-note]");
        var body = Page.GetByLabel("Text (Markdown)");
        var text = await body.InputValueAsync();
        await Expect(note).ToHaveAttributeAsync("role", "status");
        await Expect(note).ToHaveTextAsync("");

        await body.FillAsync(text + " More.");
        await Expect(note).ToHaveTextAsync("unsaved changes; the text is kept in this browser");

        // Back as it was: nothing unsaved.
        await body.FillAsync(text);
        await Expect(note).ToHaveTextAsync("");

        // A change the browser doesn't keep, the status, is still unsaved.
        await Page.GetByLabel("Archived").CheckAsync();
        await Expect(note).ToHaveTextAsync("unsaved changes");
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

        // U-45: Discard asks first, in the bar's place, so the toolbar under it stays where it was.
        // Measured once the bar says what it offers: its message comes a moment after it's shown.
        var toolbar = Page.GetByRole(AriaRole.Toolbar, new() { Name = "Formatting" });
        await Expect(Page.GetByText(new System.Text.RegularExpressions.Regex("^Unsaved changes from"))).ToBeVisibleAsync();
        var top = (await toolbar.BoundingBoxAsync())!.Y;
        await Page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Keep them" })).ToBeFocusedAsync();
        await Assert.That(Math.Abs((await toolbar.BoundingBoxAsync())!.Y - top)).IsLessThan(1);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Keep them" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restore" })).ToBeVisibleAsync();
        await Assert.That(await Page.EvaluateAsync<bool>($"() => localStorage.getItem('{Key}') !== null")).IsTrue();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Discard them" }).ClickAsync();
        await Expect(Page.GetByText("The kept changes were discarded.")).ToBeVisibleAsync();
        await Assert.That(Math.Abs((await toolbar.BoundingBoxAsync())!.Y - top)).IsLessThan(1);
        await Assert.That(await Page.EvaluateAsync<bool>($"() => localStorage.getItem('{Key}') === null")).IsTrue();
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
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/edit\\?path=posts%2F[^&]+$"));

        // The address changes as the saved page starts loading; its Markdown editor clears what was
        // kept once its scripts have run. Leaving before that, as a test can within milliseconds,
        // left the text behind (F-0185).
        await Page.WaitForFunctionAsync("() => localStorage.getItem('weesite:unsaved:editor-94f6dcc0f4cf:new-post') === null");
        await Page.OpenAsync(site.At("/admin/new/post"));

        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync("");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Restore" })).ToHaveCountAsync(0);
        await Assert.That(await Page.EvaluateAsync<string?>("() => localStorage.getItem('weesite:unsaved:editor-94f6dcc0f4cf:new-post')")).IsNull();
    }

    [Test]
    public async Task Text_typed_after_a_save_survives_a_reload_of_the_saved_address()
    {
        // F-0110: loading the address a save led to (then with saved=1) deleted what was kept, whatever its version.
        const string Key = "weesite:unsaved:editor-94f6dcc0f4cf:posts/kept-after-saving.md";
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").FillAsync("Kept after saving");
        await Page.GetByLabel("Slug").FillAsync("kept-after-saving");
        await Page.GetByLabel("Text (Markdown)").PressAsync("Control+s");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/edit\\?path=posts%2F[^&]+$"));
        await Page.GetByLabel("Text (Markdown)").PressSequentiallyAsync("Typed after the save.");
        await Page.WaitForFunctionAsync($"() => (localStorage.getItem('{Key}') ?? '').includes('after the save')");

        await Page.ReopenAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();
        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync("Typed after the save.");

        // Kept from an earlier version (saved since elsewhere): offered, and said so. On the
        // address a save leads to, such text is what was just saved, and isn't offered.
        // (Set on a fresh load: the restored text above is kept again a moment after its input.)
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fkept-after-saving.md"));
        await Page.EvaluateAsync($"() => localStorage.setItem('{Key}', JSON.stringify({{ text: 'From before.', etag: 'an-older-version', at: '2026-11-10T16:33:13.000Z' }}))");
        await Page.ReopenAsync();
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Unsaved changes" })).ToContainTextAsync("made to an earlier version");
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
    public async Task Restoring_text_made_to_an_earlier_version_meets_the_newer_save_as_a_conflict()
    {
        // F-0248: Restore kept the page's version, so a save replaced the newer one without a
        // conflict; and the offer was a live region born with its text, never announced.
        var editor = site.Services.GetRequiredService<ContentEditor>();
        var draft = new ContentDraft { Title = "Restored over", Slug = "restored-over", Date = new DateOnly(2026, 11, 23), Status = ContentStatus.Draft, Body = "The first text." };
        var created = (ContentSaved)await editor.SaveAsync(null, draft, null, CancellationToken.None);
        await editor.SaveAsync(created.Path, draft with { Body = "Saved by an app." }, created.ETag, CancellationToken.None);
        var kept = System.Text.Json.JsonSerializer.Serialize(new { text = "Typed against the first text.", etag = created.ETag, at = "2026-11-23T09:00:00.000Z" });
        await Page.OpenAsync(site.At($"/admin/edit?path={Uri.EscapeDataString(created.Path)}"));
        await Page.EvaluateAsync("([key, value]) => localStorage.setItem(key, value)", new[] { "weesite:unsaved:editor-94f6dcc0f4cf:" + created.Path, kept });

        await Page.ReopenAsync();
        var offer = Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Unsaved changes" });
        await Expect(offer).ToContainTextAsync("made to an earlier version");
        await Expect(offer.GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Restore" }).ClickAsync();
        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync("Typed against the first text.");
        await Page.GetByLabel("Text (Markdown)").PressAsync("Control+s");

        await Expect(Page.GetByRole(AriaRole.Alert).Filter(new() { HasText = "Someone saved this since you opened it" })).ToBeVisibleAsync();
        var stored = await editor.OpenAsync(created.Path, CancellationToken.None);
        await Assert.That(stored!.Draft.Body).Contains("Saved by an app.");
    }

    [Test]
    public async Task The_social_card_image_is_chosen_in_a_dialog()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));

        // F-0098: a dialog of images (no SVG), filling the image and its description.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Choose an image…" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Choose the image" });
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "diagram.svg" })).ToHaveCountAsync(0);

        // T-0123: no banner or content info landmark of its own; NVDA listed them as the page's.
        await Expect(dialog.GetByRole(AriaRole.Banner)).ToHaveCountAsync(0);
        await Expect(dialog.GetByRole(AriaRole.Contentinfo)).ToHaveCountAsync(0);
        await dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" }).FillAsync("sample");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png" }).ClickAsync();
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
    public async Task A_stored_image_brings_the_description_it_was_uploaded_with()
    {
        // F-0139: the list gives each image's kept description (the endpoint is tested on its own;
        // here the list says the sample has one).
        await Page.RouteAsync("**/admin/media.json", async route =>
        {
            var response = await route.FetchAsync();
            var files = System.Text.Json.Nodes.JsonNode.Parse(await response.TextAsync())!.AsArray();
            foreach (var file in files)
            {
                if ((string?)file!["name"] == "b368a21f9e93-sample.png")
                {
                    file["alt"] = "A small blue square";
                }
            }

            await route.FulfillAsync(new() { Response = response, Body = files.ToJsonString() });
        });
        await Page.OpenAsync(site.At("/admin/new/post"));

        await Page.GetByRole(AriaRole.Button, new() { Name = "Choose an image…" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Choose the image" });
        var alt = dialog.GetByLabel("Description of the image (alt text)");
        await dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" }).FillAsync("sample");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png" }).ClickAsync();
        await Expect(alt).ToHaveValueAsync("A small blue square");
        // U-42: the note under the field says what choosing did, and when there's nothing to bring.
        var note = dialog.Locator(".alt-note");
        await Expect(note).ToHaveTextAsync("Filled in with the description sample.png was uploaded with. Change it if this place needs another.");

        // Another image without one takes its place; a typed description stays.
        await dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" }).FillAsync("gradient");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "gradient.jpg" }).ClickAsync();
        await Expect(alt).ToHaveValueAsync("");
        await Expect(note).ToHaveTextAsync("gradient.jpg has no stored description: describe it above.");
        await alt.FillAsync("A gradient, typed");
        await dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" }).FillAsync("sample");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png" }).ClickAsync();
        await Expect(alt).ToHaveValueAsync("A gradient, typed");
        await Expect(note).ToHaveTextAsync("Your description stays. sample.png was uploaded with: “A small blue square”.");

        // Cleared, the next choice fills it again.
        await alt.FillAsync("");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png" }).ClickAsync();
        await Expect(alt).ToHaveValueAsync("A small blue square");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Use", Exact = true }).ClickAsync();
        await Expect(Page.GetByLabel("Image description (alt text)")).ToHaveValueAsync("A small blue square");
    }

    [Test]
    public async Task The_media_picker_inserts_a_stored_image_with_its_description()
    {
        await Page.OpenAsync(site.At("/admin/new/post"));
        var body = Page.GetByLabel("Text (Markdown)");
        await body.FillAsync("Before.");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        var sample = dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png" });
        await dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" }).FillAsync("sample");

        var insert = dialog.GetByRole(AriaRole.Button, new() { Name = "Insert", Exact = true });
        await insert.ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Choose a file to upload, or a stored file.");
        await sample.ClickAsync();
        await Expect(sample).ToHaveAttributeAsync("aria-pressed", "true");
        await insert.ClickAsync();
        await Expect(dialog.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Describe the image first (alt text).");
        await dialog.GetByLabel("Description of the image (alt text)").FillAsync("A blue square");
        await insert.ClickAsync();

        // A paragraph of its own (U-43).
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(body).ToHaveValueAsync("Before.\n\n![A blue square](/media/b368a21f9e93-sample.png)\n\n");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Live preview" }).GetByRole(AriaRole.Img, new() { Name = "A blue square" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task An_image_inserted_before_the_text_had_a_cursor_goes_at_its_end()
    {
        // U-43: it went in at the very start, glued to the first paragraph. Where the cursor was
        // last, it goes there.
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fhello-world.md"));
        var body = Page.GetByLabel("Text (Markdown)");
        var text = await body.InputValueAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert image or file" });
        await dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" }).FillAsync("sample");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png" }).ClickAsync();
        await dialog.GetByLabel("Description of the image (alt text)").FillAsync("A blue square");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Insert", Exact = true }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();

        var after = await body.InputValueAsync();
        await Assert.That(after).StartsWith(text);
        await Assert.That(after[text.Length..]).IsEqualTo((text is [.., '\n'] ? "\n" : "\n\n") + "![A blue square](/media/b368a21f9e93-sample.png)\n\n");

        // At the cursor, left after the first word.
        await body.EvaluateAsync("t => { t.focus(); t.setSelectionRange(4, 4); t.blur(); }");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Insert image or file" }).ClickAsync();
        await dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" }).FillAsync("sample");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png" }).ClickAsync();
        await dialog.GetByLabel("Description of the image (alt text)").FillAsync("Second");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Insert", Exact = true }).ClickAsync();
        await Expect(body).ToHaveValueAsync(after[..4] + "\n\n![Second](/media/b368a21f9e93-sample.png)\n\n" + after[4..]);
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

        await Expect(dialog).ToBeHiddenAsync(new() { Timeout = UploadTimeout });
        await Expect(Page.GetByLabel("Text (Markdown)")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex(@"^\[reading-list\.txt\]\(/media/[0-9a-f]{12}-reading-list\.txt\)\n\n$"));
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
        await Expect(Page.GetByRole(AriaRole.Dialog).Locator(".pick-grid button").First).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens", "media-picker-1280-light.png") });

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task Analytics_shows_a_skeleton_until_the_report_comes_and_says_when_it_doesnt()
    {
        // F-0202: the page first, the report after. Held back here until the skeleton is checked.
        var release = new TaskCompletionSource();
        await Page.RouteAsync("**/admin/analytics/report*", async route =>
        {
            await release.Task;
            await route.ContinueAsync();
        });
        await Page.OpenAsync(site.At("/admin/analytics?days=7"));
        var skeleton = Page.Locator("section.analytics.loading");

        await Expect(skeleton).ToHaveAttributeAsync("aria-busy", "true");
        await Expect(skeleton).ToHaveAccessibleNameAsync("Loading the report…");
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        release.SetResult();
        await Expect(skeleton).ToHaveCountAsync(0);
        await Expect(Page.Locator("section.analytics .stat-tiles dt").First).ToHaveTextAsync("Page views");
        await Expect(Page.Locator(".analytics-refresh")).ToBeVisibleAsync();

        // A report that doesn't come says so in place of the skeleton.
        await Page.UnrouteAsync("**/admin/analytics/report*");
        await Page.RouteAsync("**/admin/analytics/report*", route => route.FulfillAsync(new() { Status = 500, Body = "" }));
        await Page.ReloadAsync();
        await Expect(Page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("The report didn't load. Reload the page to try again.");
        await Expect(Page.Locator("section.analytics[aria-busy]")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_pages_explanation_opens_under_the_info_button_and_closes_again()
    {
        // T-0160: the intro is under an (i) beside the title, a disclosure.
        await Page.OpenAsync(site.At("/admin/analytics"));
        var about = Page.Locator(".page-intro summary");
        var text = Page.Locator(".page-intro-text");

        await Expect(about).ToHaveAccessibleNameAsync("About this page");
        await Expect(text).ToBeHiddenAsync();
        await about.ClickAsync();
        await Expect(text).ToBeVisibleAsync();
        await Expect(text).ToContainTextAsync("no cookies");

        await Page.Keyboard.PressAsync("Escape");
        await Expect(text).ToBeHiddenAsync();
        await Expect(about).ToBeFocusedAsync();

        await Page.Keyboard.PressAsync("Enter");
        await Expect(text).ToBeVisibleAsync();

        // F-0224: Escape with the focus elsewhere belongs to what has it, not the explanation.
        await Page.EvaluateAsync("() => document.activeElement.blur()");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(text).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Heading, new() { Level = 1 }).ClickAsync();
        await Expect(text).ToBeHiddenAsync();
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
        await Expect(rows.Nth(0).GetByLabel("Address")).ToHaveValueAsync("/about");
        await rows.Nth(0).GetByLabel("Goes to").SelectOptionAsync("");
        await Expect(rows.Nth(0).GetByLabel("Address")).ToBeEnabledAsync();
        await Expect(rows.Nth(0).GetByLabel("Address")).ToHaveValueAsync("");
        await Expect(rows.Nth(0).GetByLabel("Address")).ToHaveAttributeAsync("placeholder", "/tags");
        await rows.Nth(0).GetByLabel("Address").FillAsync("/notes");
        await rows.Nth(0).GetByLabel("Goes to").SelectOptionAsync("/contact");
        await Expect(rows.Nth(0).GetByLabel("Address")).ToHaveValueAsync("/contact");
        await rows.Nth(0).GetByLabel("Goes to").SelectOptionAsync("");
        await Expect(rows.Nth(0).GetByLabel("Address")).ToHaveValueAsync("/notes");
    }

    [Test]
    public async Task Moving_a_menu_item_to_the_top_with_the_keyboard_keeps_the_focus_in_its_row()
    {
        // F-0248: the pressed Move up was disabled at the top, and the focus fell to the page.
        await Page.OpenAsync(site.At("/admin/menu"));
        var rows = Page.Locator(".menu-row");
        await rows.Nth(1).GetByRole(AriaRole.Button, new() { Name = "Move up" }).FocusAsync();

        await Page.Keyboard.PressAsync("Enter");

        await Expect(rows.Nth(0).GetByLabel("Label")).ToHaveValueAsync("About");
        await Expect(rows.Nth(0).GetByRole(AriaRole.Button, new() { Name = "Move down" })).ToBeFocusedAsync();
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
        await Expect(Page.Locator("[data-summary]")).ToHaveTextAsync("3 of 26 checks fail");

        // The comment bars need 3 : 1 against the background (T-0170).
        await light.GetByLabel("Comment bar: an editor", new() { Exact = true }).FillAsync("#eeeeee");
        await Expect(Page.Locator("tr[data-mode=light][data-fg=comment-editor][data-bg=bg] td").Nth(3)).ToHaveTextAsync("Too low");
        await Expect(Page.Locator("[data-summary]")).ToHaveTextAsync("4 of 26 checks fail");
        await Expect(Page.Locator("[data-summary]")).ToHaveAttributeAsync("role", "status");
        await Expect(textOnBackground.Locator("td").Nth(2)).ToContainTextAsync("2.8 : 1");
        await Expect(light.GetByLabel("Text (Light): colour picker", new() { Exact = true })).ToHaveValueAsync("#999999");
        var sampleText = await Page.Locator(".theme-sample[data-mode=light]").EvaluateAsync<string>("e => getComputedStyle(e).color");
        await Assert.That(sampleText).IsEqualTo("rgb(153, 153, 153)");
    }

    [Test]
    public async Task A_form_posts_once_while_its_answer_is_on_the_way()
    {
        // Held back a second, so the second press comes while the first is on its way (F-0201).
        var posts = new List<string?>();
        await Page.RouteAsync("**/admin/theme", async route =>
        {
            if (route.Request.Method == "POST")
            {
                lock (posts)
                {
                    posts.Add(route.Request.PostData);
                }

                await Task.Delay(1000);
            }

            await route.ContinueAsync();
        });
        await Page.OpenAsync(site.At("/admin/theme"));
        var check = Page.GetByRole(AriaRole.Button, new() { Name = "Check contrast" });

        // Two presses in a row, as a double click sends them; the second finds the form on its way.
        var busy = await check.EvaluateAsync<string>("b => { b.click(); const state = `${b.getAttribute('aria-disabled')} ${b.form.getAttribute('aria-busy')}`; b.click(); b.form.requestSubmit(b); return state; }");
        await Assert.That(busy).IsEqualTo("true true");

        await Expect(Page.GetByText("Every pair has enough contrast.")).ToBeVisibleAsync();
        await Expect(check).Not.ToHaveAttributeAsync("aria-disabled", "true");
        await Assert.That(posts.Count).IsEqualTo(1);
        await Assert.That(posts[0]).Contains("Action=check");
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
        await Expect(Page.Locator("ws-tag-input [role=status]")).ToHaveTextAsync("Added #two.");
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
        // checks the zip is written asynchronously all the way to its last byte. The button posts
        // the backup page's form (T-0122).
        await Page.OpenAsync(site.At("/admin/backup"));

        var download = await Page.RunAndWaitForDownloadAsync(() => Page.GetByRole(AriaRole.Button, new() { Name = "Download the backup (.zip)" }).ClickAsync());
        await using var stream = await download.CreateReadStreamAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(buffer);

        await Assert.That(download.SuggestedFilename).StartsWith("weesite-backup-");
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

        // A table inserted (its dialog's form submits inside the editor's): still asks (F-0114).
        asked.Clear();
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "Table" }).ClickAsync();
        await Page.GetByRole(AriaRole.Dialog, new() { Name = "Insert a table" }).GetByRole(AriaRole.Button, new() { Name = "Insert" }).ClickAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "Posts" }).First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/posts$"));
        await Assert.That(asked).IsEquivalentTo(["beforeunload"]);

        // Saved: no question.
        asked.Clear();
        await Page.OpenAsync(site.At("/admin/new/post"));
        await Page.GetByLabel("Title").FillAsync("Saved before leaving");
        await Page.GetByLabel("Slug").FillAsync("saved-before-leaving");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).First.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/admin/edit\\?path=posts%2F[^&]+$"));
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
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("uploaded="), new() { Timeout = UploadTimeout });
        await Page.OpenAsync(site.At("/admin/media?q=dialog-"));
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
        await Expect(Page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Deleted" })).ToContainTextAsync($"Deleted {name}.");
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task Each_editor_has_the_invitation_to_copy()
    {
        await Page.OpenAsync(site.At("/admin/editors"));

        // ADR 0010: without email, the page says so; the row's Invitation button opens it to copy.
        await Expect(Page.GetByText("Email isn't set up, so no invitation is sent")).ToBeVisibleAsync();
        var open = Page.GetByRole(AriaRole.Button, new() { Name = "Invitation for writer@example.com" });
        await open.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "Invitation for writer@example.com" });
        var text = dialog.GetByRole(AriaRole.Textbox, new() { Name = "The invitation's text" });
        await Expect(text).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^admin@example\\.com has added you to Example Site as an author\\."));
        await Expect(text).ToHaveJSPropertyAsync("readOnly", true);
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Copy" })).ToBeVisibleAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();

        // Close shuts it, and focus goes back to the button.
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(open).ToBeFocusedAsync();
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

        // The dialog's buttons don't touch (maintainer, 2026-11-13).
        await Expect(dialog.Locator(".dialog-actions")).ToHaveCSSAsync("column-gap", "8px");
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

        // T-0131: 48 files at first, then more on request.
        await Expect(dialog.Locator(".count")).ToContainTextAsync("48 of ");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Show more" }).ClickAsync();
        await Expect(dialog.Locator(".count")).Not.ToContainTextAsync("48 of ");

        // F-0101: typing narrows the list; Enter doesn't insert.
        var filter = dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" });
        await filter.FillAsync("SAMPLE");
        await filter.PressAsync("Enter");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png" })).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "diagram.svg" })).ToBeHiddenAsync();
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
        await dialog.GetByRole(AriaRole.Searchbox, new() { Name = "Filter the stored files by name" }).FillAsync("sample");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "sample.png", Exact = true }).ClickAsync();
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
    [Arguments("/admin/tags/suggest")]
    [Arguments("/admin/backup")]
    [Arguments("/admin/preview/post/notes-for-a-future-post")]
    [Arguments("/admin/analytics")]
    [Arguments("/admin/export?path=posts%2Fnotes-on-static-rendering.md")]
    [Arguments("/admin/versions?path=posts%2Fnotes-on-static-rendering.md")]
    [Arguments("/admin/delete?path=posts%2Fnotes-on-static-rendering.md")]
    [Arguments("/admin/media/delete?name=0e3d1bf78702-diagram.svg")]
    [Arguments("/admin/media/archive?name=0e3d1bf78702-diagram.svg")]
    [Arguments("/admin/media/replace?name=b368a21f9e93-sample.png")]
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
