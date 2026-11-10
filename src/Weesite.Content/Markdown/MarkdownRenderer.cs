using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Weesite.Content.Media;

namespace Weesite.Content.Markdown;

/// <summary>A rendered item: HTML for the page and plain text for search, summaries and reading time.</summary>
/// <param name="Html">The HTML.</param>
/// <param name="PlainText">The text without markup.</param>
/// <param name="Problems">What should be fixed in the source, such as images without alt text.</param>
public sealed record RenderedMarkdown(string Html, string PlainText, IReadOnlyList<string> Problems);

/// <summary>
/// Renders posts and pages. Raw HTML is shown as text, never passed through (ADR 0002); links
/// and images may only point to http(s), mailto or relative addresses; if the text uses
/// <c>#</c> headings, all headings shift down so the page title stays the only <c>h1</c>; fenced
/// code is highlighted on the server.
/// </summary>
public sealed class MarkdownRenderer
{
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    private readonly MarkdownPipeline _pipeline = Pipeline;
    private readonly CodeHighlighter _highlighter;

    /// <summary>Initializes a new instance of the <see cref="MarkdownRenderer"/> class.</summary>
    /// <param name="highlighter">Highlights fenced code.</param>
    public MarkdownRenderer(CodeHighlighter highlighter)
    {
        _highlighter = highlighter;
    }

    /// <summary>
    /// Gets the Markdown dialect of posts and pages, shared with the HTML export so both read the
    /// same syntax. An explicit list rather than UseAdvancedExtensions: that would add media links
    /// (iframes, against the CSP) and generic attributes (any attribute, event handlers included).
    /// </summary>
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
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

    /// <summary>Renders Markdown.</summary>
    /// <param name="markdown">The Markdown body of a post or page.</param>
    /// <param name="media">Sizes and variants of the images in <c>/media/</c>, for <c>srcset</c>.</param>
    /// <returns>The HTML, the plain text and any problems found.</returns>
    public RenderedMarkdown Render(string markdown, MediaIndex? media = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var problems = new List<string>();
        var document = Markdig.Markdown.Parse(markdown, _pipeline);

        // Authors normally start sections at ##; only text that uses # moves down a level.
        var shift = document.Descendants<HeadingBlock>().Any(h => h.Level == 1) ? 1 : 0;
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case HeadingBlock heading:
                    heading.Level = Math.Min(6, heading.Level + shift);
                    break;
                case LinkInline link when !IsSafe(link.Url):
                    link.Url = "";
                    break;
                case LinkInline { IsImage: true } image when string.IsNullOrWhiteSpace(AltText(image)):
                    problems.Add($"image {image.Url} has no alt text");
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
        renderer.ObjectRenderers.ReplaceOrAdd<LinkInlineRenderer>(new ResponsiveImageRenderer(media ?? MediaIndex.Empty));
        renderer.ObjectRenderers.ReplaceOrAdd<ParagraphRenderer>(new CaptionedImageRenderer());
        renderer.Render(document);
        writer.Flush();

        return new RenderedMarkdown(writer.ToString(), Markdig.Markdown.ToPlainText(markdown, _pipeline).Trim(), problems);
    }

