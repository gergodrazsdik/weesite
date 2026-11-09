using Weesite.Content.Markdown;

namespace Weesite.Content.Tests;

/// <summary>The HTML export (brief, HTML export; T-0050): portable HTML and what had to change.</summary>
internal sealed class HtmlExporterTests
{
    private static readonly Uri Canonical = new("https://example.com/blog/a-post");

    [Test]
    public async Task Supported_markdown_keeps_its_meaning_with_absolute_addresses()
    {
        var markdown = """
            Intro with **bold**, _italic_, `code` and a [link](/about) and [another](https://other.example/x).

            ## Section

            - one
            - two

            1. first
            2. second

            > A quote.

            ```csharp
            var a = 1 < 2;
            ```

            ---

            ![A blue square](/media/abc-square.png "The square, as a caption")

            [Jump](#section) or mail <me@example.com>.
            """;

        var export = HtmlExporter.Export(markdown, Canonical, footer: false);

        await Assert.That(export.Html).Contains("<p>Intro with <strong>bold</strong>, <em>italic</em>, <code>code</code> and a <a href=\"https://example.com/about\">link</a> and <a href=\"https://other.example/x\">another</a>.</p>");
        await Assert.That(export.Html).Contains("<h2>Section</h2>");
        await Assert.That(export.Html).Contains("<ul>\n<li>one</li>\n<li>two</li>\n</ul>");
        await Assert.That(export.Html).Contains("<ol>\n<li>first</li>\n<li>second</li>\n</ol>");
        await Assert.That(export.Html).Contains("<blockquote>\n<p>A quote.</p>\n</blockquote>");
        await Assert.That(export.Html).Contains("<pre><code>var a = 1 &lt; 2;</code></pre>");
        await Assert.That(export.Html).Contains("<hr>");
        await Assert.That(export.Html).Contains("<p><img src=\"https://example.com/media/abc-square.png\" alt=\"A blue square\"></p>\n<p><em>The square, as a caption</em></p>");
        await Assert.That(export.Html).Contains("<a href=\"https://example.com/blog/a-post#section\">Jump</a>");
        await Assert.That(export.Html).Contains("<a href=\"mailto:me@example.com\">me@example.com</a>");
        await Assert.That(export.Html).DoesNotContain("class=");
        await Assert.That(export.Changes).IsEmpty();
    }

    [Test]
    public async Task Unsupported_content_is_flattened_and_listed()
    {
        var markdown = """
            #### Deep heading

            | Name | Size |
            |------|------|
            | a    | 1    |

            Text with a note[^1] and ~~old~~ words.

            - [x] done
            - [ ] to do

            [^1]: The note.
            """;

        var export = HtmlExporter.Export(markdown, Canonical, footer: false);

        await Assert.That(export.Html).Contains("<h3>Deep heading</h3>");
        await Assert.That(export.Html).Contains("<p><strong>Name | Size</strong></p>\n<p>a | 1</p>");
        await Assert.That(export.Html).Contains("<p>Text with a note[1] and old words.</p>");
        await Assert.That(export.Html).Contains("<li>☑ done</li>");
        await Assert.That(export.Html).Contains("<p><strong>Notes</strong></p>\n<p>[1] The note.</p>");
        await Assert.That(export.Changes.Count).IsEqualTo(5);
        await Assert.That(export.Changes).Contains("Tables are one paragraph per row, cells separated by |.");
    }

    [Test]
    public async Task Unsafe_addresses_and_raw_html_never_get_through()
    {
        var markdown = "[click](javascript:alert(1)) and <script>alert(1)</script> and ![x](data:image/png;base64,AAAA)";

        var export = HtmlExporter.Export(markdown, Canonical, footer: false);

        await Assert.That(export.Html).DoesNotContain("javascript:");
        await Assert.That(export.Html).DoesNotContain("<script>");
        await Assert.That(export.Html).DoesNotContain("data:");
        await Assert.That(export.Html).Contains("&lt;script&gt;");
        await Assert.That(export.Changes.Count).IsEqualTo(2);
    }

    [Test]
    public async Task The_footer_links_to_the_original()
    {
        var export = HtmlExporter.Export("Hello.", Canonical, footer: true);

        await Assert.That(export.Html).EndsWith("<hr>\n<p><em>Originally published at <a href=\"https://example.com/blog/a-post\">https://example.com/blog/a-post</a>.</em></p>\n");
        await Assert.That(export.Text).IsEqualTo("Hello.\n\nOriginally published at https://example.com/blog/a-post.");
    }

    [Test]
    public async Task A_bundle_points_the_sites_own_stored_images_at_the_zip()
    {
        var markdown = "![Stored](/media/abc-a.png) ![Missing](/media/abc-gone.png) ![Elsewhere](https://other.example/b.png) ![Again](/media/abc-a.png)";

        var export = HtmlExporter.Export(markdown, Canonical, footer: false, name => name == "abc-a.png");
        var plain = HtmlExporter.Export(markdown, Canonical, footer: false);

        await Assert.That(export.Html).Contains("<img src=\"images/abc-a.png\" alt=\"Stored\">");
        await Assert.That(export.Html).Contains("<img src=\"https://example.com/media/abc-gone.png\" alt=\"Missing\">");
        await Assert.That(export.Html).Contains("<img src=\"https://other.example/b.png\" alt=\"Elsewhere\">");
        await Assert.That(export.Images).IsEquivalentTo(["abc-a.png"]);
        await Assert.That(plain.Html).Contains("<img src=\"https://example.com/media/abc-a.png\" alt=\"Stored\">");
        await Assert.That(plain.Images).IsEmpty();
    }

    [Test]
    public async Task Hash_headings_shift_down_like_on_the_site()
    {
        var export = HtmlExporter.Export("# Top\n\n## Next\n", Canonical, footer: false);

        await Assert.That(export.Html).IsEqualTo("<h2>Top</h2>\n<h3>Next</h3>\n");
    }
}
