using System.Text;
using Weesite.Content.Markdown;

namespace Weesite.Web.Agents;

/// <summary>
/// The Markdown a post or page may use, for the editor MCP's <c>markdown_guide</c> (F-0136): what
/// <see cref="MarkdownRenderer.Pipeline"/> turns on, the site's own rules for images, links and code,
/// and the code languages the highlighter knows. Keep it in step with the pipeline.
/// </summary>
internal static class MarkdownGuide
{
    private const string Body = """
        Posts and pages are CommonMark with the extensions below. Raw HTML isn't rendered: it shows as text. Check a text with preview_item before saving it: it shows the result and lists what the site would warn about.

        ## Front matter

        A file is YAML front matter between `---` lines, then the text. The fields: `title`, `slug`, `kind` (post or page), `date` (yyyy-MM-dd, posts), `tags` (a list), `summary` (at most 160 characters), `lang` (only when the item isn't in the site's language), `image` (the picture link previews show, a `/media/…` address, best 1200 × 630 pixels) and `image_alt` (what that picture shows).

        ## Structure

        - Sections start at `##`: the title is the page's only `#` heading. A text that uses `#` has every heading moved down one level (`######` stays the sixth).
        - Headings get IDs as on GitHub ("## Next steps" is `#next-steps`), so `[see below](#next-steps)` links to one. A post lists its second-level headings under "On this page": the `##` ones, or the `#` ones in a text that uses `#`.
        - Lists: `-`, `*` or `1.`; also `a.` and `i.` lists. A roman list right after a lettered one is read as more letters (`i.` follows `h.`): put a paragraph between them. Task lists: `- [ ]` and `- [x]`.
        - Tables: pipe tables, a header row then `| --- |`; `:---`, `:---:` and `---:` align a column.
        - Block quotes: `> text`. A rule: `---` on its own line, with an empty line above it.
        - Footnotes: `text[^1]`, then `[^1]: the note` on its own line.
        - Definition lists: a term on one line, then `: its definition` on the next.
        - Abbreviations: `*[HTML]: HyperText Markup Language` on its own line explains the term wherever it appears.

        ## Text

        - `**bold**`, `_italic_`, `~~struck through~~`, `==marked==`, `++inserted++`, `H~2~O` (subscript), `x^2^` (superscript), `` `code` ``.
        - A line break inside a paragraph: a backslash at the end of the line.
        - Links: `[text](https://example.com)`; http, https and mailto, and paths on this site such as `/blog/a-post`. A link with any other scheme (ftp:, javascript:, data:) shows as its text, not as a link. Bare addresses, https://example.com or www.example.com, become links.
        - Punctuation stays as typed: write the characters you want (– — … “ ” ’), not `--`, `...` or straight quotes.

        ## Images and files

        - Images and files come from the site's Media: list_media gives each one's Markdown, upload_media adds one, replace_media swaps one for a new version. Images from other sites don't show (the site's security policy allows only its own).
        - Every image needs alt text that says what it shows: `![A heron on a post at dusk](/media/…)`.
        - An image alone in its paragraph with a title becomes a figure with that caption: `![alt text](/media/… "The caption")`.
        - Placement and size go in braces right after the image: `{.left}` or `{.right}` puts it beside the text, which wraps around it; `{.small}` (at most a third of the column) or `{.medium}` (half) makes it smaller, and alone centres it. They combine with each other and with a caption: `![A bell](/media/… "The bell"){.right .medium}`. On phones only small ones stay beside the text.
        - Anything else in braces shows as text, after an image or anywhere else (in a heading it also ends up in the heading's ID): there are no other attributes, classes or custom IDs.
        - A link to a file that isn't an image, `[the slides](/media/….pdf)`, downloads it.

        ## Code

        - Fenced code blocks name their language after the opening fence and are highlighted on the server. A file name follows the language, shown above the code: ```` ```js filename="index.js" ```` (or `title="…"`); for plain text, ```` ```text filename="notes.txt" ````.
        - A language the site doesn't know shows as plain code; `text` (or no language) is plain on purpose.

        ## Not supported

        These show as written, as text or plain code: GitHub alerts (`> [!NOTE]`), Mermaid and other diagrams, maths (`$…$`), emoji shortcodes (`:smile:`; write the emoji itself), raw HTML, embedded video and iframes. Use a block quote for a note, and an image from Media for a diagram.
        """;

    /// <summary>Writes the guide.</summary>
    /// <param name="site">The site's name, for the first line.</param>
    /// <param name="languages">The highlighter's languages.</param>
    /// <returns>The guide, as Markdown.</returns>
    public static string Text(string site, IReadOnlyList<CodeLanguage> languages)
    {
        var text = new StringBuilder()
            .AppendLine("# Markdown for posts and pages")
            .AppendLine()
            .Append("The Markdown ").Append(site).AppendLine(" renders, and how it shows it.")
            .AppendLine()
            .AppendLine(Body)
            .Append("- Languages, as the name to put after the fence (other names for it in brackets): ");
        text.AppendJoin(", ", languages
            .Where(l => l.FenceNames.Count > 0)
            .OrderBy(l => l.FenceNames[0], StringComparer.Ordinal)
            .Select(l => l.FenceNames.Count > 1 ? $"{l.FenceNames[0]} ({string.Join(", ", l.FenceNames.Skip(1))})" : l.FenceNames[0]));
        return text.AppendLine(".").ToString();
    }
}
