using Microsoft.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Content Security Policy violations in the browser (T-0053): the site sends a strict policy, so a
/// page that needs an inline script or style, or loads something from elsewhere, breaks quietly.
/// </summary>
internal static class Csp
{
    /// <summary>Records every violation from the next page load on.</summary>
    /// <param name="page">The page.</param>
    /// <returns>A task.</returns>
    public static Task WatchCspAsync(this IPage page) =>
        page.AddInitScriptAsync("document.addEventListener('securitypolicyviolation', e => (window.__cspViolations ??= []).push(`${e.effectiveDirective} blocked ${e.blockedURI || 'inline'}`));");

    /// <summary>Gets the violations so far, once the page has loaded and its pending tasks have run.</summary>
    /// <param name="page">The page.</param>
    /// <returns>Each violation: the directive and what it blocked.</returns>
    /// <remarks>
    /// Not NetworkIdle (no request for 500 ms), which a page that fetches now and then, or loads
    /// images as they scroll into view, may never reach: it timed out after 30 s on admin pages
    /// whose load had long fired (F-0243). The browser reports a violation in a task of its own,
    /// queued when it blocks something; the page's idle callback runs after the tasks queued
    /// before it, so every violation of the load and the scripts it ran is in by then.
    /// </remarks>
    public static async Task<string[]> CspViolationsAsync(this IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.Load);
        return await page.EvaluateAsync<string[]>(
            "() => new Promise(resolve => requestIdleCallback(() => resolve(window.__cspViolations ?? []), { timeout: 5000 }))");
    }
}
