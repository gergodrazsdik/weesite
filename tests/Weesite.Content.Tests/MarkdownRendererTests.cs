using Weesite.Content.Markdown;

namespace Weesite.Content.Tests;

internal sealed class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new(new CodeHighlighter());

    [Test]
    [Arguments("[the drawing](/media/0e3d1bf78702-diagram.svg)", true)]
    [Arguments("[the slides](/media/abcdef012345-talk.pdf)", true)]
    [Arguments("[the photo](/media/abcdef012345-photo.JPG)", false)]
    [Arguments("[elsewhere](https://example.com/file.zip)", false)]
    [Arguments("[a page](/about)", false)]
    public async Task Links_to_stored_files_that_download_get_the_file_class(string markdown, bool file)
    {
        var html = Renderer.Render(markdown).Html;

        await Assert.That(html.Contains("class=\"file-link\"", StringComparison.Ordinal)).IsEqualTo(file);
        await Assert.That(html.Contains("target=\"_blank\"", StringComparison.Ordinal)).IsEqualTo(file);
    }

    [Test]
    public async Task A_file_link_opens_in_a_new_tab_and_says_so()
    {
        // T-0113.
        var html = Renderer.Render("Get [the *slides*](/media/abcdef012345-talk.pdf \"Talk slides\").").Html;

        await Assert.That(html).Contains("<a href=\"/media/abcdef012345-talk.pdf\" class=\"file-link\" target=\"_blank\" rel=\"noopener\" title=\"Talk slides\">the <em>slides</em><span class=\"visually-hidden\"> (download, opens in a new tab)</span></a>");
    }

    [Test]
    public async Task A_task_list_is_marked_for_the_stylesheet()
    {
        // site.css drops the bullet beside the checkbox for this class (F-0091 L18).
        var html = Renderer.Render("- [x] done\n- [ ] to do\n").Html;

        await Assert.That(html).Contains("<ul class=\"contains-task-list\">");
        await Assert.That(html).Contains("type=\"checkbox\"");
    }

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
    public async Task An_image_with_a_title_becomes_a_captioned_figure()
    {
        var captioned = Renderer.Render("![A lighthouse on a rock](/x.jpg \"The lighthouse at dawn\")").Html;
        var inline = Renderer.Render("Text ![A lighthouse](/x.jpg \"Title\") and more.").Html;
        var plain = Renderer.Render("![A lighthouse](/x.jpg)").Html;

        await Assert.That(captioned).StartsWith("<figure><img src=\"/x.jpg\" alt=\"A lighthouse on a rock\"");
        await Assert.That(captioned).Contains("<figcaption>The lighthouse at dawn</figcaption></figure>");
        await Assert.That(inline).DoesNotContain("<figure>");
        await Assert.That(plain).DoesNotContain("<figure>");
        await Assert.That(plain).StartsWith("<p><img");
    }

    [Test]
    public async Task Code_without_a_language_is_labelled_text()
    {
        var html = Renderer.Render("    indented code").Html;

        await Assert.That(html).Contains("<div class=\"bar\"><span>text</span></div>");
    }

    [Test]
    [Arguments("```js filename=\"index.js\"\nconsole.log(1)\n```")]
    [Arguments("```js title=\"index.js\"\nconsole.log(1)\n```")]
    public async Task A_fence_can_name_its_file(string markdown)
    {
        var html = Renderer.Render(markdown).Html;

        // F-0135: the bar names the file, then the language; the language still picks the grammar.
        await Assert.That(html).Contains("<div class=\"bar\"><span><span class=\"file\">index.js</span> js</span></div>");
        await Assert.That(html).Contains("data-language=\"js\"");
    }

    [Test]
    public async Task A_file_name_is_encoded()
    {
        var html = Renderer.Render("```text filename=\"<b>x</b>.txt\"\nx\n```").Html;

        await Assert.That(html).Contains("<span class=\"file\">&lt;b&gt;x&lt;/b&gt;.txt</span>");
    }

    [Test]
    public async Task Fenced_code_is_highlighted_on_the_server()
    {
        var html = Renderer.Render("```csharp\nvar s = \"hi\"; // greet\n```").Html;

        await Assert.That(html).Contains("<div class=\"code-block\"><div class=\"bar\"><span>csharp</span></div><pre class=\"code\" tabindex=\"0\" data-language=\"csharp\"><code>");
        await Assert.That(html).Contains("<span class=\"hl-keyword\">var</span>");
        await Assert.That(html).Contains("<span class=\"hl-string\">&quot;</span>");
        await Assert.That(html).Contains("<span class=\"hl-comment\">");
    }

    [Test]
    [Arguments("rego", "allow if {\n  input.user == \"admin\"\n}", "hl-keyword\"> if<")]
    [Arguments("haskell", "main = putStrLn \"hi\" -- greet", "hl-comment")]
    [Arguments("hs", "main = putStrLn \"hi\"", "hl-string")]
    [Arguments("hcl", "a = \"b\" # c", "hl-comment")]
    [Arguments("terraform", "resource \"azurerm_x\" \"y\" {\n  name = var.n\n}", "hl-type\">resource<")]
    [Arguments("tf", "variable \"n\" {}", "hl-type\">variable<")]
    [Arguments("tofu", "locals { a = 1 }", "hl-number")]
    [Arguments("Terraform", "locals { a = 1 }", "hl-number")]
    [Arguments("just", "# build it\nbuild:\n  cargo build", "hl-comment")]
    [Arguments("justfile", "x := \"y\"", "hl-string")]
    [Arguments("proto", "message A {\n  string b = 1; // c\n}", "hl-comment")]
    [Arguments("protobuf", "syntax = \"proto3\";", "hl-string")]
    [Arguments("proto3", "message A {\n  int32 b = 1;\n}", "hl-number")]
    [Arguments("bicep", "param n string // c", "hl-comment")]
    [Arguments("bicep", "var a = 'b'", "hl-string")]
    [Arguments("toml", "[x]\na = \"b\" # c", "hl-comment")]
    [Arguments("toml", "a = 1", "hl-number")]
    public async Task Added_grammars_highlight(string language, string code, string expected)
    {
        var html = Renderer.Render($"```{language}\n{code}\n```").Html;

        await Assert.That(html).Contains(expected);
    }

    [Test]
    [Arguments("```hcl main.tf\na = 1 # c\n```")]
    [Arguments("~~~hcl\na = 1 # c\n~~~")]
    [Arguments("````HCL\na = 1 # c\n````")]
    public async Task The_fence_language_is_the_first_word_of_the_info_string(string markdown)
    {
        var html = Renderer.Render(markdown).Html;

        await Assert.That(html).Contains("hl-comment");
    }

    [Test]
    public async Task Code_in_an_unknown_language_is_escaped_plain_text()
    {
        var html = Renderer.Render("```cobol\nIF X < 3\n```").Html;

        await Assert.That(html).Contains("IF X &lt; 3");
        await Assert.That(html).DoesNotContain("hl-");
    }

    [Test]
    public async Task Fences_in_languages_without_a_grammar_are_listed_once()
    {
        var markdown = "```cobol\nA\n```\n\n```CS\nB\n```\n\n```text\nC\n```\n\n```\nD\n```\n\n~~~Cobol extra\nE\n~~~\n\n```nix\nF\n```\n\n    indented";

        var unknown = Renderer.UnknownCodeLanguages(markdown);

        await Assert.That(unknown).IsEquivalentTo(["cobol", "nix"]);
    }

    [Test]
    public async Task The_language_list_names_each_grammar_with_its_fence_names()
    {
        var languages = new CodeHighlighter().Languages;

        await Assert.That(languages.Select(l => l.Name)).IsOrderedBy(n => n, StringComparer.OrdinalIgnoreCase);
        await Assert.That(languages.Single(l => l.Name == "C#").FenceNames).IsEquivalentTo(["csharp", "c#", "cs"]);
        await Assert.That(languages.Single(l => l.FenceNames.Contains("hcl")).FenceNames).IsEquivalentTo(["hcl"]);
        await Assert.That(languages.SelectMany(l => l.FenceNames)).DoesNotContain(n => n.Contains(' ', StringComparison.Ordinal));
        await Assert.That(languages.SelectMany(l => l.FenceNames)).HasDistinctItems();
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
