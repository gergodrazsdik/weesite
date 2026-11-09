using System.Net;
using System.Text;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Weesite.Content.Markdown;

/// <summary>A post exported as portable HTML.</summary>
/// <param name="Html">The body: only the elements listed on <see cref="HtmlExporter"/>.</param>
/// <param name="Text">The same as plain text, for pasting where rich text isn't taken.</param>
/// <param name="Changes">What had to change to fit, one line per kind of change, in order of first use.</param>
/// <param name="Images">In a bundle, the media files the HTML points at under <c>images/</c>, in order of first use.</param>
public sealed record HtmlExport(string Html, string Text, IReadOnlyList<string> Changes, IReadOnlyList<string> Images);

/// <summary>
/// Exports a post's Markdown as clean HTML for publishing elsewhere by hand (brief, HTML export):
/// LinkedIn articles, newsletters, other blogs. The output uses only widely supported elements:
/// two heading levels (<c>h2</c>, <c>h3</c>), paragraphs, bold, italic, ordered and unordered
/// lists, blockquotes, inline code and code blocks, horizontal rules, line breaks, links and
/// images, with absolute URLs and no classes or styles. Anything else (tables, footnotes,
/// definition lists, task boxes, other emphasis) is flattened, and the export says what changed.
/// The title isn't part of the body: most editors take it in a field of its own.
/// </summary>
public static class HtmlExporter
{
    /// <summary>Exports Markdown.</summary>
    /// <param name="markdown">The post's Markdown body.</param>
    /// <param name="canonical">The post's public address: relative links and images resolve against it.</param>
    /// <param name="footer">Whether to end with "Originally published at" and the address.</param>
    /// <param name="bundled">
    /// For a bundle (T-0095): whether a media file exists to go into the zip. The site's own images
    /// (<c>/media/{name}</c>) that do are written as <c>images/{name}</c>; others stay absolute.
    /// Null for the plain export, where every image is an absolute address.
    /// </param>
    /// <returns>The export.</returns>
    public static HtmlExport Export(string markdown, Uri canonical, bool footer, Func<string, bool>? bundled = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(canonical);
        var document = Markdig.Markdown.Parse(markdown, MarkdownRenderer.Pipeline);
        var writer = new Writer(canonical, document.Descendants<HeadingBlock>().Any(h => h.Level == 1) ? 1 : 0, bundled);
        writer.Blocks(document);
        writer.Footnotes(document);

        var text = Markdig.Markdown.ToPlainText(markdown, MarkdownRenderer.Pipeline).Trim();
        if (footer)
        {
            var address = canonical.AbsoluteUri;
            writer.Html.Append("<hr>\n<p><em>Originally published at <a href=\"").Append(Attribute(address)).Append("\">")
                .Append(Encode(address)).Append("</a>.</em></p>\n");
            text += $"\n\nOriginally published at {address}.";
        }

        return new HtmlExport(writer.Html.ToString(), text, writer.Changes, writer.Images);
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Attribute(string text) => WebUtility.HtmlEncode(text);

    private sealed class Writer(Uri canonical, int shift, Func<string, bool>? bundled)
    {
        private readonly List<string> _images = [];

        public IReadOnlyList<string> Images => _images;

        private static readonly string[] Schemes = ["http", "https", "mailto"];

        private readonly List<string> _changes = [];

        public StringBuilder Html { get; } = new();

        public IReadOnlyList<string> Changes => _changes;

        public void Blocks(ContainerBlock container)
        {
            foreach (var block in container)
            {
                Block(block);
            }
        }

        // Footnotes at the end, as numbered paragraphs, where they were at the foot of the page.
        public void Footnotes(MarkdownDocument document)
        {
            var notes = document.Descendants<Footnote>().OrderBy(f => f.Order).ToList();
            if (notes.Count == 0)
            {
                return;
            }

            Change("Footnotes are numbered notes at the end, marked [1], [2] in the text.");
            Html.Append("<p><strong>Notes</strong></p>\n");
            foreach (var note in notes)
            {
                Html.Append("<p>[").Append(note.Order).Append("] ");
                foreach (var paragraph in note.OfType<ParagraphBlock>())
                {
                    Inlines(paragraph.Inline);
                }

                Html.Append("</p>\n");
            }
        }

        private void Block(Block block)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    Heading(heading);
                    break;
                case ParagraphBlock paragraph:
                    Paragraph(paragraph);
                    break;
                case ListBlock list:
                    List(list);
                    break;
                case QuoteBlock quote:
                    Html.Append("<blockquote>\n");
                    Blocks(quote);
                    Html.Append("</blockquote>\n");
                    break;
                case CodeBlock code:
                    Html.Append("<pre><code>").Append(Encode(code.Lines.ToString())).Append("</code></pre>\n");
                    break;
                case ThematicBreakBlock:
                    Html.Append("<hr>\n");
                    break;
                case Table table:
                    Table(table);
                    break;
                case DefinitionList definitions:
                    Definitions(definitions);
                    break;
                case Figure figure:
                    Change("Figures are their content, with the caption as a paragraph in italics.");
                    foreach (var part in figure)
                    {
                        if (part is FigureCaption caption)
                        {
                            Html.Append("<p><em>");
                            Inlines(caption.Inline);
                            Html.Append("</em></p>\n");
                        }
                        else
                        {
                            Block(part);
                        }
                    }

                    break;
                case FootnoteGroup or LinkReferenceDefinitionGroup or LinkReferenceDefinition or BlankLineBlock:
                    break;
                case ContainerBlock container:
                    Blocks(container);
                    break;
                case LeafBlock leaf when leaf.Inline is not null:
                    Html.Append("<p>");
                    Inlines(leaf.Inline);
                    Html.Append("</p>\n");
                    break;
                default:
                    break;
            }
        }

