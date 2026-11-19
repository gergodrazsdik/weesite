using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>
/// An item's status as the admin lists show it: <c>published</c>, <c>scheduled</c>, <c>draft</c>
/// or <c>archived</c>. A scheduled item whose time has come is published.
/// </summary>
internal static class ItemStatus
{
    /// <summary>Gets an item's status key, also its pill's class.</summary>
    /// <param name="item">The post or page.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The key.</returns>
    public static string Key(ContentItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Status switch
        {
            ContentStatus.Draft => "draft",
            ContentStatus.Archived => "archived",
            ContentStatus.Scheduled when !item.IsPublicAt(now) => "scheduled",
            _ => "published",
        };
    }
}
