namespace Weesite.Web.Seo;

/// <summary>
/// The <c>X-Robots-Tag</c> header, for answers search engines should crawl but not list: the
/// feeds and media files that download, which Search Console otherwise reports as "crawled, not
/// indexed", or lists as results of their own (F-0260). Links in them are still followed.
/// </summary>
internal static class RobotsTag
{
    /// <summary>The header's name.</summary>
    public const string Header = "X-Robots-Tag";

    /// <summary>
    /// Says <c>noindex</c>, unless the answer already has the header: a site with
    /// <c>Site:NoIndex</c> keeps its <c>noindex, nofollow</c>.
    /// </summary>
    /// <param name="response">The answer.</param>
    public static void NoIndex(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.TryAdd(Header, "noindex");
    }
}
