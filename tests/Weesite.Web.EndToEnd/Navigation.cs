using Microsoft.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Page loads that survive a network change on the test machine (F-0037): when another test
/// project starts a container, the runner gains a network interface and Chromium aborts loads in
/// flight with <c>net::ERR_NETWORK_CHANGED</c>, or (when the error page wins the race) reports the
/// load as "interrupted by another navigation to chrome-error://". Only those are retried; any other
/// error fails the test.
/// </summary>
internal static class Navigation
{
    private const int Attempts = 3;

    /// <summary>Loads a page, retrying after a network change.</summary>
    /// <param name="page">The page.</param>
    /// <param name="url">The address.</param>
    /// <param name="options">Load options.</param>
    /// <returns>The response.</returns>
    public static Task<IResponse?> OpenAsync(this IPage page, string url, PageGotoOptions? options = null) =>
        RetryAsync(() => page.GotoAsync(url, options));

    /// <summary>Reloads a page, retrying after a network change.</summary>
    /// <param name="page">The page.</param>
    /// <returns>The response.</returns>
    public static Task<IResponse?> ReopenAsync(this IPage page) => RetryAsync(() => page.ReloadAsync());

    private static async Task<IResponse?> RetryAsync(Func<Task<IResponse?>> load)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await load();
            }
            catch (PlaywrightException e) when (attempt < Attempts && IsNetworkChange(e))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }

    private static bool IsNetworkChange(PlaywrightException e) =>
        e.Message.Contains("ERR_NETWORK_CHANGED", StringComparison.Ordinal)
        || e.Message.Contains("interrupted by another navigation to \"chrome-error://", StringComparison.Ordinal);
}
