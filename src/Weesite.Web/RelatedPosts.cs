using Weesite.Content;

namespace Weesite.Web;

/// <summary>A post's related posts, as the current catalog has them.</summary>
internal static class RelatedPosts
{
    /// <summary>
    /// Maps related posts from the search index to the current catalog's. After a reload the
    /// index is rebuilt in the background and the previous one answers meanwhile, with the
    /// previous catalog's posts: one since unpublished or archived would be linked, and one
    /// retitled would keep its old title (F-0245).
    /// </summary>
    /// <param name="related">The index's related posts.</param>
    /// <param name="catalog">The current catalog.</param>
    /// <returns>The posts still published, as they are now.</returns>
    public static IReadOnlyList<ContentItem> InCatalog(IEnumerable<ContentItem> related, ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(related);
        ArgumentNullException.ThrowIfNull(catalog);
        return [.. related.Select(post => catalog.FindPost(post.Slug)).OfType<ContentItem>()];
    }
}
