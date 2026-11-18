using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;
using Weesite.Web.Seo;

namespace Weesite.Web.Tests;

/// <summary>Scheduled posts become published when their time comes (T-0147).</summary>
internal sealed class ScheduledPublishingTests
{
    private const string Due = "---\ntitle: Due now\nslug: due-now\ndate: 2026-01-01\nstatus: scheduled\npublish_at: 2026-01-01T08:00:00Z\n---\n\nIts time has come.\n";

    [Test]
    public async Task A_due_item_is_published_audited_and_sent_and_a_future_one_waits()
    {
        using var copy = Copy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = Configured(site);
        var publisher = factory.Services.GetRequiredService<ScheduledPublishing>();
        var queue = factory.Services.GetRequiredService<IndexNowQueue>();

        var published = await publisher.PublishDueAsync(CancellationToken.None);
        var text = await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "due-now.md"));
        var future = await File.ReadAllTextAsync(Path.Combine(copy.Root, "content", "posts", "a-post-from-the-future.md"));
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);

        await Assert.That(published).IsEqualTo(1);
        // Published is the default, which the editor leaves out.
        await Assert.That(text).DoesNotContain("status:");
        await Assert.That(text).DoesNotContain("publish_at");
        await Assert.That(future).Contains("status: scheduled");
        await Assert.That(audit.Select(e => $"{e.Actor} {e.Action} {e.Target}")).IsEquivalentTo(["schedule status-published posts/due-now.md"]);
        await Assert.That(queue.Reader.TryRead(out var sent)).IsTrue();
        await Assert.That(sent!.Url).IsEqualTo("https://example.org/blog/due-now");
        await Assert.That(await publisher.PublishDueAsync(CancellationToken.None)).IsEqualTo(0);
    }

    [Test]
    public async Task Two_at_once_publish_it_once()
    {
        // Two replicas noticing the same item: the version check lets one save.
        using var copy = Copy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = Configured(site);
        var publisher = factory.Services.GetRequiredService<ScheduledPublishing>();

        var counts = await Task.WhenAll(publisher.PublishDueAsync(CancellationToken.None), publisher.PublishDueAsync(CancellationToken.None));
        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);

        await Assert.That(counts.Sum()).IsEqualTo(1);
        await Assert.That(audit.Count(e => e.Action == "status-published")).IsEqualTo(1);
    }

    // The sample site with a post whose time has come, and IndexNow on.
    private static SiteCopy Copy()
    {
        var copy = new SiteCopy();
        File.WriteAllText(Path.Combine(copy.Root, "content", "posts", "due-now.md"), Due);
        var file = Path.Combine(copy.Root, "settings", "site.json");
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["indexNow"] = new JsonObject { ["enabled"] = true, ["key"] = "indexnow-0123456789abcdef0123456789abcdef" };
        File.WriteAllText(file, json.ToJsonString());
        return copy;
    }

    // No timer (the test calls it), Site:BaseUrl for addresses outside a request, and IndexNow
    // holding what it would send.
    private static WebApplicationFactory<Program> Configured(SiteFactory site) =>
        site.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Content:ReloadSeconds", "0");
            builder.UseSetting("Site:BaseUrl", "https://example.org");
            builder.UseSetting("IndexNow:BatchSeconds", "3600");
        });
}
