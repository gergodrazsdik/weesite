using System.Diagnostics.CodeAnalysis;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Core.Interfaces;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Reactions to posts in the browser (ADR 0011, T-0158): choosing one with the React button,
/// changing it, taking it back, the keyboard, a refusal put right, and nothing without JavaScript.
/// A site of their own, so the counts start at zero; each test reacts to another post, since the
/// server counts one reaction per post per day from the browsers' shared address and User-Agent.
/// </summary>
[ClassDataSource<ReactionSite>(Shared = SharedType.PerClass)]
internal sealed class ReactionTests(ReactionSite site) : PageTest
{
    private static readonly AxeRunOptions Axe = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa", "best-practice"] },
    };

    [Test]
    public async Task The_site_asks_for_an_easy_proof()
    {
        // F-0243: so the browser's proof of work can't hold up the form (SiteFactory.EasyProof).
        var challenge = await Page.APIRequest.GetAsync(site.At("/visitor/challenge?for=reaction"));
        await Assert.That((await challenge.JsonAsync())!.Value.GetProperty("maxNumber").GetRawText()).IsEqualTo(SiteFactory.EasyProof);
    }

    [Test]
    public async Task A_reaction_is_chosen_changed_remembered_and_taken_back()
    {
        await Page.WatchCspAsync();
        await Page.OpenAsync(site.At("/blog/hello-world"));

        var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "React", Exact = true });
        var choices = Page.GetByRole(AriaRole.Group, new() { Name = "Choose a reaction" });
        var announced = Page.Locator(".reactions-announce");
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(Page.GetByRole(AriaRole.List, new() { Name = "Reactions" })).ToBeHiddenAsync();
        await Expect(Page.GetByText("React to this post")).ToHaveCountAsync(0);

        await toggle.ClickAsync();
        await Expect(choices).ToBeVisibleAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(choices.GetByRole(AriaRole.Button, new() { Name = "Like" })).ToBeFocusedAsync();
        var axeOpen = await Page.Locator("ws-reactions").RunAxe(Axe);

        await choices.GetByRole(AriaRole.Button, new() { Name = "Like" }).ClickAsync();
        await Expect(choices).ToBeHiddenAsync();
        var yours = Page.GetByRole(AriaRole.Button, new() { Name = "Your reaction: Like. Change or remove it" });
        await Expect(yours).ToBeFocusedAsync();
        await Expect(announced).ToHaveTextAsync("You reacted: Like.");
        await Expect(Page.Locator(".reaction-tallies li[data-kind=like] .reaction-count")).ToHaveTextAsync("1");
        await Expect(Page.Locator(".reaction-tallies li[data-kind=like]")).ToHaveAttributeAsync("title", "Like");

        // The server counted it, and this browser remembers it.
        await Page.ReopenAsync();
        await Expect(yours).ToBeVisibleAsync();
        await Expect(Page.Locator(".reaction-tallies li[data-kind=like] .reaction-count")).ToHaveTextAsync("1");

        // Changing it moves the count.
        await yours.ClickAsync();
        await Expect(choices.GetByRole(AriaRole.Button, new() { Name = "Like" })).ToHaveAttributeAsync("aria-pressed", "true");
        await choices.GetByRole(AriaRole.Button, new() { Name = "Love" }).ClickAsync();
        await Expect(announced).ToHaveTextAsync("You reacted: Love.");
        await Expect(Page.Locator(".reaction-tallies li[data-kind=love] .reaction-count")).ToHaveTextAsync("1");
        await Expect(Page.Locator(".reaction-tallies li[data-kind=like]")).ToBeHiddenAsync();
        await Page.ReopenAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Your reaction: Love. Change or remove it" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".reaction-tallies li[data-kind=love] .reaction-count")).ToHaveTextAsync("1");
        await Expect(Page.Locator(".reaction-tallies li[data-kind=like]")).ToBeHiddenAsync();

        // Choosing it again takes it back.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Your reaction: Love. Change or remove it" }).ClickAsync();
        await choices.GetByRole(AriaRole.Button, new() { Name = "Love" }).ClickAsync();
        await Expect(announced).ToHaveTextAsync("Your reaction is removed.");
        await Expect(toggle).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.List, new() { Name = "Reactions" })).ToBeHiddenAsync();
        await Page.ReopenAsync();
        await Expect(toggle).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.List, new() { Name = "Reactions" })).ToBeHiddenAsync();

        var axeClosed = await Page.Locator("ws-reactions").RunAxe(Axe);
        await Assert.That(axeOpen.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        await Assert.That(axeClosed.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();
    }

    [Test]
    public async Task The_bar_shows_the_total_and_opens_each_reactions_count()
    {
        // T-0162: React carries the total; the reactions given, stacked on the right, open their counts.
        await Page.OpenAsync(site.At("/blog/plain-text-files-and-the-jars-at-pompeii"));
        var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "React", Exact = true });
        await toggle.ClickAsync();
        await Page.GetByRole(AriaRole.Group, new() { Name = "Choose a reaction" }).GetByRole(AriaRole.Button, new() { Name = "Thanks" }).ClickAsync();
        await Expect(Page.Locator(".reactions-announce")).ToHaveTextAsync("You reacted: Thanks.");

        var mine = Page.GetByRole(AriaRole.Button, new() { Name = "Your reaction: Thanks. Change or remove it (1 reaction)" });
        await Expect(mine).ToBeVisibleAsync();
        await Expect(mine.Locator(".reaction-toggle-count")).ToHaveTextAsync("1");

        var summary = Page.GetByRole(AriaRole.Button, new() { Name = "Show the count of each reaction" });
        var counts = Page.GetByRole(AriaRole.List, new() { Name = "Reactions" });
        await Expect(summary).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(counts).ToBeHiddenAsync();
        await summary.ClickAsync();
        await Expect(counts).ToBeVisibleAsync();

        // Open, the counts are in the same box, and the button closes them (T-0165).
        var hide = Page.GetByRole(AriaRole.Button, new() { Name = "Hide the counts" });
        await Expect(hide).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(Page.Locator(".reaction-summary-box .reaction-tallies")).ToBeVisibleAsync();
        await Expect(counts.Locator("li:visible")).ToHaveCountAsync(1);
        await Expect(counts.Locator("li[data-kind=thanks] .reaction-count")).ToHaveTextAsync("1");
        await hide.ClickAsync();
        await Expect(counts).ToBeHiddenAsync();
        await Expect(summary).ToHaveAttributeAsync("aria-expanded", "false");

        // The comment count beside it goes to the comment box.
        await Page.Locator("a[data-comment-jump]").ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Form, new() { Name = "Leave a comment" }).GetByLabel("Comment")).ToBeFocusedAsync();

        var axe = await Page.Locator(".post-actions").RunAxe(Axe);
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
    }

    [Test]
    public async Task The_choices_work_from_the_keyboard()
    {
        await Page.OpenAsync(site.At("/blog/why-every-repo-here-has-a-justfile"));
        var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "React", Exact = true });
        var choices = Page.GetByRole(AriaRole.Group, new() { Name = "Choose a reaction" });

        await toggle.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(choices.GetByRole(AriaRole.Button, new() { Name = "Like" })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(choices.GetByRole(AriaRole.Button, new() { Name = "Love" })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("End");
        await Expect(choices.GetByRole(AriaRole.Button, new() { Name = "Funny" })).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(choices.GetByRole(AriaRole.Button, new() { Name = "Like" })).ToBeFocusedAsync();

        // Escape closes the choices and gives the focus back to the button.
        await Page.Keyboard.PressAsync("Escape");
        await Expect(choices).ToBeHiddenAsync();
        await Expect(toggle).ToBeFocusedAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");

        // A click elsewhere closes them too.
        await toggle.ClickAsync();
        await Expect(choices).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Heading, new() { Level = 1 }).ClickAsync();
        await Expect(choices).ToBeHiddenAsync();
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

        var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "React", Exact = true });
        await toggle.ClickAsync();
        await Page.GetByRole(AriaRole.Group, new() { Name = "Choose a reaction" }).GetByRole(AriaRole.Button, new() { Name = "Thanks" }).ClickAsync();

        await Expect(Page.Locator(".reactions-status")).ToHaveTextAsync("That's a lot of reactions at once. Please try again later.");
        await Expect(toggle).ToBeVisibleAsync();
        await Expect(Page.Locator(".reaction-tallies li[data-kind=thanks]")).ToBeHiddenAsync();
    }

    [Test]
    public async Task Without_javascript_there_are_no_reactions()
    {
        await using var context = await Browser.NewContextAsync(new() { JavaScriptEnabled = false });
        var page = await context.NewPageAsync();

        await page.OpenAsync(site.At("/blog/walking-the-south-coast-of-crete"));

        await Expect(page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeVisibleAsync();
        await Expect(page.Locator("ws-reactions")).ToBeHiddenAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "React", Exact = true })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Screenshots_of_given_reactions()
    {
        var output = Weesite.Tests.RepoPaths.Artifacts("screens");
        Directory.CreateDirectory(output);
        var first = true;
        const string Yours = "Your reaction: Insightful. Change or remove it";
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
                    await page.GetByRole(AriaRole.Button, new() { Name = "React", Exact = true }).ClickAsync();
                    await page.GetByRole(AriaRole.Group, new() { Name = "Choose a reaction" }).GetByRole(AriaRole.Button, new() { Name = "Insightful" }).ClickAsync();

                    // Shown at once; wait for the server's answer before the page closes.
                    await Expect(page.Locator(".reactions-announce")).ToHaveTextAsync("You reacted: Insightful.");
                    first = false;
                }
                else
                {
                    await page.EvaluateAsync("() => localStorage.setItem('weesite:reactions', JSON.stringify({ 'posts/a-weekend-with-haskell-type-classes.md': ['insightful'] }))");
                    await page.ReloadAsync();
                    await Expect(page.GetByRole(AriaRole.Button, new() { Name = Yours })).ToBeVisibleAsync();
                }

                await page.Locator(".post-actions").ScrollIntoViewIfNeededAsync();
                var file = Path.Combine(output, $"reactions-{width}-{label}.png");
                await page.ScreenshotAsync(new() { Path = file, FullPage = true, Clip = await ClipAsync(page, page.Locator(".post-actions"), width) });
                await Assert.That(File.Exists(file)).IsTrue();

                // And with the choices open.
                await page.GetByRole(AriaRole.Button, new() { Name = Yours }).ClickAsync();
                await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Choose a reaction" })).ToBeVisibleAsync();
                var open = Path.Combine(output, $"reactions-open-{width}-{label}.png");
                await page.ScreenshotAsync(new() { Path = open, FullPage = true, Clip = await ClipAsync(page, page.Locator(".post-actions"), width) });
                await Assert.That(File.Exists(open)).IsTrue();
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
    private readonly SiteFactory _factory = new(new Dictionary<string, string> { ["Visitor:ProofMaxNumber"] = SiteFactory.EasyProof });

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
