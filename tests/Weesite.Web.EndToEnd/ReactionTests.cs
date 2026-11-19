using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Core.Interfaces;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Reactions to posts in the browser (ADR 0011): pressing, remembering, taking back, a refusal put
/// right, and nothing without JavaScript. A site of their own, so the counts start at zero; each
/// test reacts to another post, since the server counts one of each per post per day from the
/// browsers' shared address and User-Agent.
/// </summary>
[ClassDataSource<ReactionSite>(Shared = SharedType.PerClass)]
internal sealed class ReactionTests(ReactionSite site) : PageTest
{
    [Test]
    public async Task A_reaction_counts_is_remembered_and_is_taken_back()
    {
        await Page.WatchCspAsync();
        await Page.OpenAsync(site.At("/blog/hello-world"));

        var group = Page.GetByRole(AriaRole.Group, new() { Name = "React to this post" });
        var like = group.GetByRole(AriaRole.Button, new() { Name = "Like 0" });
        await Expect(like).ToHaveAttributeAsync("aria-pressed", "false");

        await like.ClickAsync();
        var liked = group.GetByRole(AriaRole.Button, new() { Name = "Like 1" });
        await Expect(liked).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Page.Locator(".reaction[data-kind=like]")).Not.ToHaveAttributeAsync("aria-disabled", "true");

        // The server counted it, and this browser remembers it.
        await Page.ReopenAsync();
        await Expect(group.GetByRole(AriaRole.Button, new() { Name = "Like 1" })).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(group.GetByRole(AriaRole.Button, new() { Name = "Love 0" })).ToHaveAttributeAsync("aria-pressed", "false");

        await group.GetByRole(AriaRole.Button, new() { Name = "Like 1" }).ClickAsync();
        await Expect(group.GetByRole(AriaRole.Button, new() { Name = "Like 0" })).ToHaveAttributeAsync("aria-pressed", "false");
        await Expect(Page.Locator(".reaction[data-kind=like]")).Not.ToHaveAttributeAsync("aria-disabled", "true");
        await Page.ReopenAsync();
        await Expect(group.GetByRole(AriaRole.Button, new() { Name = "Like 0" })).ToHaveAttributeAsync("aria-pressed", "false");

        var axe = await Page.Locator("ws-reactions").RunAxe(new AxeRunOptions
        {
            RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa", "best-practice"] },
        });
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();
    }

    [Test]
    public async Task A_refused_reaction_is_put_right_and_said()
    {
        await Page.OpenAsync(site.At("/blog/notes-on-static-rendering"));
        await Page.RouteAsync("**/visitor/reactions", async route =>
        {
            if (route.Request.Method == "POST")
            {
                await route.FulfillAsync(new() { Status = 429, Body = "Too many" });
            }
            else
            {
                await route.ContinueAsync();
            }
        });

        var thanks = Page.GetByRole(AriaRole.Button, new() { Name = "Thanks 0" });
        await thanks.ClickAsync();

        await Expect(Page.Locator("ws-reactions").GetByRole(AriaRole.Status)).ToHaveTextAsync("That's a lot of reactions at once. Please try again later.");
        await Expect(thanks).ToHaveAttributeAsync("aria-pressed", "false");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Thanks 1" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Without_javascript_there_are_no_reactions()
    {
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();

        await page.OpenAsync(site.At("/blog/walking-the-south-coast-of-crete"));

        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync();
        await Expect(page.Locator("ws-reactions")).ToBeHiddenAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Like") })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Screenshots_of_given_reactions()
    {
        var output = Weesite.Tests.RepoPaths.Artifacts("screens");
        Directory.CreateDirectory(output);
        var first = true;
        foreach (var width in new[] { 390, 1280 })
        {
            foreach (var (scheme, label) in new[] { (ColorScheme.Light, "light"), (ColorScheme.Dark, "dark") })
            {
                await using var context = await Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = 900 }, ColorScheme = scheme });
                var page = await context.NewPageAsync();
                await page.OpenAsync(site.At("/blog/a-weekend-with-haskell-type-classes"));
                var reactions = page.Locator("ws-reactions");
                await Expect(reactions).ToBeVisibleAsync();

                // Given once on the server; the other screenshots remember it in their own storage.
                if (first)
                {
                    await page.GetByRole(AriaRole.Button, new() { Name = "Insightful 0" }).ClickAsync();
                    await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Insightful 1" })).ToHaveAttributeAsync("aria-pressed", "true");

                    // Shown at once; wait for the server's answer before the page closes.
                    await Expect(page.Locator(".reaction[data-kind=insightful]")).Not.ToHaveAttributeAsync("aria-disabled", "true");
                    first = false;
                }
                else
                {
                    await page.EvaluateAsync("() => localStorage.setItem('weesite:reactions', JSON.stringify({ 'posts/a-weekend-with-haskell-type-classes.md': ['insightful'] }))");
                    await page.ReloadAsync();
                    await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Insightful 1" })).ToHaveAttributeAsync("aria-pressed", "true");
                }

                await reactions.ScrollIntoViewIfNeededAsync();
                var file = Path.Combine(output, $"reactions-{width}-{label}.png");
                await page.ScreenshotAsync(new() { Path = file, FullPage = true, Clip = await ClipAsync(page, reactions, width) });
                await Assert.That(File.Exists(file)).IsTrue();
            }
        }
    }

    // The reactions and a little around them, in the page's coordinates.
    private static async Task<Clip> ClipAsync(IPage page, ILocator element, int width)
    {
        var box = (await element.BoundingBoxAsync())!;
        var scroll = await page.EvaluateAsync<float>("() => window.scrollY");
        return new Clip { X = 0, Y = Math.Max(0, box.Y + scroll - 120), Width = width, Height = box.Height + 260 };
    }
}

/// <summary>The sample site, started on its own for the reaction tests.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class ReactionSite : IAsyncInitializer, IAsyncDisposable
{
    private readonly SiteFactory _factory = new();

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>Gets the absolute address of a path on the site.</summary>
    /// <param name="path">A path such as <c>/blog/hello-world</c>.</param>
    /// <returns>The address.</returns>
    public string At(string path) => new Uri(BaseAddress, path).ToString();

    /// <inheritdoc />
    public async Task InitializeAsync() => BaseAddress = await _factory.StartAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
