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
public sealed partial class MarkdownRenderer
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
    /// (iframes, against the CSP) and generic attributes (any attribute, event handlers included,
    /// read from braces anywhere in the text); an image's placement in braces is
    /// <see cref="ImagePlacement"/>'s narrower form.
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
        ImagePlacement.Apply(document);
        List<AutolinkInline> unsafeAutolinks = [];
        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case HeadingBlock heading:
                    heading.Level = Math.Min(6, heading.Level + shift);
                    break;
                case LinkInline link:
                    Check(link, media, problems);
                    break;
                case AutolinkInline autolink when !IsSafe(autolink.Url):
                    problems.Add($"link {autolink.Url} shows as text: only http, https and mailto links, and paths on this site, are allowed");
                    unsafeAutolinks.Add(autolink);
                    break;
            }
        }

        // An address with another scheme stays as text rather than a link to nowhere (F-0145).
        foreach (var autolink in unsafeAutolinks)
        {
            autolink.ReplaceBy(new LiteralInline(autolink.Url));
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        _pipeline.Setup(renderer);
        renderer.ObjectRenderers.ReplaceOrAdd<CodeBlockRenderer>(new HighlightingCodeBlockRenderer(_highlighter));
        renderer.ObjectRenderers.ReplaceOrAdd<LinkInlineRenderer>(new ResponsiveImageRenderer(media ?? MediaIndex.Empty));
        renderer.ObjectRenderers.ReplaceOrAdd<ParagraphRenderer>(new CaptionedImageRenderer());
        renderer.Render(document);
        writer.Flush();

        return new RenderedMarkdown(writer.ToString(), Markdig.Markdown.ToPlainText(ImagePlacement.Strip(markdown), _pipeline).Trim(), problems);
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
            .Where(language => !IsFileName(language))
            .Where(language => language.Length > 0 && !CodeHighlighter.IsPlain(language) && !_highlighter.Supports(language))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // A link or image the site won't show as written (F-0145): another scheme becomes text, and an
    // image needs alt text, a file that's in Media and no other site (the CSP allows only its own).
    private static void Check(LinkInline link, MediaIndex? media, List<string> problems)
    {
        if (!IsSafe(link.Url))
        {
            problems.Add($"{(link.IsImage ? "image" : "link")} {link.Url} shows as text: only http, https and mailto links, and paths on this site, are allowed");
            link.Url = "";
            link.SetData(Unsafe, true);
            return;
        }

        if (link.IsImage && string.IsNullOrWhiteSpace(AltText(link)))
        {
            problems.Add($"image {link.Url} has no alt text");
        }

        var url = link.Url?.Trim() ?? "";
        if (url.StartsWith("/media/", StringComparison.Ordinal) && media is not null && !ReferenceEquals(media, MediaIndex.Empty)
            && !media.Has(url["/media/".Length..].Split('?', '#')[0]))
        {
            problems.Add($"{(link.IsImage ? "image" : "file")} {url} isn't in Media");
        }
        else if (link.IsImage && Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            problems.Add($"image {url} is on another site and won't show: the site shows only images from its Media");
        }
    }

    // Marks a link or image whose address was dropped: it renders as its text.
    private static readonly object Unsafe = new();

    // A fence's first word that's a file name, not a language: filename="…" or title="…".
    private static bool IsFileName(string info) =>
        info.StartsWith("filename=", StringComparison.Ordinal) || info.StartsWith("title=", StringComparison.Ordinal);

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
            // A dropped address (F-0145): a link shows its text, an image its alt text.
            if (link.GetData(Unsafe) is true)
            {
                if (link.IsImage)
                {
                    renderer.WriteEscape(AltText(link));
                }
                else
                {
                    renderer.WriteChildren(link);
                }

                return;
            }

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
            if (link.TryGetAttributes()?.Classes is { Count: > 0 } classes)
            {
                renderer.Write(" class=\"").WriteEscape(string.Join(' ', classes)).Write('"');
            }

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
            if (obj.Inline?.FirstChild is LinkInline { IsImage: true, Title: { Length: > 0 } title } image && obj.Inline.FirstChild.NextSibling is null)
            {
                // A placed image's classes go on its figure, which then sits beside the text (F-0142).
                renderer.EnsureLine();
                if (image.TryGetAttributes() is { Classes: { Count: > 0 } classes } attributes)
                {
                    renderer.Write("<figure class=\"").WriteEscape(string.Join(' ', classes)).Write("\">");
                    attributes.Classes = null;
                }
                else
                {
                    renderer.Write("<figure>");
                }

                renderer.WriteLeafInline(obj);
                renderer.Write("<figcaption>").WriteEscape(title).WriteLine("</figcaption></figure>");
                return;
            }

            base.Write(renderer, obj);
        }
    }

    /// <summary>
    /// Writes fenced and indented code blocks with server-side highlighting. A fence can name a
    /// file after its language, <c>```js filename="index.js"</c> (or <c>title="…"</c>), which the
    /// bar shows (F-0135).
    /// </summary>
    private sealed partial class HighlightingCodeBlockRenderer(CodeHighlighter highlighter) : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            // A fence that starts with the file name has no language (F-0145).
            var info = obj is FencedCodeBlock fenced ? $"{fenced.Info} {fenced.Arguments}".Trim() : "";
            var language = obj is FencedCodeBlock { Info: { Length: > 0 } first } && !IsFileName(first) ? first.Trim() : null;
            var file = FileName().Match(info) is { Success: true } match ? match.Groups["name"].Value.Trim() : null;
            var code = obj.Lines.ToString();

            renderer.EnsureLine();

            // Long lines scroll sideways; tabindex lets keyboard users focus the block and scroll it
            // (WCAG 2.1.1, axe's scrollable-region-focusable).
            // A bar names the file, if given, and the language; the public site's copy-code.js adds
            // a Copy button to it.
            renderer.Write("<div class=\"code-block\"><div class=\"bar\"><span>");
            if (!string.IsNullOrEmpty(file))
            {
                renderer.Write("<span class=\"file\">").WriteEscape(file).Write("</span> ");
            }

            renderer.WriteEscape(language ?? "text").Write("</span></div>");
            renderer.Write("<pre class=\"code\" tabindex=\"0\"");
            if (language is not null)
            {
                renderer.Write(" data-language=\"").WriteEscape(language).Write('"');
            }

            renderer.Write("><code>");
            renderer.Write(highlighter.Highlight(code, language));
            renderer.WriteLine("</code></pre></div>");
        }

        [System.Text.RegularExpressions.GeneratedRegex("(?:^|\\s)(?:filename|title)=\"(?<name>[^\"]{1,200})\"", System.Text.RegularExpressions.RegexOptions.NonBacktracking)]
        private static partial System.Text.RegularExpressions.Regex FileName();
    }
}
