using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>How the browser tests read the CSP violations (<see cref="Csp"/>).</summary>
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed class CspTests(SiteServer site) : PageTest
{
    [Test]
    public async Task Violations_are_read_on_a_page_that_never_stops_fetching()
    {
        // F-0243: the check waited for no request in 500 ms, which a page that fetches now and then
        // never reaches; it timed out after 30 s on admin pages.
        Page.SetDefaultTimeout(10_000);
        await Page.WatchCspAsync();
        await Page.OpenAsync(site.At("/"));

        // A request every 200 ms, and a violation reported after the load: an inline script added
        // by a script, which the policy blocks.
        await Page.EvaluateAsync(
            """
            () => {
                setInterval(() => fetch('/robots.txt', { cache: 'no-store' }), 200);
                const script = document.createElement('script');
                script.textContent = 'window.ran = true';
                document.body.append(script);
            }
            """);

        var violations = await Page.CspViolationsAsync();

        await Assert.That(violations).Contains(v => v.StartsWith("script-src", StringComparison.Ordinal) && v.EndsWith("blocked inline", StringComparison.Ordinal));
        await Assert.That(await Page.EvaluateAsync<bool>("() => window.ran === true")).IsFalse();
    }
}
