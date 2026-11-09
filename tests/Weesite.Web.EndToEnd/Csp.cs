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

    /// <summary>Gets the violations so far, once the page has settled.</summary>
    /// <param name="page">The page.</param>
    /// <returns>Each violation: the directive and what it blocked.</returns>
    public static async Task<string[]> CspViolationsAsync(this IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return await page.EvaluateAsync<string[]>("() => window.__cspViolations ?? []");
    }
}
