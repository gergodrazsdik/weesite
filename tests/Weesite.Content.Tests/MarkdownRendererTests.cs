using Weesite.Content.Markdown;
using Weesite.Content.Media;

namespace Weesite.Content.Tests;

internal sealed class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new(new CodeHighlighter());

    [Test]
    [Arguments("quotes")]
    [Arguments("lists")]
    [Arguments("emphasis")]
    public async Task Text_nested_too_deeply_shows_as_plain_text_with_a_problem(string kind)
    {
        // T-0177: the Markdown parser refuses text nested past its depth limit with an exception,
        // which a save or a preview answered with an error page.
        var deep = kind switch
        {
            "quotes" => string.Concat(Enumerable.Repeat("> ", 2000)) + "deep",
            "lists" => string.Join('\n', Enumerable.Range(0, 300).Select(i => new string(' ', i * 2) + "- item")) + " deep",
            _ => string.Concat(Enumerable.Repeat("*a ", 5000)) + "deep" + string.Concat(Enumerable.Repeat(" a*", 5000)),
        };
        var renderer = new MarkdownRenderer(new CodeHighlighter());

        var rendered = renderer.Render(deep);
        var languages = renderer.UnknownCodeLanguages(deep);
        var words = MarkdownRenderer.WordsIn(deep);

        // Within what a save accepts, so a save or a preview could send it.
        await Assert.That(deep.Length).IsLessThanOrEqualTo(Weesite.Content.ContentDraft.MaxBodyLength);
        await Assert.That(rendered.Html).Contains("deep");
        await Assert.That(rendered.Problems.Single()).Contains("nested too deeply");
        await Assert.That(languages).IsEmpty();
        await Assert.That(words).IsGreaterThan(0);
    }

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
    [Arguments("[x](javascript:alert(1))")] // an inline link
    [Arguments("[x](JavaScript:alert(1))")] // mixed case
    [Arguments("[x](data:text/html;base64,PHNjcmlwdD4=)")] // data:
    [Arguments("[x](vbscript:msgbox)")] // vbscript:
    [Arguments("![x](javascript:alert(1))")] // an image
    [Arguments("[x][r]\n\n[r]: javascript:alert(1)")] // a reference link
    [Arguments("![x][r]\n\n[r]: javascript:alert(1)")] // a reference image
    [Arguments("[x](javascript&colon;alert(1))")] // an entity-encoded colon
    [Arguments("[x](&#106;avascript:alert(1))")] // a decimal entity
    [Arguments("[x](&#x6A;avascript:alert(1))")] // a hex entity
    [Arguments("[x]( javascript:alert(1) )")] // spaces around it
    [Arguments("[x](<javascript:alert(1)>)")] // an address in angle brackets
    [Arguments("<javascript:alert(1)>")] // an autolink
    [Arguments("<ftp://example.com/a>")] // an autolink with another scheme
    [Arguments("See ftp://example.com/a today.")] // a bare autolink
    [Arguments("| a |\n|---|\n| [x](javascript:alert(1)) |")] // a table cell
    [Arguments("Text.[^1]\n\n[^1]: [x](javascript:alert(1))")] // a footnote
    [Arguments("^^^\n![a](/media/0123456789ab-x.jpg)\n^^^ [x](javascript:alert(1))")] // a figure caption
    [Arguments("- [x](javascript:alert(1))\n  - [y](javascript:alert(1))")] // a nested list
    [Arguments("> [x](javascript:alert(1))")] // a quote
    [Arguments("Term\n: [x](javascript:alert(1))")] // a definition list
    [Arguments("*[x](javascript:alert(1))*")] // emphasis
    [Arguments("[**x**](javascript:alert(1))")] // link text with emphasis
    public async Task Unsafe_link_targets_are_refused_by_every_route_into_the_html(string markdown)
    {
        // F-0252: reference links, entities, autolinks and links inside tables, footnotes and
        // captions reach the check by other routes than a plain inline link.
        var html = Renderer.Render(markdown).Html;

        await Assert.That(Targets(html).Where(t => !IsSafeTarget(t))).IsEmpty();
        await Assert.That(html.Contains("<a href=\"javascript", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }

    [Test]
    public async Task The_link_check_sees_safe_links_in_the_same_places()
    {
        // The test above would pass if those places rendered no links at all.
        var html = Renderer.Render("| a |\n|---|\n| [t](https://example.com/t) |\n\nText.[^1]\n\n[^1]: [f](https://example.com/f)\n\n[r][ref]\n\n[ref]: https://example.com/r\n").Html;

        await Assert.That(Targets(html)).Contains("https://example.com/t");
        await Assert.That(Targets(html)).Contains("https://example.com/f");
        await Assert.That(Targets(html)).Contains("https://example.com/r");
    }

    [Test]
    [Arguments("[the archive](ftp://example.com/a)", "<p>the archive</p>")]
    [Arguments("[click](javascript:alert(1))", "<p>click</p>")]
    [Arguments("![a diagram](javascript:alert(1))", "<p>a diagram</p>")]
    [Arguments("<ftp://example.com/a>", "<p>ftp://example.com/a</p>")]
    public async Task A_dropped_address_shows_as_text_and_says_why(string markdown, string html)
    {
        // F-0145: not a link to the page it's on (href=""), and preview_item says so.
        var rendered = Renderer.Render(markdown);

        await Assert.That(rendered.Html.Trim()).IsEqualTo(html);
        await Assert.That(rendered.Problems.Single()).Contains("shows as text");
    }

    [Test]
    [Arguments("![a backup](/admin/backup/file)")]
    [Arguments("![a backup](../admin/backup/file)")]
    [Arguments("![a page](about)")]
    [Arguments("![a backup](https://example.com/admin/audit/export.csv)")]
    [Arguments("![a backup](//example.com/admin/backup/file)")]
    [Arguments("![a backup](HTTPS:/\\example.com/admin/x)")]
    [Arguments("![a backup](/media/../admin/x)")]
    [Arguments("![a backup](https://example.com/media/%2e%2e/admin/x)")]
    [Arguments("![a backup](/media/)")]
    public async Task On_the_site_images_come_only_from_media(string markdown)
    {
        // T-0122: any other address would have each reader's browser request that page, signed
        // in as they are.
        var rendered = Renderer.Render(markdown);

        await Assert.That(rendered.Html).DoesNotContain("<img");
        await Assert.That(rendered.Problems.Single()).Contains("images on the site come from Media");
    }

    [Test]
    public async Task Images_and_files_that_wont_show_are_problems()
    {
        var media = new Weesite.Content.Media.MediaIndex(new Dictionary<string, Weesite.Content.Media.ImageInfo> { ["0123456789ab-bell.png"] = new(400, 400, []) });

        var problems = Renderer.Render("![A bell](/media/0123456789ab-bell.png) ![Gone](/media/abcdef012345-gone.png) [slides](/media/abcdef012345-slides.pdf) ![Elsewhere](https://example.com/x.png)", media).Problems;

        await Assert.That(problems).IsEquivalentTo([
            "image /media/abcdef012345-gone.png isn't in Media (line 1)",
            "file /media/abcdef012345-slides.pdf isn't in Media (line 1)",
            "image https://example.com/x.png shows as its alt text: images on the site come from Media (line 1)",
        ]);
    }

    [Test]
    public async Task Problems_name_their_line_in_the_file()
    {
        // F-0146: the same warning for five images said nothing about which one.
        var problems = Renderer.Render("Text.\n\n![](/media/abc123-a.jpg)\n\nMore.\n\n![](/media/abc123-a.jpg)", firstLine: 7).Problems;

        await Assert.That(problems).IsEquivalentTo(["image /media/abc123-a.jpg has no alt text (line 9)", "image /media/abc123-a.jpg has no alt text (line 13)"]);
    }

    [Test]
    [Arguments("[x](other-post)")]
    [Arguments("[x](www.example.com)")]
    [Arguments("[x](../notes)")]
    public async Task A_relative_link_is_kept_and_reported(string markdown)
    {
        // F-0146: it resolves under the item's own address, and differently in feeds.
        var rendered = Renderer.Render(markdown);

        await Assert.That(rendered.Html).Contains("<a href=");
        await Assert.That(rendered.Problems.Single()).Contains("is relative: start a path on this site with /");
    }

    [Test]
    [Arguments("[x](/blog/a-post)")]
    [Arguments("[x](#a-heading)")]
    [Arguments("[x](https://example.com)")]
    [Arguments("[x](mailto:a@example.com)")]
    public async Task Site_paths_and_absolute_links_are_not_reported(string markdown)
    {
        await Assert.That(Renderer.Render(markdown).Problems).IsEmpty();
    }

    [Test]
    public async Task A_fence_with_only_a_file_name_has_no_language()
    {
        var html = Renderer.Render("```filename=\"plain.txt\"\nplain\n```").Html;

        await Assert.That(html).Contains("<div class=\"bar\"><span><span class=\"file\">plain.txt</span> text</span></div>");
        await Assert.That(html).DoesNotContain("data-language");
        await Assert.That(Renderer.UnknownCodeLanguages("```filename=\"plain.txt\"\nplain\n```")).IsEmpty();
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
        var captioned = Renderer.Render("![A lighthouse on a rock](/media/0123456789ab-x.jpg \"The lighthouse at dawn\")").Html;
        var inline = Renderer.Render("Text ![A lighthouse](/media/0123456789ab-x.jpg \"Title\") and more.").Html;
        var plain = Renderer.Render("![A lighthouse](/media/0123456789ab-x.jpg)").Html;

        await Assert.That(captioned).StartsWith("<figure><img src=\"/media/0123456789ab-x.jpg\" alt=\"A lighthouse on a rock\"");
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
    public async Task An_image_can_sit_beside_the_text()
    {
        // F-0142: placement and size classes in braces, on stored images and others alike.
        var media = new Weesite.Content.Media.MediaIndex(new Dictionary<string, Weesite.Content.Media.ImageInfo> { ["0123456789ab-bell.png"] = new(400, 400, []) });

        var stored = Renderer.Render("![A bell](/media/0123456789ab-bell.png){.left .small}\n\nText.", media).Html;
        // Another site's address outside its /media/ is dropped (T-0177); one in it keeps its classes.
        var other = Renderer.Render("![A bell](https://example.com/media/0123456789ab-bell.png){.right .medium}").Html;

        await Assert.That(stored).Contains("<img src=\"/media/0123456789ab-bell.png\" class=\"left small\"");
        await Assert.That(stored).Contains("<p>Text.</p>");
        await Assert.That(other).Contains("class=\"right medium\"");
    }

    [Test]
    public async Task A_captioned_image_places_its_figure()
    {
        var html = Renderer.Render("![A bell](/media/0123456789ab-bell.png \"The bell\"){.right}").Html;

        await Assert.That(html).Contains("<figure class=\"right\"><img");
        await Assert.That(html).DoesNotContain("<img src=\"/media/0123456789ab-bell.png\" class=");
        await Assert.That(html).Contains("<figcaption>The bell</figcaption></figure>");
    }

    [Test]
    [Arguments("![A bell](/media/0123456789ab-bell.png){.left onclick=\"alert(1)\" style=\"position:fixed\" #main .evil}")]
    [Arguments("[a link](https://example.com){onclick=\"alert(1)\" href=\"javascript:alert(1)\" #main}")]
    [Arguments("A paragraph {onmouseover=\"alert(1)\" #main}")]
    [Arguments("## A heading {#main .evil}")]
    public async Task Other_braces_stay_text_and_set_nothing(string markdown)
    {
        var html = Renderer.Render(markdown).Html;

        // Only the image placement form is read (F-0142); the rest shows as written, encoded.
        await Assert.That(html).Contains("{");
        await Assert.That(html).DoesNotMatch("<[^>]*(\\son[a-z]+=|style=|id=\"main\"|javascript:|class=\"[^\"]*evil)");
    }

    [Test]
    [Arguments("Use the {name} placeholder.", "{name}")]
    [Arguments("An empty object {} here.", "{}")]
    [Arguments("It ends with braces {x}", "{x}")]
    [Arguments("A **bold**{x} word", "{x}")]
    [Arguments("Set {a=1} now", "{a=1}")]
    [Arguments("A sentence.{x}", "{x}")]
    public async Task Braces_in_prose_stay_text(string markdown, string kept)
    {
        var html = Renderer.Render(markdown).Html;

        await Assert.That(html).Contains(kept);
    }

    [Test]
    [Arguments("{.left .large}")]
    [Arguments("{.small .medium}")]
    [Arguments("{.left .right}")]
    [Arguments("{width=300}")]
    [Arguments("{}")]
    public async Task Braces_that_are_not_a_placement_set_nothing_and_are_reported(string braces)
    {
        // F-0146: all or nothing, and preview_item says so.
        var rendered = Renderer.Render("![A bell](/media/0123456789ab-bell.png)" + braces + " Text.");

        await Assert.That(rendered.Html).DoesNotContain("class=");
        await Assert.That(rendered.Html).Contains(braces);
        await Assert.That(rendered.PlainText).Contains(braces);
        await Assert.That(rendered.Problems.Single()).IsEqualTo($"image /media/0123456789ab-bell.png: {braces} after it shows as text: use .left or .right, .small or .medium, at most one of each and nothing else (line 1)");
    }

    [Test]
    public async Task Placement_braces_stay_out_of_the_plain_text()
    {
        var rendered = Renderer.Render("![A bell](/media/0123456789ab-bell.png){.left .small} Text after.");

        await Assert.That(rendered.PlainText).DoesNotContain("{");
        await Assert.That(rendered.Html).Contains("class=\"left small\"");
        await Assert.That(rendered.Html).Contains(" Text after.");
    }

    [Test]
    public async Task A_document_of_many_slow_blocks_renders_quickly()
    {
        // T-0122: each of these lines took TextMateSharp about a second, under a lock every render
        // shared; the native highlighter takes milliseconds, and the budget still bounds the rest.
        // Each block differs (its first line), so none comes from the cache of highlighted blocks.
        var markdown = string.Concat(Enumerable.Range(0, 60).Select(i => $"```js\nb{i}();\n" + string.Concat(Enumerable.Repeat("a(", 490)) + "\n```\n\n"));
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var html = Renderer.Render(markdown).Html;

        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(html).Contains(">b0</span>();");

        // Whether the last blocks fit the budget depends on the machine: they took 5 ms each here,
        // and 35 on a CI runner busy with the other test projects, where the 2 seconds ran out
        // after 57 blocks (F-0265). Highlighted or plain, every block is there.
        foreach (var i in Enumerable.Range(0, 60))
        {
            await Assert.That(html).Contains($"b{i}</span>();").Or.Contains($"<code>b{i}();");
        }
    }

    [Test]
    public async Task Very_long_lines_and_blocks_stay_plain()
    {
        var longLine = Renderer.Render("```csharp\nvar s = 1;\n" + new string('x', CodeHighlighter.MaxLineLength + 1) + "\nvar t = 2;\n```").Html;
        var bigBlock = Renderer.Render("```csharp\n" + string.Concat(Enumerable.Repeat("var s = 1;\n", 2_000)) + "```").Html;

        await Assert.That(longLine).Contains("<span class=\"hl-keyword\">var</span> s");
        await Assert.That(longLine).Contains("\nvar t = 2;");
        await Assert.That(bigBlock).DoesNotContain("<span class=\"hl-");
    }

    [Test]
    public async Task Fenced_code_is_highlighted_on_the_server()
    {
        var html = Renderer.Render("```csharp\nvar s = \"hi\"; // greet\n```").Html;

        await Assert.That(html).Contains("<div class=\"code-block\"><div class=\"bar\"><span>csharp</span></div><pre class=\"code\" tabindex=\"0\" data-language=\"csharp\"><code>");
        await Assert.That(html).Contains("<span class=\"hl-keyword\">var</span>");
        await Assert.That(html).Contains("<span class=\"hl-string\">&quot;hi&quot;</span>");
        await Assert.That(html).Contains("<span class=\"hl-comment\">");
    }

    [Test]
    [Arguments("rego", "allow if {\n  input.user == \"admin\"\n}", "hl-keyword\"> if<")]
    [Arguments("just", "# build it\nbuild:\n  cargo build", "hl-comment")]
    [Arguments("justfile", "x := \"y\"", "hl-string")]
    [Arguments("bicep", "param n string // c", "hl-comment")]
    [Arguments("bicep", "var a = 'b'", "hl-string")]
    [Arguments("jade", "div.card", "hl-tag")]
    [Arguments("vb", "Dim x As Integer = 1 ' note", "hl-comment")]
    [Arguments("haskell", "main = putStrLn \"hi\" -- greet", "hl-comment")]
    [Arguments("hs", "main = putStrLn \"hi\"", "hl-string")]
    [Arguments("hcl", "a = \"b\" # c", "hl-comment")]
    [Arguments("terraform", "resource \"azurerm_x\" \"y\" {\n  name = var.n\n}", "hl-type\">resource<")]
    [Arguments("tf", "variable \"n\" {}", "hl-type\">variable<")]
    [Arguments("terraform", "resource \"azurerm_x\" \"y\" {}", "hl-constant\">&quot;azurerm_x&quot;<")]
    [Arguments("hcl", "block \"label\" {}", "hl-constant\">&quot;label&quot;<")]
    [Arguments("tofu", "locals { a = 1 }", "hl-number")]
    [Arguments("Terraform", "locals { a = 1 }", "hl-number")]
    [Arguments("proto", "message A {\n  string b = 1; // c\n}", "hl-comment")]
    [Arguments("protobuf", "syntax = \"proto3\";", "hl-string")]
    [Arguments("proto3", "message A {\n  int32 b = 1;\n}", "hl-number")]
    [Arguments("toml", "[x]\na = \"b\" # c", "hl-comment")]
    [Arguments("toml", "a = 1", "hl-number")]
    [Arguments("toml", "[package]\nname = \"x\"", "hl-property\">name<")]
    [Arguments("json", "{\"name\": 1}", "hl-property\">&quot;name&quot;<")]
    [Arguments("properties", "app.name=Example", "hl-property\">app.name<")]
    [Arguments("diff", "@@ -1 +1 @@\n-old\n+new", "hl-inserted\">+new<")]
    [Arguments("patch", "@@ -1 +1 @@\n-old\n+new", "hl-deleted\">-old<")]
    [Arguments("razor", "@if (x)\n{\n    <a href=\"/a\">A</a>\n}", "hl-attribute\">href<")]
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
        var markdown = "```cobol\nA\n```\n\n```CS\nB\n```\n\n```text\nC\n```\n\n```\nD\n```\n\n~~~Cobol extra\nE\n~~~\n\n```befunge\nF\n```\n\n    indented";

        var unknown = Renderer.UnknownCodeLanguages(markdown);

        await Assert.That(unknown).IsEquivalentTo(["cobol", "befunge"]);
    }

    [Test]
    public async Task The_language_list_names_each_grammar_with_its_fence_names()
    {
        var languages = new CodeHighlighter().Languages;

        await Assert.That(languages.Select(l => l.Name)).IsOrderedBy(n => n, StringComparer.OrdinalIgnoreCase);
        // The first fence name is the one the editor inserts: Weesite's from before syntect (T-0189).
        var csharp = languages.Single(l => l.Name == "C#").FenceNames;
        await Assert.That(csharp[0]).IsEqualTo("csharp");
        await Assert.That(csharp).Contains("c#").And.Contains("cs");
        await Assert.That(languages.Single(l => l.Name == "Shell Script").FenceNames[0]).IsEqualTo("shellscript");
        await Assert.That(languages.Single(l => l.FenceNames.Contains("hcl")).FenceNames[0]).IsEqualTo("terraform");
        await Assert.That(languages.SelectMany(l => l.FenceNames)).DoesNotContain(n => n.Contains(' ', StringComparison.Ordinal));
        await Assert.That(languages.SelectMany(l => l.FenceNames)).HasDistinctItems();
    }

    [Test]
    public async Task A_table_columns_alignment_is_a_class_not_a_style()
    {
        // F-0257: Markdig writes style="text-align: …", which the CSP's style-src blocks, so the
        // column wasn't aligned and every cell logged a violation (Search Console reported them).
        var html = Renderer.Render("| Name | Left | Middle | Count |\n| --- | :--- | :---: | ---: |\n| a | b | c | 1 |\n| d | e | f | 22 |").Html;

        await Assert.That(html).DoesNotContain("style=");
        await Assert.That(html).Contains("<th>Name</th>").And.Contains("<th class=\"align-left\">Left</th>");
        await Assert.That(html).Contains("<th class=\"align-center\">Middle</th>").And.Contains("<th class=\"align-right\">Count</th>");
        await Assert.That(html).Contains("<td>a</td>").And.Contains("<td class=\"align-left\">b</td>");
        await Assert.That(html).Contains("<td class=\"align-center\">c</td>").And.Contains("<td class=\"align-right\">22</td>");
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

    [Test]
    public async Task The_preview_marks_each_block_with_its_source_line()
    {
        // F-0180: the editor lines its two panes up by these.
        const string Markdown = """
            ## Setup

            A paragraph
            on two lines.

            ![A diagram](/media/0e3d1bf78702-diagram.png "The flow")

            - one
            - two

            ```bash filename="run.sh"
            echo hi
            ```

            | a | b |
            | - | - |
            | 1 | 2 |
            """;

        var html = Renderer.Render(Markdown, sourceLines: true).Html;

        await Assert.That(html).Contains("<h2 id=\"setup\" data-line=\"1\">");
        await Assert.That(html).Contains("<p data-line=\"3\">A paragraph");
        await Assert.That(html).Contains("<figure data-line=\"6\">");
        await Assert.That(html).Contains("<ul data-line=\"8\">");
        await Assert.That(html).Contains("<li data-line=\"9\">two");
        await Assert.That(html).Contains("<div class=\"code-block\" data-line=\"11\">");
        await Assert.That(html).Contains("<table data-line=\"15\">");
        await Assert.That(Renderer.Render(Markdown).Html).DoesNotContain("data-line");
    }

    [Test]
    public async Task Words_are_counted_in_the_plain_text()
    {
        // F-0181: list markers, table pipes and heading marks aren't words; the editor counts so too.
        const string Markdown = "## Setup\n\n- one\n- two\n\n| a | b |\n| - | - |\n| c | d |\n\n![An image](/media/abcdef012345-photo.png) and [a link](https://example.com/a/long/address).";

        await Assert.That(MarkdownRenderer.WordsIn(Markdown)).IsEqualTo(Renderer.Render(Markdown).Words);
        await Assert.That(MarkdownRenderer.WordsIn("## Setup\n\n- one\n- two\n\n| a | b |\n| - | - |\n| c | d |")).IsEqualTo(7);
    }

    [Test]
    public async Task The_same_text_renders_once_and_its_problems_still_name_the_files_lines()
    {
        // T-0177: a render is kept by the text's hash and the renderer's settings.
        var renderer = new MarkdownRenderer(new CodeHighlighter());
        const string Markdown = "Text.\n\n![](/media/0123456789ab-bell.png)";

        var first = renderer.Render(Markdown, firstLine: 1);
        var later = renderer.Render(Markdown, firstLine: 5);
        var preview = renderer.Render(Markdown, sourceLines: true);

        await Assert.That(renderer.Rendered).IsEqualTo(2);
        await Assert.That(later.Html).IsEqualTo(first.Html);
        await Assert.That(first.MissingAlt.Single()).EndsWith("(line 3)");
        await Assert.That(later.MissingAlt.Single()).EndsWith("(line 7)");
        await Assert.That(later.Problems).IsEquivalentTo(later.MissingAlt);
        await Assert.That(preview.Html).Contains("data-line");
        await Assert.That(MarkdownRenderer.WordsIn(Markdown)).IsEqualTo(first.Words);
    }

    [Test]
    public async Task A_kept_render_is_redone_when_an_image_it_uses_changes_in_media()
    {
        var renderer = new MarkdownRenderer(new CodeHighlighter());
        const string Markdown = "![A bell](/media/0123456789ab-bell.png)";
        var without = new MediaIndex(new Dictionary<string, ImageInfo>(), ["abcdef012345-other.pdf"]);
        var otherFile = new MediaIndex(new Dictionary<string, ImageInfo>(), ["abcdef012345-other.pdf", "abcdef012345-more.pdf"]);
        var with = new MediaIndex(new Dictionary<string, ImageInfo> { ["0123456789ab-bell.png"] = new(800, 600, [(480, "0123456789ab-bell-480w.webp")]) });

        var missing = renderer.Render(Markdown, without);
        var unrelated = renderer.Render(Markdown, otherFile);
        var found = renderer.Render(Markdown, with);

        await Assert.That(renderer.Rendered).IsEqualTo(2);
        await Assert.That(ReferenceEquals(missing.Html, unrelated.Html)).IsTrue();
        await Assert.That(missing.Problems.Single()).Contains("isn't in Media");
        await Assert.That(found.Problems).IsEmpty();
        await Assert.That(found.Html).Contains("width=\"800\"");
    }

    [Test]
    public async Task A_code_block_that_ran_out_of_its_time_is_remembered_as_it_came_out()
    {
        // T-0177: such a block would take that time again at every load. Unique code, so no other
        // test has highlighted it before; a budget of a microsecond runs out after a line, and a
        // highlighter whose line limit is one tick counts that as the block's own time.
        var highlighter = new CodeHighlighter(TimeSpan.FromTicks(1));
        var code = $"var b{Guid.NewGuid():N} = 1;\nvar c = 2;\nvar d = 3;";
        var first = new HighlightBudget(TimeSpan.FromMicroseconds(1));
        var again = new HighlightBudget();

        var html = highlighter.Highlight(code, "cs", first);
        var kept = highlighter.Highlight(code, "cs", again);

        await Assert.That(first.Remaining).IsLessThan(TimeSpan.FromMicroseconds(1));
        await Assert.That(html).EndsWith("var d = 3;");
        await Assert.That(again.Remaining).IsEqualTo(HighlightBudget.PerDocument);
        await Assert.That(kept).IsEqualTo(html);
    }

    [Test]
    public async Task A_code_block_cut_short_by_the_rest_of_its_document_gets_its_own_turn_later()
    {
        var highlighter = new CodeHighlighter();
        var code = $"var b{Guid.NewGuid():N} = 1;\nvar c = 2;\nvar d = 3;";
        var nearlySpent = new HighlightBudget(TimeSpan.FromMicroseconds(1));
        var fresh = new HighlightBudget();

        var cut = highlighter.Highlight(code, "cs", nearlySpent);
        var whole = highlighter.Highlight(code, "cs", fresh);

        await Assert.That(nearlySpent.Remaining).IsLessThanOrEqualTo(TimeSpan.Zero);
        await Assert.That(fresh.Remaining).IsLessThan(HighlightBudget.PerDocument);
        await Assert.That(cut).EndsWith("var d = 3;");
        await Assert.That(whole).EndsWith("<span class=\"hl-number\">3</span>;");
    }

    [Test]
    public async Task A_document_whose_budget_is_spent_gets_its_later_blocks_plain()
    {
        // T-0122: the budget is the document's, all blocks together.
        var highlighter = new CodeHighlighter();
        var budget = new HighlightBudget(TimeSpan.FromMicroseconds(1));

        _ = highlighter.Highlight($"var a{Guid.NewGuid():N} = 1;\nvar b = 2;", "cs", budget);
        var later = highlighter.Highlight($"var c{Guid.NewGuid():N} = 3;", "cs", budget);

        await Assert.That(budget.Remaining).IsLessThanOrEqualTo(TimeSpan.Zero);
        await Assert.That(later).DoesNotContain("<span");
    }

    // Every href and src in the HTML, entities decoded, as a browser would read them.
    internal static List<string> Targets(string html) =>
        [.. System.Text.RegularExpressions.Regex.Matches(html, "(?:href|src|srcset)\\s*=\\s*\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim())];

    // A target the site may link: a path, fragment or query, http(s) or mailto, or a relative
    // address without a scheme.
    internal static bool IsSafeTarget(string url) =>
        url.Length == 0 || url[0] is '/' or '#' or '?'
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || !url.Contains(':', StringComparison.Ordinal);
}
