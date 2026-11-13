using Markdig;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>A section of a post or page: the text under one heading, as plain text.</summary>
/// <param name="Item">The post or page.</param>
/// <param name="Index">The chunk's position in the item, from 0.</param>
/// <param name="Heading">The section's heading, or null for the text before the first heading.</param>
/// <param name="Text">The section's text without markup, code blocks left out.</param>
/// <param name="Code">The section's code blocks, their lines as written; empty when it has none.</param>
public sealed record Chunk(ContentItem Item, int Index, string? Heading, string Text, string Code = "");

/// <summary>
/// Splits an item's Markdown at its headings (brief, Hybrid search), so search can point at the
/// section that matched and, later, embed each section on its own. Headings inside fenced code
/// don't count. Fenced code is kept apart from the text, so a match in code can be shown as code.
/// </summary>
public static partial class Chunker
{
    // The site's own dialect, so footnotes, strikethrough and task lists come out as text rather than
    // as their markup ("[^1]", "~~") in snippets and embeddings (F-0091 L17).
    private static readonly MarkdownPipeline Pipeline = Weesite.Content.Markdown.MarkdownRenderer.Pipeline;

    /// <summary>Splits an item into chunks; sections without text are left out.</summary>
    /// <param name="item">The post or page.</param>
    /// <returns>The chunks, in order.</returns>
    public static IReadOnlyList<Chunk> Split(ContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var chunks = new List<Chunk>();
        string? heading = null;
        var section = new List<string>();
        var code = new List<string>();
        string? fence = null;

        void Flush()
        {
            var text = TaskMarker().Replace(Markdown.ToPlainText(string.Join('\n', section), Pipeline), "").Trim();
            var lines = string.Join('\n', code.Where(l => l.Trim().Length > 0)).Trim();
            if (text.Length > 0 || lines.Length > 0 || heading is not null)
            {
                chunks.Add(new Chunk(item, chunks.Count, heading, text, lines));
            }

            section.Clear();
            code.Clear();
        }

        foreach (var line in item.Markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var run = FenceRun(trimmed);
            if (fence is null && run is not null)
            {
                fence = run;
                continue;
            }

            // As CommonMark closes a fence: the same character, at least as many, and nothing after
            // them, so a ```` block can show a ``` example inside it (F-0119).
            else if (fence is not null && run is not null && run[0] == fence[0] && run.Length >= fence.Length && trimmed[run.Length..].Trim().Length == 0)
            {
                fence = null;
                continue;
            }
            else if (fence is not null)
            {
                code.Add(line);
                continue;
            }
            else if (HeadingText(line) is { } text)
            {
                Flush();
                heading = text;
                continue;
            }

            section.Add(line);
        }

        Flush();

        // An item with no text of its own (an empty body, or only an embed) still gets a chunk, so
        // it's found by its title (F-0119).
        if (chunks.Count == 0)
        {
            chunks.Add(new Chunk(item, 0, null, "", ""));
        }

        return chunks;
    }

    // A task list item's checkbox, which the plain text keeps as "[x] " or "[ ] " (F-0091 L17).
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\[[ xX]\] ", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
    private static partial System.Text.RegularExpressions.Regex TaskMarker();

    // An ATX heading ("## Title", up to three spaces of indent), without its closing hashes.
    private static string? HeadingText(string line)
    {
        var indent = line.Length - line.TrimStart(' ').Length;
        var rest = line.TrimStart(' ');
        var level = rest.Length - rest.TrimStart('#').Length;
        if (indent > 3 || level is < 1 or > 6 || (rest.Length > level && rest[level] != ' ' && rest[level] != '\t'))
        {
            return null;
        }

        // A closing run of #s counts only after a space, as in CommonMark: "Why C#" keeps its #
        // (F-0119).
        var text = rest[level..].Trim();
        var closing = text.Length - text.TrimEnd('#').Length;
        if (closing > 0 && (closing == text.Length || text[^(closing + 1)] is ' ' or '\t'))
        {
            text = text[..^closing].Trim();
        }

        return text.Length > 0 ? text : null;
    }

    // The fence a line opens or closes with: three or more backticks or tildes; null otherwise.
    private static string? FenceRun(string trimmed)
    {
        if (trimmed.Length < 3 || trimmed[0] is not ('`' or '~'))
        {
            return null;
        }

        var length = trimmed.Length - trimmed.TrimStart(trimmed[0]).Length;
        return length >= 3 ? trimmed[..length] : null;
    }
}