        // ## and ### keep their levels; deeper ones become h3 (two levels, brief). With # headings
        // in the text everything shifts down one, as on the site.
        private void Heading(HeadingBlock heading)
        {
            var level = heading.Level + shift;
            if (level > 3)
            {
                Change("Headings below the third level are third-level headings.");
            }

            var tag = level <= 2 ? "h2" : "h3";
            Html.Append('<').Append(tag).Append('>');
            Inlines(heading.Inline);
            Html.Append("</").Append(tag).Append(">\n");
        }

        // A paragraph that is just an image with a title: the image, then the title as its caption.
        private void Paragraph(ParagraphBlock paragraph)
        {
            Html.Append("<p>");
            Inlines(paragraph.Inline);
            Html.Append("</p>\n");
            if (paragraph.Inline?.FirstChild is LinkInline { IsImage: true, Title: { Length: > 0 } title } image
                && image.NextSibling is null)
            {
                Html.Append("<p><em>").Append(Encode(title)).Append("</em></p>\n");
            }
        }

        private void List(ListBlock list)
        {
            var tag = list.IsOrdered ? "ol" : "ul";
            Html.Append('<').Append(tag);
            if (list.IsOrdered && list.BulletType != '1')
            {
                Change("Lettered and Roman-numbered lists are numbered lists.");
            }
            else if (list.IsOrdered && list.OrderedStart is { Length: > 0 } start && start != "1" && int.TryParse(start, out var first))
            {
                Html.Append(" start=\"").Append(first).Append('"');
            }

            Html.Append(">\n");
            foreach (var item in list.OfType<ListItemBlock>())
            {
                Html.Append("<li>");

                // A tight list's items are their text, without paragraphs.
                if (!list.IsLoose && item.Count == 1 && item[0] is ParagraphBlock only)
                {
                    Inlines(only.Inline);
                }
                else
                {
                    Html.Append('\n');
                    Blocks(item);
                }

                Html.Append("</li>\n");
            }

            Html.Append("</").Append(tag).Append(">\n");
        }

        // One paragraph per row, cells separated by a vertical bar, the header row in bold.
        private void Table(Table table)
        {
            Change("Tables are one paragraph per row, cells separated by |.");
            foreach (var row in table.OfType<TableRow>())
            {
                Html.Append("<p>");
                if (row.IsHeader)
                {
                    Html.Append("<strong>");
                }

                var first = true;
                foreach (var cell in row.OfType<TableCell>())
                {
                    if (!first)
                    {
                        Html.Append(" | ");
                    }

                    first = false;
                    foreach (var paragraph in cell.OfType<ParagraphBlock>())
                    {
                        Inlines(paragraph.Inline);
                    }
                }

                if (row.IsHeader)
                {
                    Html.Append("</strong>");
                }

                Html.Append("</p>\n");
            }
        }

        private void Definitions(DefinitionList list)
        {
            Change("Definition lists are a bold term followed by its text.");
            foreach (var item in list.OfType<DefinitionItem>())
            {
                foreach (var part in item)
                {
                    if (part is DefinitionTerm term)
                    {
                        Html.Append("<p><strong>");
                        Inlines(term.Inline);
                        Html.Append("</strong></p>\n");
                    }
                    else
                    {
                        Block(part);
                    }
                }
            }
        }

