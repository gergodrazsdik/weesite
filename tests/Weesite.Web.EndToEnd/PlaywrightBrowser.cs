using System.Runtime.CompilerServices;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Keeps <c>BROWSER</c> for Playwright (F-0259). TUnit.Playwright takes the browser to launch from
/// it, and fails every test when it names anything but <c>chromium</c>, <c>firefox</c> or
/// <c>webkit</c>. Many machines set it to the command that opens links (<c>wslview</c>, a script
/// that opens them on the Windows side, <c>xdg-open</c>), so a run there uses Chromium, as one
/// without the variable does. This process only: the shell and its link opener keep their value.
/// </summary>
internal static class PlaywrightBrowser
{
    /// <summary>The browsers Playwright can launch, as the variable names them.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal) { "chromium", "firefox", "webkit" };

    /// <summary>The browser a run uses for a value of <c>BROWSER</c>.</summary>
    /// <param name="value">The variable's value, or null when it isn't set.</param>
    /// <returns>The value when it names a Playwright browser; otherwise <c>chromium</c>.</returns>
    public static string For(string? value) => value is not null && Names.Contains(value) ? value : "chromium";

    [ModuleInitializer]
    internal static void Initialize() =>
        Environment.SetEnvironmentVariable("BROWSER", For(Environment.GetEnvironmentVariable("BROWSER")));
}
