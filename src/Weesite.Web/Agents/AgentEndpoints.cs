using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Weesite.Content;
using Weesite.Web.Seo;

namespace Weesite.Web.Agents;

/// <summary>
/// Markdown for agents: <c>/llms.txt</c>, <c>/llms-full.txt</c>, <c>.md</c> versions of every post
/// and page, and content negotiation on their HTML URLs (<c>Accept: text/markdown</c>, with
/// <c>Vary: Accept</c> on every response from those URLs).
/// </summary>
internal static partial class AgentEndpoints
{
    private const string MarkdownType = "text/markdown; charset=utf-8";

    /// <summary>Maps <c>/llms.txt</c>, <c>/llms-full.txt</c> and the <c>.md</c> URLs.</summary>
    /// <param name="app">The app.</param>
    public static void MapAgentText(this IEndpointRouteBuilder app)
    {
        app.MapGet("/llms.txt", (HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
            Text(context, AgentText.Index(content.Current, urls.Origin(context.Request)), "text/plain; charset=utf-8"));
        app.MapGet("/llms-full.txt", (HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
            Text(context, AgentText.Full(content.Current, urls.Origin(context.Request)), "text/plain; charset=utf-8"));
        app.MapGet("/blog/{slug}.md", (string slug, HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
            ItemMarkdown(context, content.Current, content.Current.FindPost(slug), urls));
        app.MapGet("/{slug}.md", (string slug, HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
            ItemMarkdown(context, content.Current, content.Current.FindPage(slug), urls));
    }

    /// <summary>
    /// Answers a post's or page's URL with Markdown when the client prefers it, and marks those
    /// responses as varying by <c>Accept</c> either way. Runs after the slug redirects.
    /// </summary>
    /// <param name="app">The app.</param>
    public static void UseMarkdownNegotiation(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var item = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
                ? FindItem(context)
                : null;
            if (item is null)
            {
                await next(context);
                return;
            }

            context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);
            if (!PrefersMarkdown(context.Request.Headers.Accept))
            {
                await next(context);
                return;
            }

            var services = context.RequestServices;
            var catalog = services.GetRequiredService<ContentCatalogProvider>().Current;
            var origin = services.GetRequiredService<SiteUrls>().Origin(context.Request);
            await Text(context, AgentText.Markdown(item, catalog.Site, origin), MarkdownType).ExecuteAsync(context);
        });

    /// <summary>
    /// Whether an <c>Accept</c> header asks for <c>text/markdown</c> at least as strongly as for
    /// <c>text/html</c>. Wildcards don't count: browsers send <c>*/*</c> and want HTML.
    /// </summary>
    /// <param name="accept">The header values.</param>
    /// <returns>True for Markdown.</returns>
    internal static bool PrefersMarkdown(StringValues accept)
    {
        if (!MediaTypeHeaderValue.TryParseList(accept, out var types))
        {
            return false;
        }

        double Quality(string type) => types
            .Where(t => t.MediaType.Equals(type, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Quality ?? 1.0)
            .DefaultIfEmpty(0)
            .Max();

        var markdown = Quality("text/markdown");
        return markdown > 0 && markdown >= Quality("text/html");
    }

    private static ContentItem? FindItem(HttpContext context)
    {
        var catalog = context.RequestServices.GetRequiredService<ContentCatalogProvider>().Current;
        var match = ItemPath().Match(context.Request.Path.Value ?? "");
        if (!match.Success)
        {
            return null;
        }

        var slug = match.Groups["slug"].Value;
        return match.Groups["blog"].Success ? catalog.FindPost(slug) : catalog.FindPage(slug);
    }

    private static IResult ItemMarkdown(HttpContext context, ContentCatalog catalog, ContentItem? item, SiteUrls urls) =>
        item is null
            ? Results.NotFound()
            : Text(context, AgentText.Markdown(item, catalog.Site, urls.Origin(context.Request)), MarkdownType);

    // Content changes only with a new revision for now (ADR 0002), so a short shared cache is safe.
    private static IResult Text(HttpContext context, string text, string contentType)
    {
        context.Response.Headers[HeaderNames.CacheControl] = "public, max-age=300";
        context.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        return Results.Text(text, contentType);
    }

    [GeneratedRegex("^(?<blog>/blog)?/(?<slug>[a-z0-9]+(?:-[a-z0-9]+)*)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ItemPath();
}
