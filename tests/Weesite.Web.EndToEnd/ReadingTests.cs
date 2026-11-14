using System.Diagnostics.CodeAnalysis;
using System.Text;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using TUnit.Core.Interfaces;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// Reading a long post (F-0131 to F-0134): links to its sections, its images at full size, its
/// contents at the top, and the way back up. The sample posts are short, so this site adds one.
/// </summary>
[ClassDataSource<LongPostSite>(Shared = SharedType.PerClass)]
internal sealed class ReadingTests(LongPostSite site) : PageTest
{
    private string Post => new Uri(site.BaseAddress, "/blog/" + LongPostSite.Slug).ToString();

    [Test]
    public async Task Each_section_heading_links_to_itself()
    {
        await Page.SetViewportSizeAsync(1280, 900);
        await Page.OpenAsync(Post);

        var link = Page.GetByRole(AriaRole.Link, new() { Name = "Link to this section: Second part" });
        await Expect(link).ToHaveAttributeAsync("href", "#second-part");
        await Page.GetByRole(AriaRole.Heading, new() { Name = "Second part" }).HoverAsync();
        await Expect(link).ToHaveCSSAsync("opacity", "1");

        await link.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("#second-part$"));
    }

    [Test]
    public async Task A_large_image_opens_at_full_size_and_closes_again()
    {
        await Page.SetViewportSizeAsync(1280, 900);
        await Page.OpenAsync(Post);

        var open = Page.GetByRole(AriaRole.Button, new() { Name = "Show at full size: A blue gradient" });
        await open.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "A blue gradient" });
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Img)).ToHaveAttributeAsync("src", "/media/c0f10a37852f-gradient.jpg");
        var box = (await dialog.GetByRole(AriaRole.Img).BoundingBoxAsync())!;
        await Assert.That(box.Height).IsLessThanOrEqualTo(900);

        await Page.Keyboard.PressAsync("Escape");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(open).ToBeFocusedAsync();

        await open.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
    }

    [Test]
    public async Task On_a_wide_screen_the_contents_at_the_top_open_and_close()
    {
        await Page.SetViewportSizeAsync(1280, 900);
        await Page.OpenAsync(Post);

        var top = Page.Locator("details.toc-top");
        var summary = top.Locator("summary");
        await Expect(summary).ToHaveTextAsync("On this page");
        await Expect(top.GetByRole(AriaRole.Link, new() { Name = "Third part" })).ToBeHiddenAsync();
        await Expect(Page.Locator(".toc-top.narrow")).ToBeHiddenAsync();

        await summary.ClickAsync();
        await top.GetByRole(AriaRole.Link, new() { Name = "Third part" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("#third-part$"));
        await Expect(top).Not.ToHaveAttributeAsync("open", "");

        await summary.ClickAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(top).Not.ToHaveAttributeAsync("open", "");
        await Expect(summary).ToBeFocusedAsync();
    }

    [Test]
    public async Task On_a_phone_the_contents_are_a_list_at_the_top_and_the_end_leads_back_up()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await Page.OpenAsync(Post);

        var list = Page.Locator(".toc-top.narrow");
        await Expect(list.GetByRole(AriaRole.Link, new() { Name = "First part" })).ToBeVisibleAsync();
        await Expect(Page.Locator("details.toc-top")).ToBeHiddenAsync();
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "On this page" })).ToBeHiddenAsync();

        var back = Page.GetByRole(AriaRole.Link, new() { Name = "Back to the top" });
        await back.ScrollIntoViewIfNeededAsync();
        await back.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("#main$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToBeInViewportAsync();
    }

    [Test]
    [Arguments(1280)]
    [Arguments(390)]
    public async Task The_long_post_passes_axe(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        await Page.OpenAsync(Post);
        await Expect(Page.Locator(".heading-link").First).ToBeAttachedAsync();

        var results = await Page.RunAxe();

        await Assert.That(results.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        var screens = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens");
        await Page.ScreenshotAsync(new() { Path = Path.Combine(screens, $"post-long-{width}-light.png") });
    }

    [Test]
    public async Task A_short_post_has_no_way_back_up()
    {
        await Page.OpenAsync(new Uri(site.BaseAddress, "/blog/hello-world").ToString());

        await Expect(Page.Locator("article.post h1")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Back to the top" })).ToHaveCountAsync(0);
    }
}

/// <summary>The sample site with one long post, for this class only.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by TUnit's ClassDataSource.")]
internal sealed class LongPostSite : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The long post's slug.</summary>
    public const string Slug = "a-long-read";

    private readonly SiteFactory _factory = new();

    /// <summary>Gets the address the app listens on.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat("These words fill the post so it takes a few minutes to read.", 12));
        var body = new StringBuilder();
        foreach (var heading in (string[])["First part", "Second part", "Third part"])
        {
            body.Append("## ").AppendLine(heading).AppendLine();
            for (var i = 0; i < 3; i++)
            {
                body.AppendLine(paragraph).AppendLine();
            }

            if (heading == "Second part")
            {
                body.AppendLine("![A blue gradient](/media/c0f10a37852f-gradient.jpg)").AppendLine();
            }
        }

        var post = $"""
            ---
            title: "A long read"
            slug: {Slug}
            date: 2026-03-06
            author: example-author
            ---

            {body}
            """;
        await File.WriteAllTextAsync(Path.Combine(_factory.ContentPath, "content", "posts", Slug + ".md"), post);
        BaseAddress = await _factory.StartAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}
