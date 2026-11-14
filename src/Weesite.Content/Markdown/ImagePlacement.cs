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

    /// <summary>
    /// Moves each image's placement from the braces after it to its classes. Braces with any other
    /// word, or with two places or two sizes, stay text: all or nothing, and reported.
    /// </summary>
    /// <param name="document">The parsed text.</param>
    /// <param name="problem">Called with an image and what's wrong with the braces after it.</param>
    public static void Apply(MarkdownDocument document, Action<LinkInline, string>? problem = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage).ToList())
        {
            if (image.NextSibling is not LiteralInline literal || AnyBraces().Match(literal.Content.ToString()) is not { Success: true } match)
            {
                continue;
            }

            if (Words(match.Value) is not { } words)
            {
                problem?.Invoke(image, $"{match.Value} after it shows as text: use .left or .right, .small or .medium, at most one of each and nothing else");
                continue;
            }

            var attributes = image.GetAttributes();
            foreach (var word in words)
            {
                attributes.AddClass(word);
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
        return AfterImage().Replace(markdown, m => Words(m.Value[1..]) is null ? m.Value : ")");
    }

    // The classes in braces, or null when they aren't a placement: only known words, at most one
    // place and one size.
    private static string[]? Words(string braces)
    {
        var words = braces[1..^1].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Any(w => w.Length < 2 || w[0] != '.' || !Classes.Contains(w[1..])))
        {
            return null;
        }

        var names = words.Select(w => w[1..]).ToArray();
        return names.Count(n => n is "left" or "right") > 1 || names.Count(n => n is "small" or "medium") > 1 ? null : names;
    }

    [GeneratedRegex(@"^\{[^{}\n]*\}", RegexOptions.NonBacktracking)]
    private static partial Regex AnyBraces();

    [GeneratedRegex(@"\)\{[ \t]*(?:\.(?:left|right|small|medium)[ \t]*)+\}", RegexOptions.NonBacktracking)]
    private static partial Regex AfterImage();
}
