using System.Text.RegularExpressions;
using Markdig.Helpers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Weesite.Content.Markdown;

/// <summary>
/// An image's place beside the text and its size (F-0142): braces right after an image that hold
/// only <c>.left</c>, <c>.right</c>, <c>.small</c> or <c>.medium</c>, such as
/// <c>![A bell](/media/…){.left .small}</c>. Markdig's generic attributes would also read braces in
/// ordinary text ("the {name} placeholder" lost its word) and allow any attribute, so this reads
/// only that one form; anything else in braces stays text.
/// </summary>
public static partial class ImagePlacement
{
    /// <summary>Gets the classes an image may take.</summary>
    public static IReadOnlySet<string> Classes { get; } = new HashSet<string>(StringComparer.Ordinal) { "left", "right", "small", "medium" };

    /// <summary>Moves each image's placement from the braces after it to its classes.</summary>
    /// <param name="document">The parsed text.</param>
    public static void Apply(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage).ToList())
        {
            if (image.NextSibling is not LiteralInline literal || Braces().Match(literal.Content.ToString()) is not { Success: true } match)
            {
                continue;
            }

            var attributes = image.GetAttributes();
            foreach (var name in match.Value[1..^1].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                attributes.AddClass(name[1..]);
            }

            var rest = literal.Content.ToString()[match.Length..];
            if (rest.Length == 0)
            {
                literal.Remove();
            }
            else
            {
                literal.Content = new StringSlice(rest);
            }
        }
    }

    /// <summary>Removes the braces from a text, for its plain-text form.</summary>
    /// <param name="markdown">The Markdown.</param>
    /// <returns>It without placement braces after images.</returns>
    public static string Strip(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return AfterImage().Replace(markdown, ")");
    }

    [GeneratedRegex(@"^\{[ \t]*(?:\.(?:left|right|small|medium)[ \t]*)+\}", RegexOptions.NonBacktracking)]
    private static partial Regex Braces();

    [GeneratedRegex(@"\)\{[ \t]*(?:\.(?:left|right|small|medium)[ \t]*)+\}", RegexOptions.NonBacktracking)]
    private static partial Regex AfterImage();
}
