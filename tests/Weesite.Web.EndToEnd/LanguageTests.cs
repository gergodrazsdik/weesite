using System.Diagnostics.CodeAnalysis;
using Microsoft.Playwright;
using TUnit.Core.Interfaces;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>A site in another language in a real browser (ADR 0008, T-0060).</summary>
[ClassDataSource<HungarianSite>(Shared = SharedType.PerClass)]
internal sealed class LanguageTests(HungarianSite site) : PageTest
{
    [Test]
    [Arguments(1280)]
    [Arguments(390)]
    public async Task Short_dates_stay_on_one_line_and_titles_line_up(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        await Page.OpenAsync(new Uri(site.BaseAddress, "/blog").ToString());

        var dates = Page.Locator(".post-list.dated .when time");
        var titles = Page.Locator(".post-list.dated h3");
        await Expect(dates.First).ToBeVisibleAsync();
        var heights = new HashSet<double>();
        foreach (var date in await dates.AllAsync())
        {
            heights.Add(Math.Round((await date.BoundingBoxAsync())!.Height));
        }

        var lefts = new HashSet<double>();
        foreach (var title in await titles.AllAsync())
        {
            lefts.Add(Math.Round((await title.BoundingBoxAsync())!.X));
        }

        await Assert.That(await Page.Locator(".post-list.dated").First.InnerTextAsync()).Contains("szept.");
        await Assert.That(heights).Count().IsEqualTo(1);
        await Assert.That(lefts).Count().IsEqualTo(1);
        var screens = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens");
        await Page.ScreenshotAsync(new() { Path = Path.Combine(screens, $"blog-hu-{width}-light.png") });
    }

    [Test]
    public async Task A_post_reads_in_hungarian_with_its_scripts()
    {
        await Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await Page.SetViewportSizeAsync(1280, 1600);
        await Page.OpenAsync(new Uri(site.BaseAddress, "/blog/notes-on-static-rendering").ToString());

        var block = Page.Locator(".code-block").First;
        await block.GetByRole(AriaRole.Button, new() { Name = "Másolás" }).ClickAsync();
        await Expect(block.GetByRole(AriaRole.Status)).ToHaveTextAsync("A kód a vágólapra került");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Megosztás", Exact = true })).ToHaveCountAsync(2);
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Ezen az oldalon" })).ToBeVisibleAsync();
        var screens = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens");
        await Page.ScreenshotAsync(new() { Path = Path.Combine(screens, "post-hu-1280-light.png"), FullPage = true });
    }
}

/// <summary>The sample site with Hungarian as its language, for this class only.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class HungarianSite : IAsyncInitializer, IAsyncDisposable
{
    private readonly SiteFactory _factory = new();

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var settings = Path.Combine(_factory.ContentPath, "settings", "site.json");
        await File.WriteAllTextAsync(settings, (await File.ReadAllTextAsync(settings)).Replace("\"language\": \"en\",", "\"language\": \"hu\",", StringComparison.Ordinal));
        BaseAddress = await _factory.StartAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
