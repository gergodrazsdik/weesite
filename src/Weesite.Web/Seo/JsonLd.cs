using System.Text.Json;
using System.Text.Json.Nodes;
using Weesite.Content;

namespace Weesite.Web.Seo;

/// <summary>
/// schema.org JSON-LD: <c>BlogPosting</c> for posts (with a <c>Person</c> author),
/// <c>WebPage</c> for pages and <c>WebSite</c> for the home page. It goes into a
/// <c>&lt;script type="application/ld+json"&gt;</c> data block, which browsers never run; the
/// serializer escapes <c>&lt;</c>, <c>&gt;</c> and <c>&amp;</c>, so text can't close the element.
/// </summary>
internal static class JsonLd
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Builds the JSON-LD for a page.</summary>
    /// <param name="site">The site settings.</param>
    /// <param name="item">The post or page, or null.</param>
    /// <param name="isHome">Whether this is the home page (gets <c>WebSite</c> when there's no item).</param>
    /// <param name="url">The page's canonical URL.</param>
    /// <param name="origin">The site's origin.</param>
    /// <param name="imageUrl">The Open Graph image's absolute URL.</param>
    /// <returns>The JSON, or null when the page has none.</returns>
    public static string? For(SiteSettings site, ContentItem? item, bool isHome, string url, string origin, string imageUrl)
    {
        ArgumentNullException.ThrowIfNull(site);
        var data = item switch
        {
            { Kind: ContentKind.Post } post => Post(site, post, url, imageUrl),
            { Kind: ContentKind.Page } page => Thing("WebPage", page.Title, page.Description, url, page.Language),
            null when isHome => Thing("WebSite", site.Name, site.Tagline, origin + "/", site.Language),
            _ => (JsonObject?)null,
        };
        return data?.ToJsonString(Options);
    }

    private static JsonObject Thing(string type, string name, string? description, string url, string language)
    {
        var data = new JsonObject
        {
            ["@context"] = "https://schema.org",
            ["@type"] = type,
            ["name"] = name,
            ["url"] = url,
            ["inLanguage"] = language,
        };
        if (description is not null)
        {
            data["description"] = description;
        }

        return data;
    }

    private static JsonObject Post(SiteSettings site, ContentItem post, string url, string imageUrl)
    {
        var data = new JsonObject
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "BlogPosting",
            ["headline"] = post.Title,
            ["url"] = url,
            ["mainEntityOfPage"] = url,
            ["image"] = imageUrl,
            ["inLanguage"] = post.Language,
            ["wordCount"] = post.PlainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
        };
        if (post.Description is { } description)
        {
            data["description"] = description;
        }

        if (post.Date is { } date)
        {
            data["datePublished"] = ContentUrls.Timestamp(date);
            data["dateModified"] = ContentUrls.Timestamp(post.Updated ?? date);
        }

        if (post.Tags.Count > 0)
        {
            data["keywords"] = new JsonArray([.. post.Tags.Select(t => (JsonNode)t)]);
        }

        if (site.FindAuthor(post.AuthorId) is { } author)
        {
            var person = new JsonObject { ["@type"] = "Person", ["name"] = author.Name };
            var links = author.Links.Select(l => l.Address?.ToString()).OfType<string>().ToList();
            if (links.Count > 0)
            {
                person["url"] = links[0];
                person["sameAs"] = new JsonArray([.. links.Select(l => (JsonNode)l)]);
            }

            data["author"] = person;
        }

        return data;
    }
}
