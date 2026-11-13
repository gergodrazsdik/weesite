namespace Weesite.Content.Tests;

internal sealed class ContentParserTests
{
    private const string Post = """
        ---
        title: Hello, world
        slug: hello-world
        date: 2026-01-15
        author: example-author
        tags: [welcome, weesite]
        summary: The first post.
        redirect_from: [hello]
        ---

        Body text.
        """;

    [Test]
    public async Task Parses_a_post_with_front_matter()
    {
        var item = ContentParser.Parse("posts/hello-world.md", Post).Item;

        await Assert.That(item).IsNotNull();
        await Assert.That(item!.Kind).IsEqualTo(ContentKind.Post);
        await Assert.That(item.Slug).IsEqualTo("hello-world");
        await Assert.That(item.Title).IsEqualTo("Hello, world");
        await Assert.That(item.Date).IsEqualTo(new DateOnly(2026, 1, 15));
        await Assert.That(item.Tags).IsEquivalentTo(["welcome", "weesite"]);
        await Assert.That(item.PreviousSlugs).IsEquivalentTo(["hello"]);
        await Assert.That(item.Language).IsEqualTo("en");
        await Assert.That(item.Markdown).IsEqualTo("Body text.");
    }

    [Test]
    public async Task Parses_a_page_without_a_date()
    {
        const string PageText = "---\nkind: page\ntitle: About\nslug: about\nmenu: true\n---\nText\n";

        var item = ContentParser.Parse("pages/about.md", PageText).Item;

        await Assert.That(item!.Kind).IsEqualTo(ContentKind.Page);
        await Assert.That(item.ShowInMenu).IsTrue();
        await Assert.That(item.Date).IsNull();
    }

    [Test]
    [Arguments("no front matter", "Just text", "The file doesn't start with a front matter block")]
    [Arguments("unclosed", "---\ntitle: x\n", "isn't closed")]
    [Arguments("bad yaml", "---\ntitle: [unclosed\n---\n", "isn't valid YAML")]
    [Arguments("no title", "---\nslug: x\ndate: 2026-01-01\n---\n", "The title is missing.")]
    [Arguments("bad slug", "---\ntitle: X\nslug: Not_Valid\ndate: 2026-01-01\n---\n", "isn't lowercase")]
    [Arguments("post without date", "---\ntitle: X\nslug: x\n---\n", "A post needs a date.")]
    [Arguments("bad kind", "---\nkind: note\ntitle: X\nslug: x\n---\n", "must be post or page")]
    [Arguments("bad tag", "---\ntitle: X\nslug: x\ndate: 2026-01-01\ntags: [Bad Tag]\n---\n", "The tag 'Bad Tag'")]
    [Arguments("empty tag", "---\ntitle: X\nslug: x\ndate: 2026-01-01\ntags: [a, ~]\n---\n", "has an empty entry")]
    [Arguments("empty redirect", "---\ntitle: X\nslug: x\ndate: 2026-01-01\nredirect_from:\n  -\n---\n", "has an empty entry")]
    public async Task Reports_invalid_files_without_throwing(string name, string text, string expected)
    {
        var result = ContentParser.Parse($"{name}.md", text);

        await Assert.That(result.Item).IsNull();
        await Assert.That(result.Error).Contains(expected);
    }
}
