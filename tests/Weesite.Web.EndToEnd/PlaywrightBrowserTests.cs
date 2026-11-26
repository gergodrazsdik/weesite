namespace Weesite.Web.EndToEnd;

/// <summary>Which browser a run launches for a value of <c>BROWSER</c> (F-0259).</summary>
internal sealed class PlaywrightBrowserTests
{
    [Test]
    [Arguments("chromium", "chromium")]
    [Arguments("firefox", "firefox")]
    [Arguments("webkit", "webkit")]
    [Arguments(null, "chromium")]
    [Arguments("", "chromium")]
    [Arguments("wslview", "chromium")]
    [Arguments("/home/someone/bin/open-link", "chromium")]
    [Arguments("Chromium", "chromium")]
    public async Task A_link_opener_in_BROWSER_means_chromium(string? value, string expected)
    {
        await Assert.That(PlaywrightBrowser.For(value)).IsEqualTo(expected);
    }

    [Test]
    public async Task The_run_sees_a_playwright_browser()
    {
        await Assert.That(PlaywrightBrowser.Names).Contains(Environment.GetEnvironmentVariable("BROWSER")!);
    }
}
