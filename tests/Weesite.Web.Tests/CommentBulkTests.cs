using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>Several waiting comments approved or rejected at once from the Comments page (T-0184).</summary>
internal sealed partial class CommentTests
{
    [Test]
    public async Task Several_waiting_comments_are_approved_or_rejected_together()
    {
        using var copy = new SiteCopy();
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (first, _) = await SendAsync(client, site, "Ada", "First of a flood");
        var (second, _) = await SendAsync(client, site, "Bob", "Second of a flood");
        var (third, _) = await SendAsync(client, site, "Cy", "Kept waiting");
        var page = await admin.GetStringAsync(new Uri("/admin/comments?tab=waiting", UriKind.Relative));

        // A checkbox per waiting comment, tied to the bulk form, each with its own name.
        await Assert.That(page).Contains("<form id=\"bulk-moderation\"");
        await Assert.That(Selections(page)).Count().IsEqualTo(3);
        await Assert.That(page).Contains("Select the comment by Ada");

        using var approved = await BulkAsync(admin, page, "approve", first, second);
        var after = await admin.GetStringAsync(approved.Headers.Location);

        await Assert.That(approved.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&done=bulk-approved&n=2&left=0");
        await Assert.That(Stored(site).Single(c => c.Id == first).Status).IsEqualTo(CommentStatus.Approved);
        await Assert.That(Stored(site).Single(c => c.Id == second).Status).IsEqualTo(CommentStatus.Approved);
        await Assert.That(Stored(site).Single(c => c.Id == third).Status).IsEqualTo(CommentStatus.Pending);
        await Assert.That(after).Contains("Approved: 2.");
        await Assert.That(after).DoesNotContain("Left alone");

        // One waiting comment left: nothing to do several at once, so no bulk form.
        await Assert.That(after).DoesNotContain("bulk-moderation");
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        await Assert.That(audit.Count(e => e.Action == "comment-approved")).IsEqualTo(2);
    }

    [Test]
    public async Task A_comment_that_changed_since_the_page_is_left_alone_in_a_bulk_action()
    {
        using var copy = new SiteCopy();
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (decided, _) = await SendAsync(client, site, "Ada", "Decided by someone else");
        var (waiting, _) = await SendAsync(client, site, "Bob", "Still waiting");
        var page = await admin.GetStringAsync(new Uri("/admin/comments?tab=waiting", UriKind.Relative));

        // Another moderator approves one after this page was shown (F-0209).
        await Moderate(admin, "approve", decided);
        using var rejected = await BulkAsync(admin, page, "reject", decided, waiting);
        var after = await admin.GetStringAsync(rejected.Headers.Location);

        await Assert.That(rejected.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&done=bulk-rejected&n=1&left=1");
        await Assert.That(Stored(site).Single(c => c.Id == decided).Status).IsEqualTo(CommentStatus.Approved);
        await Assert.That(Stored(site).Single(c => c.Id == waiting).Status).IsEqualTo(CommentStatus.Rejected);
        await Assert.That(after).Contains("Rejected: 1.");
        await Assert.That(after).Contains("Left alone, as they changed since the page showed them or are gone: 1.");
    }

    [Test]
    public async Task A_bulk_action_needs_a_choice_and_ticked_comments()
    {
        using var copy = new SiteCopy();
        await using var root = new SiteFactory(copy.Root);
        await using var site = Site(root);
        using var client = site.CreateClient();
        using var admin = Admin(site);

        var (one, _) = await SendAsync(client, site, "Ada", "One");
        var (two, _) = await SendAsync(client, site, "Bob", "Two");
        var page = await admin.GetStringAsync(new Uri("/admin/comments?tab=waiting", UriKind.Relative));

        using var none = await BulkAsync(admin, page, "approve");
        using var unknown = await BulkAsync(admin, page, "delete", one, two);
        using var forged = await PostBulkAsync(admin, page, "approve", values: ["not-an-id|0|" + PostPath, "|" + PostPath]);
        var afterNone = await admin.GetStringAsync(none.Headers.Location);

        await Assert.That(none.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&done=bulk-none");
        await Assert.That(afterNone).Contains("Tick the comments to approve or reject first.");
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(forged.Headers.Location?.ToString()).IsEqualTo("/admin/comments?tab=waiting&done=bulk-none");
        await Assert.That(Stored(site).All(c => c.Status == CommentStatus.Pending)).IsTrue();
    }

    // The bulk form's post, with the values the page shows for the comments given (or values as given).
    private static async Task<HttpResponseMessage> BulkAsync(HttpClient admin, string page, string choice, params string[] ids) =>
        await PostBulkAsync(admin, page, choice, [.. Selections(page).Where(v => ids.Contains(v.Split('|')[0]))]);

    private static async Task<HttpResponseMessage> PostBulkAsync(HttpClient admin, string page, string choice, IEnumerable<string> values)
    {
        var token = Forms.Token().Match(page).Groups[1].Value;
        List<KeyValuePair<string, string>> fields = [new("do", choice), new("tab", "waiting"), new("__RequestVerificationToken", token)];
        fields.AddRange(values.Select(v => new KeyValuePair<string, string>("selected", v)));
        using var form = new FormUrlEncodedContent(fields);
        return await admin.PostAsync(new Uri("/admin/comments/bulk", UriKind.Relative), form);
    }

    // The values of the page's bulk checkboxes, read without a regex (the page is long).
    private static List<string> Selections(string page)
    {
        const string Marker = "form=\"bulk-moderation\" name=\"selected\" value=\"";
        List<string> values = [];
        for (var at = page.IndexOf(Marker, StringComparison.Ordinal); at >= 0; at = page.IndexOf(Marker, at, StringComparison.Ordinal))
        {
            at += Marker.Length;
            values.Add(page.Substring(at, page.AsSpan(at).IndexOf('"')));
        }

        return values;
    }
}
