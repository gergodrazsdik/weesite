using System.Globalization;
using System.Net;
using System.Text;
using Weesite.Content;
using Weesite.Web.Admin;
using Weesite.Web.Seo;

namespace Weesite.Web.Visitors;

/// <summary>
/// The comments' hourly digest (ADR 0011): to each moderator, the new comments and edits since the
/// last one that wait for them. Admins (configured and added) get every post's; an author gets their
/// own posts'. The visitors' words go only into the body, escaped in the HTML; the headers hold the
/// site's own subject. Nothing goes to visitors. The link to the Comments page is on
/// <c>Site:BaseUrl</c>, and left out without it: never on the address a visitor's request named.
/// One mail lists at most <see cref="MaxListed"/> comments.
/// </summary>
/// <param name="comments">The comments in memory: the digest reads what waits there, after a cheap
/// check that they're current, rather than the whole table every minute (F-0250).</param>
/// <param name="editors">The editors added in the admin.</param>
/// <param name="admins">The configured admins.</param>
/// <param name="content">The site and its posts.</param>
/// <param name="urls">The site's address, for the link to the Comments page.</param>
internal sealed class CommentsDigest(
    CommentCatalog comments,
    IEditorStore editors,
    ConfiguredAdmins admins,
    ContentCatalogProvider content,
    SiteUrls urls) : IDigest
{
    /// <summary>The most comments one mail lists; it counts the rest.</summary>
    public const int MaxListed = 50;

    /// <inheritdoc />
    public string Name => "comments";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DigestMail>> BuildAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken)
    {
        // One read of the store's version; the comments only when it moved.
        await comments.RefreshAsync(cancellationToken);
        var waiting = comments.WaitingComments
            .Where(c => c.Written > after && c.Written <= until)
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        if (waiting.Count == 0)
        {
            return [];
        }

        var catalog = content.Current;
        var posts = catalog.Posts.Concat(catalog.Unpublished)
            .Where(i => i.Kind == ContentKind.Post)
            .GroupBy(i => i.SourcePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Each moderator once: configured admins, then added editors (an added admin who is also
        // configured is the same person). The configured ones come with the Key Vault secrets (T-0192).
        await admins.WhenLoadedAsync(cancellationToken);
        var moderators = new Dictionary<string, (string Address, string? AuthorId)>(StringComparer.Ordinal);
        foreach (var address in admins.Addresses)
        {
            moderators.TryAdd(ConfiguredAdmins.Normalize(address), (address.Trim(), null));
        }

        foreach (var editor in await editors.ListAsync(cancellationToken))
        {
            moderators.TryAdd(ConfiguredAdmins.Normalize(editor.Email), (editor.Email, editor.Role == EditorRole.Admin ? null : EditorRights.AuthorId(editor.Email)));
        }

        var link = urls.BaseUrl is { } origin ? origin + "/admin/comments" : null;
        var mails = new List<DigestMail>();
        foreach (var (address, authorId) in moderators.Values)
        {
            var theirs = waiting
                .Where(c => authorId is null || (posts.GetValueOrDefault(c.Post) is { } post && string.Equals(post.AuthorId, authorId, StringComparison.Ordinal)))
                .ToList();
            if (theirs.Count > 0)
            {
                mails.Add(Build(catalog.Site, address, theirs, posts, link));
            }
        }

        return mails;
    }

    /// <summary>Builds one moderator's mail.</summary>
    /// <param name="site">The site's settings: its name, and the time zone and clock times are shown in (T-0157).</param>
    /// <param name="recipient">The moderator.</param>
    /// <param name="waiting">Their comments and edits waiting, oldest first.</param>
    /// <param name="posts">The posts by source path, for their titles.</param>
    /// <param name="link">The Comments page's address, or null when it isn't known.</param>
    /// <returns>The mail.</returns>
    internal static DigestMail Build(SiteSettings site, string recipient, IReadOnlyList<Comment> waiting, IReadOnlyDictionary<string, ContentItem> posts, string? link)
    {
        var siteName = site.Name;
        var subject = waiting.Count == 1
            ? $"1 comment waits for moderation on {siteName}"
            : string.Create(CultureInfo.InvariantCulture, $"{waiting.Count} comments wait for moderation on {siteName}");
        var text = new StringBuilder();
        var html = new StringBuilder();
        var intro = waiting.Count == 1
            ? $"A comment on {siteName} waits for you to approve or reject it."
            : string.Create(CultureInfo.InvariantCulture, $"{waiting.Count} comments on {siteName} wait for you to approve or reject them.");
        text.Append(intro).Append("\n\n");
        html.Append("<p>").Append(Encode(intro)).Append("</p>\n");
        foreach (var comment in waiting.Take(MaxListed))
        {
            var title = posts.GetValueOrDefault(comment.Post)?.Title ?? comment.Post;
            var when = site.FormatTime(comment.Written);
            var isEdit = comment.Status == CommentStatus.Approved && comment.PendingText is not null;
            var kind = isEdit ? "An edit of a published comment" : "A new comment";
            text.Append(kind).Append(" on \"").Append(title).Append("\"\n")
                .Append("From: ").Append(comment.Name).Append('\n')
                .Append("Written: ").Append(when).Append("\n\n")
                .Append(isEdit ? comment.PendingText : comment.Text).Append("\n\n---\n\n");
            html.Append("<hr>\n<p>").Append(Encode(kind)).Append(" on <strong>").Append(Encode(title)).Append("</strong><br>\n")
                .Append(Encode(comment.Name)).Append(", ").Append(Encode(when)).Append("</p>\n")
                .Append("<p style=\"white-space: pre-wrap\">").Append(Encode(isEdit ? comment.PendingText! : comment.Text)).Append("</p>\n");
        }

        if (waiting.Count > MaxListed)
        {
            var more = string.Create(CultureInfo.InvariantCulture, $"{waiting.Count - MaxListed} more aren't listed here.");
            text.Append(more).Append("\n\n");
            html.Append("<hr>\n<p>").Append(Encode(more)).Append("</p>\n");
        }

        var footer = waiting.Count == 1 ? "Approve or reject it under Comments in the site's admin" : "Approve or reject them under Comments in the site's admin";
        text.Append(link is null ? footer + "." : $"{footer}: {link}").Append('\n');
        html.Append("<hr>\n<p>").Append(Encode(footer));
        if (link is not null)
        {
            html.Append(": <a href=\"").Append(Encode(link)).Append("\">").Append(Encode(link)).Append("</a>");
        }

        html.Append(".</p>\n");
        return new DigestMail(recipient, subject, text.ToString(), html.ToString());
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}
