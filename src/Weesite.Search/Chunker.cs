using Markdig;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>A section of a post or page: the text under one heading, as plain text.</summary>
/// <param name="Item">The post or page.</param>
/// <param name="Index">The chunk's position in the item, from 0.</param>
/// <param name="Heading">The section's heading, or null for the text before the first heading.</param>
/// <param name="Text">The section's text without markup.</param>
public sealed record Chunk(ContentItem Item, int Index, string? Heading, string Text);

/// <summary>
/// Splits an item's Markdown at its headings (brief, Hybrid search), so search can point at the
/// section that matched and, later, embed each section on its own. Headings inside fenced code
/// don't count.
/// </summary>
public static class Chunker
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    /// <summary>Splits an item into chunks; sections without text are left out.</summary>
    /// <param name="item">The post or page.</param>
    /// <returns>The chunks, in order.</returns>
    public static IReadOnlyList<Chunk> Split(ContentItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var chunks = new List<Chunk>();
        string? heading = null;
        var section = new List<string>();
        string? fence = null;

        void Flush()
        {
            var text = Markdown.ToPlainText(string.Join('\n', section), Pipeline).Trim();
            if (text.Length > 0 || heading is not null)
            {
                chunks.Add(new Chunk(item, chunks.Count, heading, text));
            }

            section.Clear();
        }

        foreach (var line in item.Markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (fence is null && (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)))
            {
                fence = trimmed[..3];
            }
            else if (fence is not null && trimmed.StartsWith(fence, StringComparison.Ordinal))
            {
                fence = null;
            }
            else if (fence is null && HeadingText(line) is { } text)
            {
                Flush();
                heading = text;
                continue;
            }

            section.Add(line);
        }

        Flush();
        return chunks;
    }

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

        var text = rest[level..].Trim().TrimEnd('#').Trim();
        return text.Length > 0 ? text : null;
    }
}