        private void Inlines(ContainerInline? container)
        {
            for (var inline = container?.FirstChild; inline is not null; inline = inline.NextSibling)
            {
                Inline(inline);
            }
        }

        private void Inline(Inline inline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Html.Append(Encode(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    Html.Append("<code>").Append(Encode(code.Content)).Append("</code>");
                    break;
                case EmphasisInline emphasis:
                    Emphasis(emphasis);
                    break;
                case LinkInline { IsImage: true } image:
                    Image(image);
                    break;
                case LinkInline link:
                    Link(link.Url, link);
                    break;
                case AutolinkInline autolink:
                    if (Absolute(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url) is { } address)
                    {
                        Html.Append("<a href=\"").Append(Attribute(address)).Append("\">").Append(Encode(autolink.Url)).Append("</a>");
                    }
                    else
                    {
                        Html.Append(Encode(autolink.Url));
                    }

                    break;
                case LineBreakInline { IsHard: true }:
                    Html.Append("<br>\n");
                    break;
                case LineBreakInline:
                    Html.Append('\n');
                    break;
                case HtmlEntityInline entity:
                    Html.Append(Encode(entity.Transcoded.ToString()));
                    break;
                case FootnoteLink { IsBackLink: false } note:
                    Html.Append('[').Append(note.Footnote.Order).Append(']');
                    break;
                case FootnoteLink:
                    break;
                case TaskList task:
                    Change("Task boxes are the characters ☐ and ☑.");
                    Html.Append(task.Checked ? "☑" : "☐");
                    break;
                case AbbreviationInline abbreviation:
                    Html.Append(Encode(abbreviation.Abbreviation.Label ?? ""));
                    break;
                case ContainerInline container:
                    Inlines(container);
                    break;
                default:
                    Html.Append(Encode(inline.ToString() ?? ""));
                    break;
            }
        }

        // ** and __ are bold, * and _ italic; strikethrough, sub- and superscript, inserted and
        // marked text keep their words and lose the formatting.
        private void Emphasis(EmphasisInline emphasis)
        {
            var tag = emphasis.DelimiterChar is '*' or '_'
                ? emphasis.DelimiterCount >= 2 ? "strong" : "em"
                : null;
            if (tag is null)
            {
                Change("Strikethrough, subscript, superscript and highlighted text are plain text.");
                Inlines(emphasis);
                return;
            }

            Html.Append('<').Append(tag).Append('>');
            Inlines(emphasis);
            Html.Append("</").Append(tag).Append('>');
        }

        private void Image(LinkInline image)
        {
            var alt = string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString()));
            if (Absolute(image.Url) is not { } source)
            {
                Change("Images with an address that can't be used elsewhere are their description.");
                Html.Append(Encode(alt));
                return;
            }

            // In a bundle, the site's own images point at their copy in the zip.
            var address = new Uri(source);
            if (bundled is not null && address.IsAbsoluteUri && Uri.Compare(address, canonical, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
                && address.AbsolutePath.StartsWith("/media/", StringComparison.Ordinal) && Uri.UnescapeDataString(address.AbsolutePath["/media/".Length..]) is var name && bundled(name))
            {
                if (!_images.Contains(name))
                {
                    _images.Add(name);
                }

                source = "images/" + name;
            }

            Html.Append("<img src=\"").Append(Attribute(source)).Append("\" alt=\"").Append(Attribute(alt)).Append("\">");
        }

        private void Link(string? url, LinkInline link)
        {
            if (Absolute(url) is not { } address)
            {
                Change("Links with an address that can't be used elsewhere are plain text.");
                Inlines(link);
                return;
            }

            Html.Append("<a href=\"").Append(Attribute(address)).Append("\">");
            Inlines(link);
            Html.Append("</a>");
        }

        // An absolute http(s) or mailto address: paths and fragments resolve against the post's
        // own address, so links to the site keep working from elsewhere.
        private string? Absolute(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            return Uri.TryCreate(canonical, url.Trim(), out var address) && Schemes.Contains(address.Scheme, StringComparer.OrdinalIgnoreCase)
                ? address.AbsoluteUri
                : null;
        }

        private void Change(string change)
        {
            if (!_changes.Contains(change))
            {
                _changes.Add(change);
            }
        }
    }
}
