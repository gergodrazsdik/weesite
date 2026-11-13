using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using Microsoft.Net.Http.Headers;
using Weesite.Content;

namespace Weesite.Web.Seo;

/// <summary>
/// The post feeds (ADR 0002): Atom at <c>/feed.xml</c> and JSON Feed 1.1 at <c>/feed.json</c>,
/// and the same per tag at <c>/blog/tags/{tag}/feed.xml</c> and <c>.json</c>. Each carries the
/// newest posts with their full HTML, links made absolute. Pages stay out of the feeds.
/// </summary>
internal static class FeedEndpoints
{
    /// <summary>The most posts a feed carries.</summary>
    public const int MaxItems = 50;

    /// <summary>Maps the feed endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapFeeds(this IEndpointRouteBuilder app)
    {
        app.MapGet("/feed.xml", (HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
            Atom(context, urls, content.Current, null));
        app.MapGet("/feed.json", (HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
            Json(context, urls, content.Current, null));
        app.MapGet("/blog/tags/{tag}/feed.xml", (string tag, HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
            content.Current.Tags.ContainsKey(tag) ? Atom(context, urls, content.Current, tag) : Results.NotFound());
        app.MapGet("/blog/tags/{tag}/feed.json", (string tag, HttpContext context, ContentCatalogProvider content, SiteUrls urls) =>
            content.Current.Tags.ContainsKey(tag) ? Json(context, urls, content.Current, tag) : Results.NotFound());
    }

    /// <summary>Gets a feed's title.</summary>
    /// <param name="site">The site settings.</param>
    /// <param name="tag">The tag, or null for the whole site.</param>
    /// <returns>The title.</returns>
    public static string Title(SiteSettings site, string? tag) => tag is null ? site.Name : $"{site.Name} · {tag}";

    /// <summary>Gets a feed's path.</summary>
    /// <param name="tag">The tag, or null for the whole site.</param>
    /// <param name="extension"><c>xml</c> or <c>json</c>.</param>
    /// <returns>The path.</returns>
    public static string PathOf(string? tag, string extension) =>
        tag is null ? $"/feed.{extension}" : $"{ContentUrls.TagPath(tag)}/feed.{extension}";

    private static List<ContentItem> Posts(ContentCatalog catalog, string? tag) =>
        (tag is null ? catalog.Posts : catalog.Tags[tag]).Take(MaxItems).ToList();

    private static IResult Atom(HttpContext context, SiteUrls urls, ContentCatalog catalog, string? tag)
    {
        var site = catalog.Site;
        var origin = urls.Origin(context.Request);
        var posts = Posts(catalog, tag);
        var updated = posts.Select(ContentUrls.LastChanged).Max() ?? new DateOnly(2000, 1, 1);
        var home = origin + (tag is null ? "/" : ContentUrls.TagPath(tag));

        var xml = XmlResults.Write(writer =>
        {
            const string Ns = "http://www.w3.org/2005/Atom";
            writer.WriteStartElement("feed", Ns);
            writer.WriteAttributeString("xml", "lang", null, site.Language);
            writer.WriteElementString("id", Ns, home);
            writer.WriteElementString("title", Ns, Title(site, tag));
            if (site.Tagline is { } tagline)
            {
                writer.WriteElementString("subtitle", Ns, tagline);
            }

            writer.WriteElementString("updated", Ns, ContentUrls.Timestamp(updated));
            Link(writer, Ns, "self", origin + PathOf(tag, "xml"), "application/atom+xml");
            Link(writer, Ns, "alternate", home, "text/html");
            writer.WriteStartElement("author", Ns);
            writer.WriteElementString("name", Ns, site.Authors.Count > 0 ? site.Authors[0].Name : site.Name);
            writer.WriteEndElement();
            writer.WriteElementString("generator", Ns, "Weesite");

            foreach (var post in posts)
            {
                var url = origin + ContentUrls.PathOf(post);
                writer.WriteStartElement("entry", Ns);
                if (SiteCulture.Differing(post.Language, site.Language) is { } language)
                {
                    writer.WriteAttributeString("xml", "lang", null, language);
                }

                writer.WriteElementString("id", Ns, url);
                writer.WriteElementString("title", Ns, post.Title);
                Link(writer, Ns, "alternate", url, "text/html");
                if (post.Date is { } date)
                {
                    writer.WriteElementString("published", Ns, ContentUrls.Timestamp(date));
                }

                writer.WriteElementString("updated", Ns, ContentUrls.Timestamp(ContentUrls.LastChanged(post) ?? updated));
                if (site.FindAuthor(post.AuthorId) is { } author)
                {
                    writer.WriteStartElement("author", Ns);
                    writer.WriteElementString("name", Ns, author.Name);
                    writer.WriteEndElement();
                }

                foreach (var postTag in post.Tags)
                {
                    writer.WriteStartElement("category", Ns);
                    writer.WriteAttributeString("term", postTag);
                    writer.WriteEndElement();
                }

                if (post.Summary is { } summary)
                {
                    writer.WriteElementString("summary", Ns, summary);
                }

                writer.WriteStartElement("content", Ns);
                writer.WriteAttributeString("type", "html");
                writer.WriteString(ContentUrls.AbsoluteHtml(post.Html, origin + ContentUrls.PathOf(post)));
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        });

        Cache(context);
        return Results.Bytes(xml, "application/atom+xml; charset=utf-8");
    }

    private static IResult Json(HttpContext context, SiteUrls urls, ContentCatalog catalog, string? tag)
    {
        var site = catalog.Site;
        var origin = urls.Origin(context.Request);
        var home = origin + (tag is null ? "/" : ContentUrls.TagPath(tag));

        // Readable text ("·", accents) rather than \u escapes: the feed is JSON, never embedded in HTML.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", "https://jsonfeed.org/version/1.1");
            writer.WriteString("title", Title(site, tag));
            writer.WriteString("home_page_url", home);
            writer.WriteString("feed_url", origin + PathOf(tag, "json"));
            if (site.Tagline is { } tagline)
            {
                writer.WriteString("description", tagline);
            }

            writer.WriteString("language", site.Language);
            writer.WriteStartArray("authors");
            foreach (var author in site.Authors)
            {
                WriteAuthor(writer, author);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("items");
            foreach (var post in Posts(catalog, tag))
            {
                var url = origin + ContentUrls.PathOf(post);
                writer.WriteStartObject();
                writer.WriteString("id", url);
                writer.WriteString("url", url);
                writer.WriteString("title", post.Title);
                writer.WriteString("content_html", ContentUrls.AbsoluteHtml(post.Html, url));
                if (post.Summary is { } summary)
                {
                    writer.WriteString("summary", summary);
                }

                if (post.Image is { } image && image.StartsWith('/', StringComparison.Ordinal))
                {
                    writer.WriteString("image", origin + image);
                }

                if (post.Date is { } date)
                {
                    writer.WriteString("date_published", ContentUrls.Timestamp(date));
                }

                if (post.Updated is { } updated)
                {
                    writer.WriteString("date_modified", ContentUrls.Timestamp(updated));
                }

                writer.WriteStartArray("tags");
                foreach (var postTag in post.Tags)
                {
                    writer.WriteStringValue(postTag);
                }

                writer.WriteEndArray();
                if (site.FindAuthor(post.AuthorId) is { } author)
                {
                    writer.WriteStartArray("authors");
                    WriteAuthor(writer, author);
                    writer.WriteEndArray();
                }

                writer.WriteString("language", post.Language);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Cache(context);
        return Results.Bytes(stream.ToArray(), "application/feed+json; charset=utf-8");
    }

    private static void WriteAuthor(Utf8JsonWriter writer, Author author)
    {
        writer.WriteStartObject();
        writer.WriteString("name", author.Name);
        if (author.Links.Count > 0 && author.Links[0].Address is { } url)
        {
            writer.WriteString("url", url.ToString());
        }

        writer.WriteEndObject();
    }

    private static void Link(XmlWriter writer, string ns, string rel, string href, string type)
    {
        writer.WriteStartElement("link", ns);
        writer.WriteAttributeString("rel", rel);
        writer.WriteAttributeString("type", type);
        writer.WriteAttributeString("href", href);
        writer.WriteEndElement();
    }

    private static void Cache(HttpContext context) =>
        context.Response.Headers[HeaderNames.CacheControl] = Performance.ShortPublicCache;
}
