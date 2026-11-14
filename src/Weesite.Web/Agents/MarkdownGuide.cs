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
        Posts and pages are CommonMark with the extensions below. Raw HTML isn't rendered: it shows as text. Check a text with preview_item before saving it.

        ## Structure

        - Sections start at `##`: the title is the page's only `#` heading. A text that uses `#` has every heading moved down one level.
        - Headings get IDs as on GitHub ("## Next steps" is `#next-steps`), so `[see below](#next-steps)` links to one. A post lists its `##` headings under "On this page".
        - Lists: `-`, `*` or `1.`; also `a.` and `i.` lists. Task lists: `- [ ]` and `- [x]`.
        - Tables: pipe tables, a header row then `| --- |`; `:---`, `:---:` and `---:` align a column.
        - Block quotes: `> text`.
        - Footnotes: `text[^1]`, then `[^1]: the note` on its own line.
        - Definition lists: a term on one line, then `: its definition` on the next.
        - Abbreviations: `*[HTML]: HyperText Markup Language` on its own line explains the term wherever it appears.

        ## Text

        - `**bold**`, `_italic_`, `~~struck through~~`, `==marked==`, `++inserted++`, `H~2~O` (subscript), `x^2^` (superscript), `` `code` ``.
        - Links: `[text](https://example.com)`, http, https and mailto only (other schemes are dropped); paths on this site such as `/blog/a-post`. Bare addresses such as https://example.com become links.

        ## Images and files

        - Images and files come from the site's Media: list_media gives each one's Markdown, upload_media adds one. Images from other sites don't load (the site's security policy blocks them).
        - Every image needs alt text that says what it shows: `![A heron on a post at dusk](/media/…)`. preview_item reports images without it.
        - An image alone in its paragraph with a title becomes a figure with that caption: `![alt text](/media/… "The caption")`.
        - Beside the text, which wraps around it: `{.left}` or `{.right}` right after the image, with `{.small}` (a third of the column) or `{.medium}` (half) for its size, such as `![A bell](/media/…){.left .small}`. On phones only small ones stay beside the text. Nothing else in braces does anything.
        - A link to a file that isn't an image, `[the slides](/media/….pdf)`, downloads it.

        ## Code

        - Fenced code blocks name their language after the opening fence and are highlighted on the server. A file name can follow the language: `filename="index.js"` (or `title="…"`), shown above the code.
        - A language the site doesn't know shows as plain code; `text` (or no language) is plain on purpose.
        """;

    /// <summary>Writes the guide.</summary>
    /// <param name="site">The site's name, for the heading.</param>
    /// <param name="languages">The highlighter's languages.</param>
    /// <returns>The guide, as Markdown.</returns>
    public static string Text(string site, IReadOnlyList<CodeLanguage> languages)
    {
        var text = new StringBuilder()
            .Append("# Markdown on ").AppendLine(site)
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