    /// <summary>
    /// Lists the languages named by fenced code blocks that have no grammar, so those blocks show
    /// as plain code. Fences without a language, or with <c>text</c> and the like, aren't listed.
    /// </summary>
    /// <param name="markdown">The Markdown body of a post or page.</param>
    /// <returns>The names as written, each once, in order of first use.</returns>
    public IReadOnlyList<string> UnknownCodeLanguages(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return [.. Markdig.Markdown.Parse(markdown, _pipeline)
            .Descendants<FencedCodeBlock>()
            .Select(fenced => fenced.Info?.Trim())
            .OfType<string>()
            .Where(language => language.Length > 0 && !CodeHighlighter.IsPlain(language) && !_highlighter.Supports(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string AltText(LinkInline image) =>
        string.Concat(image.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

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

    /// <summary>
    /// Writes images from <c>/media/</c> with their size, a <c>srcset</c> of the WebP variants,
    /// lazy loading and async decoding; everything else as Markdig does.
    /// </summary>
    private sealed class ResponsiveImageRenderer(MediaIndex media) : LinkInlineRenderer
    {
        private const string Prefix = "/media/";

        // The reading column is about 42rem wide (T-0036); narrower screens use the full width.
        private const string Sizes = "(min-width: 44rem) 42rem, 100vw";

        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            // A link to a stored file that downloads rather than opens (not a web image) gets a
            // class, which the stylesheet gives a download icon (T-0105), and opens in a new tab,
            // which its accessible name says (T-0113). The text is English until the site is
            // translated (T-0060).
            if (!link.IsImage && link.Url is { } url && url.StartsWith(Prefix, StringComparison.Ordinal) && !OpensInline(url))
            {
                renderer.Write("<a href=\"").WriteEscapeUrl(url).Write("\" class=\"file-link\" target=\"_blank\" rel=\"noopener\"");
                if (!string.IsNullOrEmpty(link.Title))
                {
                    renderer.Write(" title=\"").WriteEscape(link.Title).Write('"');
                }

                renderer.Write('>');
                renderer.WriteChildren(link);
                renderer.Write("<span class=\"visually-hidden\"> (download, opens in a new tab)</span></a>");
                return;
            }

            if (!link.IsImage || link.Url is null || !link.Url.StartsWith(Prefix, StringComparison.Ordinal)
                || media.Find(link.Url[Prefix.Length..]) is not { } info)
            {
                base.Write(renderer, link);
                return;
            }

            renderer.Write("<img src=\"").WriteEscapeUrl(link.Url).Write('"');
            if (info.Variants.Count > 0)
            {
                renderer.Write(" srcset=\"");
                foreach (var (width, name) in info.Variants)
                {
                    renderer.WriteEscapeUrl(Prefix + name).Write(' ').Write(width.ToString(System.Globalization.CultureInfo.InvariantCulture)).Write("w, ");
                }

                renderer.WriteEscapeUrl(link.Url).Write(' ').Write(info.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)).Write("w\"");
                renderer.Write(" sizes=\"").Write(Sizes).Write('"');
            }

            renderer.Write(" width=\"").Write(info.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)).Write('"');
            renderer.Write(" height=\"").Write(info.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)).Write('"');
            renderer.Write(" alt=\"").WriteEscape(AltText(link)).Write('"');
            renderer.Write(" loading=\"lazy\" decoding=\"async\" />");
        }

        // The types the media endpoint serves inline; everything else is an attachment.
        private static bool OpensInline(string url)
        {
            var path = url.AsSpan();
            var end = path.IndexOfAny('?', '#');
            var extension = System.IO.Path.GetExtension(end < 0 ? path : path[..end]);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) || extension.Equals(".avif", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Writes a paragraph that holds only an image with a title as a figure, the title as its
    /// caption (ADR 0006); the alt text stays the image's description, so a screen reader doesn't
    /// read the same text twice. Other paragraphs render as usual.
    /// </summary>
    private sealed class CaptionedImageRenderer : ParagraphRenderer
    {
        protected override void Write(HtmlRenderer renderer, ParagraphBlock obj)
        {
            if (obj.Inline?.FirstChild is LinkInline { IsImage: true, Title: { Length: > 0 } title } && obj.Inline.FirstChild.NextSibling is null)
            {
                renderer.EnsureLine();
                renderer.Write("<figure>");
                renderer.WriteLeafInline(obj);
                renderer.Write("<figcaption>").WriteEscape(title).WriteLine("</figcaption></figure>");
                return;
            }

            base.Write(renderer, obj);
        }
    }

    /// <summary>Writes fenced and indented code blocks with server-side highlighting.</summary>
    private sealed class HighlightingCodeBlockRenderer(CodeHighlighter highlighter) : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            var language = obj is FencedCodeBlock fenced && !string.IsNullOrWhiteSpace(fenced.Info) ? fenced.Info.Trim() : null;
            var code = obj.Lines.ToString();

            renderer.EnsureLine();

            // Long lines scroll sideways; tabindex lets keyboard users focus the block and scroll it
            // (WCAG 2.1.1, axe's scrollable-region-focusable).
            // A bar names the language; the public site's copy-code.js adds a Copy button to it.
            renderer.Write("<div class=\"code-block\"><div class=\"bar\"><span>").WriteEscape(language ?? "text").Write("</span></div>");
            renderer.Write("<pre class=\"code\" tabindex=\"0\"");
            if (language is not null)
            {
                renderer.Write(" data-language=\"").WriteEscape(language).Write('"');
            }

            renderer.Write("><code>");
            renderer.Write(highlighter.Highlight(code, language));
            renderer.WriteLine("</code></pre></div>");
        }
    }
}
