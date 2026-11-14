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
        "/", "/blog", "/blog?page=2", "/blog/notes-on-static-rendering", "/blog/walking-the-south-coast-of-crete",
        "/blog/hello-world", "/blog/tags", "/blog/tags/web", "/search?q=rendering", "/search?q=zzzzqqq", "/about",
        "/typography", "/privacy", "/terms", "/no-such-page", "/admin/sign-in",
    ];

    private static readonly string[] AdminPages =
    [
        "/admin", "/admin/posts", "/admin/pages", "/admin/new/post", "/admin/edit?path=posts%2Fnotes-on-static-rendering.md",
        "/admin/versions?path=posts%2Fnotes-on-static-rendering.md", "/admin/preview/post/notes-for-a-future-post",
        "/admin/media", "/admin/media?show=unused", "/admin/media/replace?name=b368a21f9e93-sample.png",
        "/admin/media/archive?name=0e3d1bf78702-diagram.svg", "/admin/media/delete?name=0e3d1bf78702-diagram.svg",
        "/admin/profile", "/admin/settings", "/admin/menu", "/admin/theme", "/admin/tags", "/admin/editors",
        "/admin/analytics", "/admin/backup", "/admin/audit", "/admin/export?path=posts%2Fnotes-on-static-rendering.md",
        "/admin/delete?path=posts%2Fnotes-on-static-rendering.md", "/admin/denied", "/admin/forbidden",
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
        // 2.3.3: no animation or smooth scrolling with prefers-reduced-motion: reduce.
        await Page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        await Page.OpenAsync(site.At(path));
        await Page.GetByRole(AriaRole.Link, new() { Name = "Blog", Exact = true }).First.ClickAsync();
        await Page.WaitForURLAsync(site.At("/blog"));

        await Assert.That(await Page.EvaluateAsync<int>("() => document.getAnimations().length")).IsEqualTo(0);
        await Assert.That(await Page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).scrollBehavior")).IsEqualTo("auto");
    }

    // The focused element: an ID for it, whether it or a near ancestor shows an outline or shadow
    // only while focused, and whether it's out of view or under something else.
    private const string FocusScript = """
        () => {
            const el = document.activeElement;
            if (!el || el === document.body) return null;
            const id = el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') + '[' + (el.getAttribute('aria-label') || el.textContent || el.getAttribute('name') || '').trim().slice(0, 40) + ']';
            const shows = (node) => {
                const s = getComputedStyle(node);
                return (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0) || (s.boxShadow && s.boxShadow !== 'none');
            };
            let visible = false;
            for (let node = el, depth = 0; node && depth < 4; node = node.parentElement, depth++) {
                if (shows(node)) { visible = true; break; }
            }
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
                return [...document.querySelectorAll('body *')]
                    .filter(e => e.getBoundingClientRect().right > innerWidth + 1 && !clipped(e))
                    .slice(0, 5)
                    .map(e => e.tagName.toLowerCase() + (e.className ? '.' + String(e.className).split(' ').join('.') : '') + ' ' + Math.round(e.getBoundingClientRect().right));
            }
            """);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by Playwright from the script's result.")]
    private sealed record FocusStop(string Id, bool Visible, bool Hidden);
}
