using Weesite.Content.Markdown;

namespace Weesite.Content.Tests;

internal sealed class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new(new CodeHighlighter());

    [Test]
    public async Task Raw_html_is_shown_as_text()
    {
        var html = Renderer.Render("Hello <script>alert(1)</script> <b onclick=\"x()\">bold</b>").Html;

        await Assert.That(html).DoesNotContain("<script>");
        await Assert.That(html).DoesNotContain("<b ");
        await Assert.That(html).Contains("&lt;script&gt;");
    }

    [Test]
    [Arguments("[x](javascript:alert(1))")]
    [Arguments("[x](JavaScript:alert(1))")]
    [Arguments("[x](data:text/html;base64,PHNjcmlwdD4=)")]
    [Arguments("[x](vbscript:msgbox)")]
    [Arguments("![x](javascript:alert(1))")]
    public async Task Unsafe_link_targets_are_removed(string markdown)
    {
        var html = Renderer.Render(markdown).Html;

        await Assert.That(html.Contains("script:", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(html).DoesNotContain("data:");
    }

    [Test]
    [Arguments("[x](https://example.com/a)", "https://example.com/a")]
    [Arguments("[x](mailto:author@example.com)", "mailto:author@example.com")]
    [Arguments("[x](/blog/hello-world)", "/blog/hello-world")]
    [Arguments("[x](#section)", "#section")]
    [Arguments("[x](notes)", "notes")]
    public async Task Safe_link_targets_are_kept(string markdown, string href)
    {
        var html = Renderer.Render(markdown).Html;

        await Assert.That(html).Contains($"href=\"{href}\"");
    }

    [Test]
    public async Task Headings_shift_down_and_get_ids()
    {
        var html = Renderer.Render("# First\n\n## Second").Html;

        await Assert.That(html).Contains("<h2 id=\"first\">First</h2>");
        await Assert.That(html).Contains("<h3 id=\"second\">Second</h3>");
        await Assert.That(html).DoesNotContain("<h1");
    }

    [Test]
    public async Task Sections_written_with_two_hashes_stay_h2()
    {
        var html = Renderer.Render("## First\n\n### Second").Html;

        await Assert.That(html).Contains("<h2 id=\"first\">First</h2>");
        await Assert.That(html).Contains("<h3 id=\"second\">Second</h3>");
    }

    [Test]
    public async Task Fenced_code_is_highlighted_on_the_server()
    {
        var html = Renderer.Render("```csharp\nvar s = \"hi\"; // greet\n```").Html;

        await Assert.That(html).Contains("<pre class=\"code\" data-language=\"csharp\"><code>");
        await Assert.That(html).Contains("<span class=\"hl-keyword\">var</span>");
        await Assert.That(html).Contains("<span class=\"hl-string\">&quot;</span>");
        await Assert.That(html).Contains("<span class=\"hl-comment\">");
    }

    [Test]
    public async Task Code_in_an_unknown_language_is_escaped_plain_text()
    {
        var html = Renderer.Render("```rego\nallow if input.x < 3\n```").Html;

        await Assert.That(html).Contains("allow if input.x &lt; 3");
        await Assert.That(html).DoesNotContain("hl-");
    }

    [Test]
    public async Task Tables_footnotes_and_task_lists_render()
    {
        var html = Renderer.Render("| a | b |\n|---|---|\n| 1 | 2 |\n\nText[^1]\n\n[^1]: Note\n\n- [x] done").Html;

        await Assert.That(html).Contains("<table>");
        await Assert.That(html).Contains("class=\"footnotes\"");
        await Assert.That(html).Contains("type=\"checkbox\"");
    }

    [Test]
    public async Task Plain_text_has_no_markup()
    {
        var text = Renderer.Render("Some **bold** and a [link](https://example.com).").PlainText;

        await Assert.That(text).IsEqualTo("Some bold and a link.");
    }
}
