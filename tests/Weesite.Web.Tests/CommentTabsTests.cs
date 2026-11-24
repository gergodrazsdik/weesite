using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The admin's Comments tabs after the UI and UX review (U-49): Approved, and approving a rejected comment.</summary>
internal sealed partial class CommentTests
{
    [Test]
    public async Task A_rejected_comment_can_be_approved_and_approved_ones_are_listed_and_removed()
    {
        using var copy = new SiteCopy();
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);
        using var author = Client(site, EditorRole.Author);

        var (changed, _) = await SendAsync(client, site, "Ada", "Rejected by mistake");
        var (kept, _) = await SendAsync(client, site, "Bob", "Approved and listed");
        await Moderate(admin, "reject", changed);
        await Moderate(admin, "approve", kept);

        // The Rejected tab offers Approve; approving publishes it.
        var rejected = await admin.GetStringAsync(new Uri("/admin/comments?tab=rejected", UriKind.Relative));
        await Assert.That(rejected).Contains("Rejected by mistake");
        await Assert.That(rejected).Contains("action=\"/admin/comments/approve\"");
        using var approved = await ModerateOn(admin, "rejected", "approve", changed);
        await Assert.That(approved.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=rejected&done=approved");
        await Assert.That(Stored(site).Single(c => c.Id == changed).Status).IsEqualTo(CommentStatus.Approved);

        // The Approved tab lists both, newest first, with Remove for admins only.
        var list = await admin.GetStringAsync(new Uri("/admin/comments?tab=approved", UriKind.Relative));
        await Assert.That(list).Contains("aria-current=\"page\">Approved <span><span class=\"visually-hidden\">, </span>2</span>");
        await Assert.That(list.IndexOf("Approved and listed", StringComparison.Ordinal)).IsLessThan(list.IndexOf("Rejected by mistake", StringComparison.Ordinal));
        await Assert.That(list).Contains("action=\"/admin/comments/remove\"");
        var authors = await author.GetStringAsync(new Uri("/admin/comments?tab=approved", UriKind.Relative));
        await Assert.That(authors).DoesNotContain("action=\"/admin/comments/remove\"");

        using var removed = await ModerateOn(admin, "approved", "remove", kept);
        await Assert.That(removed.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=approved&done=removed");
        await Assert.That(Stored(site).Single(c => c.Id == kept).Status).IsEqualTo(CommentStatus.Removed);
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "comment-removed" && e.Target == $"{PostPath}#{kept}")).IsTrue();
    }

    [Test]
    public async Task Deleting_a_comment_needs_the_confirm_dialog_s_field()
    {
        // Without JavaScript the dialog never asks, so the post comes without its field: the
        // comment stays, and the tab says why (U-67).
        using var copy = new SiteCopy();
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (id, _) = await SendAsync(client, site, "Ada", "Kept until confirmed");
        using var refused = await ModerateOn(admin, "waiting", "delete", id, confirmed: false);
        var after = await admin.GetStringAsync(refused.Headers.Location);

        await Assert.That(refused.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&unconfirmed=1");
        await Assert.That(Stored(site).Any(c => c.Id == id)).IsTrue();
        await Assert.That(after).Contains("Nothing changed: this asks you first");
        await Assert.That(after).Contains("Kept until confirmed");

        using var deleted = await ModerateOn(admin, "waiting", "delete", id);
        await Assert.That(deleted.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&done=deleted");
        await Assert.That(Stored(site).Any(c => c.Id == id)).IsFalse();
    }

    // A decision from a tab of the admin's Comments page, with the version that tab shows;
    // confirmed, as the dialog does for Delete (U-67), unless asked not to be.
    private static async Task<HttpResponseMessage> ModerateOn(HttpClient admin, string tab, string action, string id, bool confirmed = true)
    {
        var page = await admin.GetStringAsync(new Uri($"/admin/comments?tab={tab}", UriKind.Relative));
        var token = Forms.Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([new("post", PostPath), new("id", id), new("version", VersionOn(page, id)), new("tab", tab), new(Confirmation.Field, confirmed ? "1" : ""), new("__RequestVerificationToken", token)]);
        return await admin.PostAsync(new Uri($"/admin/comments/{action}", UriKind.Relative), form);
    }
}
