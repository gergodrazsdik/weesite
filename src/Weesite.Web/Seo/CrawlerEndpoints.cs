using Microsoft.Net.Http.Headers;
using Weesite.Content;

namespace Weesite.Web.Seo;

/// <summary>
/// <c>/robots.txt</c> and <c>/sitemap.xml</c>. Crawlers, AI ones included, are welcome; a
/// non-public environment (<c>Site:NoIndex</c>) disallows everything instead.
/// </summary>
internal static class CrawlerEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapCrawlers(this IEndpointRouteBuilder app)
    {
        app.MapGet("/robots.txt", (HttpContext context, IConfiguration configuration, SiteUrls urls) =>
        {
            Cache(context);
            var text = configuration.GetValue<bool>("Site:NoIndex")
                ? "User-agent: *\nDisallow: /\n"
                : $"User-agent: *\nAllow: /\nDisallow: /admin\n\nSitemap: {urls.Absolute(context.Request, "/sitemap.xml")}\n";
            return Results.Text(text, "text/plain; charset=utf-8");
        });

        app.MapGet("/sitemap.xml", (HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
        {
            Cache(context);
            return Results.Bytes(Sitemap(content.Current, urls.Origin(context.Request)), "application/xml; charset=utf-8");
        });
    }

    private static byte[] Sitemap(ContentCatalog catalog, string origin) =>
        XmlResults.Write(writer =>
        {
            const string Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            var newest = catalog.Posts.Select(ContentUrls.LastChanged).Max();
            writer.WriteStartElement("urlset", Ns);

            void Url(string path, DateOnly? lastModified)
            {
                writer.WriteStartElement("url", Ns);
                writer.WriteElementString("loc", Ns, origin + path);
                if (lastModified is { } date)
                {
                    writer.WriteElementString("lastmod", Ns, date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                }

                writer.WriteEndElement();
            }

            Url("/", newest);
            Url("/blog", newest);
            if (catalog.Tags.Count > 0)
            {
                Url("/blog/tags", newest);
            }

            foreach (var post in catalog.Posts)
            {
                Url(ContentUrls.PathOf(post), ContentUrls.LastChanged(post));
            }

            foreach (var page in catalog.Pages)
            {
                Url(ContentUrls.PathOf(page), ContentUrls.LastChanged(page));
            }

            foreach (var (tag, posts) in catalog.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                Url(ContentUrls.TagPath(tag), posts.Select(ContentUrls.LastChanged).Max());
            }

            writer.WriteEndElement();
        });

    private static void Cache(HttpContext context) =>
        context.Response.Headers[HeaderNames.CacheControl] = Performance.ShortPublicCache;
}
