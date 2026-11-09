using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Screenshots of the key pages at phone and desktop width, light and dark, written to
/// artifacts/screens/ for people and agents to look at (`just screens`). They are not compared
/// pixel by pixel: fonts differ between machines and CI. Structure is checked in
/// <see cref="PageTests"/>.
/// </summary>
[Category("screens")]
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed class Screens(SiteServer site) : BrowserTest
{
    private static readonly string OutputDirectory = Path.Combine(FindRepoRoot(), "artifacts", "screens");

    /// <summary>Gets the pages to capture: a file name and a path.</summary>
    /// <returns>Pairs of name and path.</returns>
    public static IEnumerable<(string Name, string Path)> Pages() =>
    [
        ("home", "/"),
        ("blog", "/blog"),
        ("post", "/blog/notes-on-static-rendering"),
        ("tag", "/blog/tags/web"),
        ("photo-post", "/blog/walking-the-south-coast-of-crete"),
        ("search", "/search?q=rendering"),
        ("page", "/about"),
        ("not-found", "/no-such-page"),
    ];

    /// <summary>Gets the editor pages to capture, signed in as an admin.</summary>
    /// <returns>Pairs of name and path.</returns>
    public static IEnumerable<(string Name, string Path)> AdminPages() =>
    [
        ("admin-posts", "/admin/posts"),
        ("editor", "/admin/edit?path=posts%2Fnotes-on-static-rendering.md"),
        ("admin-media", "/admin/media"),
        ("admin-profile", "/admin/profile"),
    ];

    [Test]
    [MethodDataSource(nameof(AdminPages))]
    public async Task CaptureSignedIn((string Name, string Path) target) =>
        await CaptureAsync(target, new Dictionary<string, string> { ["X-Test-Role"] = "Admin" });

    [Test]
    [MethodDataSource(nameof(Pages))]
    public async Task Capture((string Name, string Path) target) => await CaptureAsync(target, null);

    private async Task CaptureAsync((string Name, string Path) target, Dictionary<string, string>? headers)
    {
        Directory.CreateDirectory(OutputDirectory);

        foreach (var width in new[] { 390, 1280 })
        {
            foreach (var (scheme, label) in new[] { (ColorScheme.Light, "light"), (ColorScheme.Dark, "dark") })
            {
                await using var context = await Browser.NewContextAsync(new()
                {
                    ViewportSize = new() { Width = width, Height = 900 },
                    ColorScheme = scheme,
                    ExtraHTTPHeaders = headers,
                });
                var page = await context.NewPageAsync();
                await page.GotoAsync(site.At(target.Path));

                var file = Path.Combine(OutputDirectory, $"{target.Name}-{width}-{label}.png");
                await page.ScreenshotAsync(new() { Path = file, FullPage = true });
                await Assert.That(File.Exists(file)).IsTrue();
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Weesite.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Weesite.slnx not found above the test binaries.");
    }
}
