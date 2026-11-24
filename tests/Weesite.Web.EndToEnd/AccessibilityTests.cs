using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The accessibility review's automated part (T-0123), WCAG 2.2 AA: axe with the WCAG 2.0 to 2.2
/// A and AA rules and its best practices on every page, in both themes, on a phone and a desktop;
/// reflow at 320 CSS pixels (400 % zoom) and 640 (200 % zoom on a 1280 screen) without scrolling
/// sideways; every keyboard stop showing where the focus is, with no trap; and no motion when the
/// reader asks for less. The screen reader check is by hand (docs/accessibility.md).
/// </summary>
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed class AccessibilityTests(SiteServer site) : PageTest
{
    private static readonly AxeRunOptions WcagAa = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa", "best-practice"],
        },
    };

    private static readonly string[] PublicPages =
    [
        "/", "/blog", "/blog/notes-on-static-rendering", "/blog/walking-the-south-coast-of-crete",
        "/blog/notes-on-dialing-in-espresso", "/blog/a-small-rego-baseline-for-infrastructure",
        "/blog/hello-world", "/blog/tags", "/blog/tags/web", "/search?q=rendering", "/search?q=zzzzqqq", "/about",
        "/typography", "/privacy", "/terms", "/contact", "/no-such-page", "/admin/sign-in",
    ];

    private static readonly string[] AdminPages =
    [
        "/admin", "/admin/posts", "/admin/pages", "/admin/new/post", "/admin/edit?path=posts%2Fnotes-on-static-rendering.md",
        "/admin/versions?path=posts%2Fnotes-on-static-rendering.md", "/admin/preview/post/notes-for-a-future-post",
        "/admin/media", "/admin/media?show=unused", "/admin/media/replace?name=b368a21f9e93-sample.png",
        "/admin/media/archive?name=0e3d1bf78702-diagram.svg", "/admin/media/delete?name=0e3d1bf78702-diagram.svg",
        "/admin/profile", "/admin/settings", "/admin/reset", "/admin/menu", "/admin/theme", "/admin/tags", "/admin/tags/suggest", "/admin/editors",
        "/admin/analytics", "/admin/analytics/posts", "/admin/backup", "/admin/audit", "/admin/export?path=posts%2Fnotes-on-static-rendering.md",
        "/admin/delete?path=posts%2Fnotes-on-static-rendering.md", "/admin/denied", "/admin/forbidden", "/admin/messages", "/admin/comments",
    ];

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static readonly int[] Widths = [320, 1280];

    private static readonly string[] Schemes = ["light", "dark"];

    /// <summary>Every public page at both widths and in both themes.</summary>
    /// <returns>The cases.</returns>
    public static IEnumerable<Func<(string Path, int Width, string Scheme)>> PublicCases() =>
        from path in PublicPages
        from width in Widths
        from scheme in Schemes
        select (Func<(string, int, string)>)(() => (path, width, scheme));

    /// <summary>Every admin page, light on a phone and dark on a desktop.</summary>
    /// <returns>The cases.</returns>
    public static IEnumerable<Func<(string Path, int Width, string Scheme)>> AdminCases() =>
        AdminPages.SelectMany(path => new Func<(string, int, string)>[] { () => (path, 320, "light"), () => (path, 1280, "dark") });

    /// <summary>Every page at 200 % zoom on a 1280 pixel screen.</summary>
    /// <returns>The cases.</returns>
    public static IEnumerable<Func<(string Path, bool Admin)>> ZoomCases() =>
        PublicPages.Select(p => (Func<(string, bool)>)(() => (p, false)))
            .Concat(AdminPages.Select(p => (Func<(string, bool)>)(() => (p, true))));

    [Test]
    [MethodDataSource(nameof(PublicCases))]
    public async Task A_public_page_passes_axe_and_reflows((string Path, int Width, string Scheme) test) =>
        await CheckAsync(test.Path, test.Width, test.Scheme, admin: false);

    [Test]
    [MethodDataSource(nameof(AdminCases))]
    public async Task An_admin_page_passes_axe_and_reflows((string Path, int Width, string Scheme) test) =>
        await CheckAsync(test.Path, test.Width, test.Scheme, admin: true);

    [Test]
    [MethodDataSource(nameof(ZoomCases))]
    public async Task A_page_reflows_at_200_percent_zoom((string Path, bool Admin) test)
    {
        // 1.4.10: a 1280 pixel window at 200 % is 640 CSS pixels wide.
        await SignInAsync(test.Admin);
        await Page.SetViewportSizeAsync(640, 800);
        await Page.OpenAsync(site.At(test.Path));

        await Assert.That(await SidewaysOverflowAsync()).IsEmpty();
    }

    [Test]
    [Arguments("/")]
    [Arguments("/blog")]
    [Arguments("/blog/notes-on-static-rendering")]
    [Arguments("/blog/tags")]
    [Arguments("/privacy")]
    public async Task Small_public_links_are_at_least_24_pixels_on_a_phone(string path)
    {
        // U-24, WCAG 2.5.8: the header menu, tags, the footer and the other small links outside the
        // text (inline links in a sentence are exempt).
        await Page.SetViewportSizeAsync(390, 844);
        await Page.OpenAsync(site.At(path));

        var small = await Page.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll(".site-menu a, .site-footer a, .crumbs a, .list-foot a, .tag-list a, .tag-index a, a.tag, .toc a, .mini-list a, aside .more")]
                .filter((a) => a.checkVisibility())
                .map((a) => [a, a.getBoundingClientRect()])
                .filter(([, box]) => box.height < 24 || box.width < 24)
                .map(([a, box]) => `${a.textContent.trim()}: ${Math.round(box.width)} × ${Math.round(box.height)}`)
            """);

        await Assert.That(small).IsEmpty();
    }

    [Test]
    public async Task Comment_times_are_at_least_24_pixels_on_a_phone()
    {
        // U-24, WCAG 2.5.8: the time beside a comment is a button ("2 days ago", which shows the
        // exact time), and was only as tall as its text.
        await Page.SetViewportSizeAsync(390, 844);
        await Page.OpenAsync(site.At("/blog/notes-on-static-rendering"));
        var times = Page.Locator(".comment-list .time-toggle");
        await Expect(times.First).ToBeVisibleAsync();

        var small = await times.EvaluateAllAsync<string[]>("""
            (buttons) => buttons
                .map((b) => [b, b.getBoundingClientRect()])
                .filter(([, box]) => box.height < 24 || box.width < 24)
                .map(([b, box]) => `${b.textContent.trim()}: ${Math.round(box.width)} × ${Math.round(box.height)}`)
            """);

        await Assert.That(await times.CountAsync()).IsGreaterThan(1);
        await Assert.That(small).IsEmpty();
    }

    [Test]
    [Arguments(1280)]
    [Arguments(320)]
    public async Task The_editor_s_statuses_stay_on_one_line(int width)
    {
        // F-0148: wrapping (for 400 % zoom) put Archived on a second row in the side column.
        await SignInAsync(admin: true);
        await Page.SetViewportSizeAsync(width, 900);
        await Page.OpenAsync(site.At("/admin/edit?path=posts%2Fnotes-on-static-rendering.md"));

        var tops = await Page.Locator(".seg-ctl[role=radiogroup] label").EvaluateAllAsync<double[]>("labels => labels.map(l => Math.round(l.getBoundingClientRect().top))");

        await Assert.That(tops.Length).IsEqualTo(4);
        await Assert.That(tops.Distinct().Count()).IsEqualTo(1);
        await Assert.That(await SidewaysOverflowAsync()).IsEmpty();
    }

    [Test]
    [Arguments("/", false)]
    [Arguments("/blog/notes-on-static-rendering", false)]
    [Arguments("/search?q=rendering", false)]
    [Arguments("/admin/posts", true)]
    [Arguments("/admin/edit?path=posts%2Fnotes-on-static-rendering.md", true)]
    [Arguments("/admin/media", true)]
    [Arguments("/admin/settings", true)]
    public async Task Every_keyboard_stop_shows_the_focus_and_none_traps_it(string path, bool admin)
    {
        // 2.4.7 and 2.4.11 (focus visible, not hidden) and 2.1.2 (no keyboard trap).
        await SignInAsync(admin);
        await Page.SetViewportSizeAsync(1280, 900);
        await Page.OpenAsync(site.At(path));

        var stops = new List<string>();
        var problems = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            await Page.Keyboard.PressAsync("Tab");
            var json = await Page.EvaluateAsync<string?>(FocusScript);
            var stop = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<FocusStop>(json, Json);
            if (stop is null)
            {
                break;
            }

            if (stops.Count > 0 && stops[0] == stop.Id)
            {
                break;
            }

            stops.Add(stop.Id);
            if (!stop.Visible)
            {
                problems.Add($"{stop.Id}: no visible focus");
            }

            if (stop.Hidden)
            {
                problems.Add($"{stop.Id}: focused out of view or covered");
            }
        }

        await Assert.That(stops.Count).IsGreaterThan(3);
        await Assert.That(stops.Count).IsLessThan(400);
        await Assert.That(problems).IsEmpty();
    }

    [Test]
    [Arguments("/")]
    [Arguments("/blog/notes-on-static-rendering")]
    public async Task Nothing_moves_when_the_reader_asks_for_less_motion(string path)
    {
        // 2.3.3: no animation or smooth scrolling with prefers-reduced-motion: reduce. Following a
        // link is what would start a view transition.
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await Page.OpenAsync(site.At(path));
        var blog = Page.GetByRole(AriaRole.Link, new() { Name = "Blog", Exact = true }).First;
        await Expect(blog).ToHaveAttributeAsync("href", "/blog");

        // F-0243: WaitForURLAsync, once the address matched, also waited for the new page's load
        // event, which now and then never came on CI (30 s, with the network long idle), as in
        // F-0104. The address is checked as F-0104 does, and the page's heading says it has
        // rendered; animations and the scroll behaviour don't wait for the load event.
        await blog.ClickAsync();
        await Expect(Page).ToHaveURLAsync(site.At("/blog"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Blog", Level = 1 })).ToBeVisibleAsync();

        await Assert.That(await Page.EvaluateAsync<int>("() => document.getAnimations().length")).IsEqualTo(0);
        await Assert.That(await Page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).scrollBehavior")).IsEqualTo("auto");
    }

    // The focused element: an ID for it, whether it or a near ancestor shows an outline or shadow
    // only while focused, and whether it's out of view or under something else. "Only while
    // focused": each is read focused and then blurred, and only one that changes counts; one
    // that's always there (the current menu link's underline shadow) showed no focus (F-0252).
    // The element is focused again after, which keeps :focus-visible after a key press.
    private const string FocusScript = """
        () => {
            const el = document.activeElement;
            if (!el || el === document.body) return null;
            const id = el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + '[' + (el.getAttribute('aria-label') || el.textContent || el.getAttribute('name') || '').trim().slice(0, 40) + ']';
            const chain = [];
            for (let node = el, depth = 0; node && depth < 4; node = node.parentElement, depth++) chain.push(node);
            const look = (node) => {
                const s = getComputedStyle(node);
                const outline = s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0 ? `${s.outlineStyle} ${s.outlineWidth} ${s.outlineColor} ${s.outlineOffset}` : '';
                const shadow = s.boxShadow && s.boxShadow !== 'none' ? s.boxShadow : '';
                return { outline, shadow };
            };
            const focused = chain.map(look);
            el.blur();
            const blurred = chain.map(look);
            el.focus({ preventScroll: true });
            const visible = chain.some((_, i) =>
                (focused[i].outline !== '' && focused[i].outline !== blurred[i].outline)
                || (focused[i].shadow !== '' && focused[i].shadow !== blurred[i].shadow));
            const box = el.getBoundingClientRect();
            const inView = box.width > 0 && box.height > 0 && box.bottom > 0 && box.top < innerHeight && box.right > 0 && box.left < innerWidth;
            let covered = false;
            if (inView) {
                const x = Math.min(Math.max(box.left + box.width / 2, 0), innerWidth - 1);
                const y = Math.min(Math.max(box.top + Math.min(box.height / 2, 8), 0), innerHeight - 1);
                const top = document.elementFromPoint(x, y);
                covered = !!top && !el.contains(top) && !top.contains(el) && !(el.labels && [...el.labels].some(l => l.contains(top)));
            }
            return JSON.stringify({ id, visible, hidden: !inView || covered });
        }
        """;

    private async Task CheckAsync(string path, int width, string scheme, bool admin)
    {
        await SignInAsync(admin);
        await Page.SetViewportSizeAsync(width, 900);
        await Page.EmulateMediaAsync(new() { ColorScheme = scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        await Page.OpenAsync(site.At(path));

        var results = await Page.RunAxe(WcagAa);

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help} ({string.Join(", ", v.Nodes.Take(3).Select(n => string.Join(' ', n.Target)))})")).IsEmpty();
        await Assert.That(await SidewaysOverflowAsync()).IsEmpty();
    }

    private async Task SignInAsync(bool admin)
    {
        if (admin)
        {
            await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { ["X-Test-Role"] = "Admin" });
        }
    }

    // 1.4.10: the page doesn't scroll sideways. Tables, code and other regions that scroll on their
    // own are allowed; the page as a whole isn't. Names what sticks out, if anything.
    private async Task<IReadOnlyList<string>> SidewaysOverflowAsync() =>
        await Page.EvaluateAsync<string[]>("""
            () => {
                if (document.documentElement.scrollWidth <= innerWidth + 1) return [];
                const clipped = (e) => {
                    for (let p = e.parentElement; p && p !== document.body; p = p.parentElement) {
                        if (['auto', 'scroll', 'hidden', 'clip'].includes(getComputedStyle(p).overflowX)) return true;
                    }
                    return false;
                };
                const named = [...document.querySelectorAll('body *')]
                    .filter(e => e.getBoundingClientRect().right > innerWidth + 1 && !clipped(e))
                    .slice(0, 5)
                    .map(e => e.tagName.toLowerCase() + (e.className ? '.' + String(e.className).split(' ').join('.') : '') + ' ' + Math.round(e.getBoundingClientRect().right));

                // Absolutely placed text inside a scrolling box that isn't positioned escapes it and
                // still widens the page (T-0157): the page's width is what counts.
                return named.length > 0 ? named : ['the page is ' + document.documentElement.scrollWidth + ' pixels wide'];
            }
            """);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by Playwright from the script's result.")]
    private sealed record FocusStop(string Id, bool Visible, bool Hidden);
}
