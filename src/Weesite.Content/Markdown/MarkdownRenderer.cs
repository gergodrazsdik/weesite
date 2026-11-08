using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Weesite.Content.Markdown;

/// <summary>A rendered item: HTML for the page and plain text for search, summaries and reading time.</summary>
/// <param name="Html">The HTML.</param>
/// <param name="PlainText">The text without markup.</param>
public sealed record RenderedMarkdown(string Html, string PlainText);

/// <summary>
/// Renders posts and pages. Raw HTML is shown as text, never passed through (ADR 0002); links
/// and images may only point to http(s), mailto or relative addresses; headings shift one level
/// down, so the page title stays the only <c>h1</c>; fenced code is highlighted on the server.
/// </summary>
public sealed class MarkdownRenderer
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    private readonly MarkdownPipeline _pipeline;
    private readonly CodeHighlighter _highlighter;

    /// <summary>Initializes a new instance of the <see cref="MarkdownRenderer"/> class.</summary>
    /// <param name="highlighter">Highlights fenced code.</param>
    public MarkdownRenderer(CodeHighlighter highlighter)
    {
        _highlighter = highlighter;

        // An explicit list rather than UseAdvancedExtensions: that would add media links (iframes,
        // against the CSP) and generic attributes (any attribute, event handlers included).
        _pipeline = new MarkdownPipelineBuilder()
            .DisableHtml()
            .UseAbbreviations()
            .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
            .UseAutoLinks()
            .UseDefinitionLists()
            .UseEmphasisExtras()
            .UseFigures()
            .UseFootnotes()
            .UseListExtras()
            .UsePipeTables()
            .UseTaskLists()
            .Build();
    }

    /// <summary>Renders Markdown.</summary>
    /// <param name="markdown">The Markdown body of a post or page.</param>
    /// <returns>The HTML and the plain text.</returns>
    public RenderedMarkdown Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdig.Markdown.Parse(markdown, _pipeline);
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case HeadingBlock heading:
                    heading.Level = Math.Min(6, heading.Level + 1);
                    break;
                case LinkInline link when !IsSafe(link.Url):
                    link.Url = "";
                    break;
                case AutolinkInline autolink when !IsSafe(autolink.Url):
                    autolink.Url = "";
                    break;
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.ObjectRenderers.ReplaceOrAdd<CodeBlockRenderer>(new HighlightingCodeBlockRenderer(_highlighter));
        renderer.Render(document);
        writer.Flush();

        return new RenderedMarkdown(writer.ToString(), Markdig.Markdown.ToPlainText(markdown, _pipeline).Trim());
    }

    private static bool IsSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        // Paths, fragments and queries on this site; "//host" keeps the page's http(s) scheme.
        var trimmed = url.Trim();
        if (trimmed[0] is '/' or '#' or '?')
        {
            return true;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return AllowedSchemes.Contains(absolute.Scheme, StringComparer.OrdinalIgnoreCase);
        }

        // Relative paths such as "notes" or "../x" have no scheme; anything with a colon before
        // the first slash would be a scheme the check above didn't accept.
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        return colon < 0 || (slash >= 0 && slash < colon);
    }

    /// <summary>Writes fenced and indented code blocks with server-side highlighting.</summary>
    private sealed class HighlightingCodeBlockRenderer(CodeHighlighter highlighter) : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            var language = obj is FencedCodeBlock fenced && !string.IsNullOrWhiteSpace(fenced.Info) ? fenced.Info.Trim() : null;
            var code = obj.Lines.ToString();

            renderer.EnsureLine();
            renderer.Write("<pre class=\"code\"");
            if (language is not null)
            {
                renderer.Write(" data-language=\"").WriteEscape(language).Write('"');
            }

            renderer.Write("><code>");
            renderer.Write(highlighter.Highlight(code, language));
            renderer.WriteLine("</code></pre>");
        }
    }
}
