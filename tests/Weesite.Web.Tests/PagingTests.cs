using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The admin's long lists in pages (T-0131).</summary>
internal sealed partial class PagingTests
{
    [Test]
    [Arguments(null, 60, 25, 1)]
    [Arguments("2", 60, 25, 2)]
    [Arguments("3", 60, 25, 3)]
    [Arguments("99", 60, 25, 3)]
    [Arguments("0", 60, 25, 1)]
    [Arguments("abc", 60, 25, 1)]
    [Arguments("-2", 60, 25, 1)]
    [Arguments("2", 0, 25, 1)]
    public async Task The_page_asked_for_is_a_page_the_list_has(string? page, int total, int size, int expected) =>
        await Assert.That(Paging.Current(page, total, size)).IsEqualTo(expected);

    [Test]
    public async Task A_page_keeps_the_lists_address_and_page_one_has_none()
    {
        await Assert.That(Paging.With("/admin/posts", 1)).IsEqualTo("/admin/posts");
        await Assert.That(Paging.With("/admin/posts", 2)).IsEqualTo("/admin/posts?page=2");
        await Assert.That(Paging.With("/admin/posts?q=x&status=draft", 3)).IsEqualTo("/admin/posts?q=x&status=draft&page=3");
    }

    [Test]
    public async Task Posts_come_25_a_page_with_the_filter_and_tab_kept()
    {
        using var copy = new SiteCopy();
        for (var i = 1; i <= 30; i++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(copy.Root, "content", "posts", $"paged-{i:D2}.md"),
                $"---\ntitle: Paged {i:D2}\nslug: paged-{i:D2}\ndate: 2020-01-{(i % 28) + 1:D2}\nstatus: draft\n---\nText.\n");
        }

        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);

        var first = await admin.GetStringAsync(new Uri("/admin/posts?status=draft", UriKind.Relative));
        var second = await admin.GetStringAsync(new Uri("/admin/posts?status=draft&page=2", UriKind.Relative));
        var past = await admin.GetStringAsync(new Uri("/admin/posts?status=draft&page=99", UriKind.Relative));
        var drafts = int.Parse(Regex.Match(first, "Drafts <span>(\\d+)</span>").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        await Assert.That(Rows(first)).IsEqualTo(25);
        await Assert.That(Rows(second)).IsEqualTo(drafts - 25);
        await Assert.That(first).Contains($"<span class=\"pager-count\">1–25 of {drafts}</span>");
        await Assert.That(first).Contains("<a class=\"btn small\" href=\"/admin/posts?status=draft&amp;page=2\" rel=\"next\">Next</a>");
        await Assert.That(second).Contains("<a class=\"btn small\" href=\"/admin/posts?status=draft\" rel=\"prev\">Previous</a>");
        await Assert.That(second).Contains("aria-current=\"page\" aria-label=\"Page 2\">2</a>");
        await Assert.That(past).Contains("aria-current=\"page\" aria-label=\"Page 2\">2</a>");
    }

    [Test]
    public async Task The_media_list_comes_24_a_page()
    {
        using var copy = new SiteCopy();
        var media = Path.Combine(copy.Root, "media");
        Directory.CreateDirectory(media);
        for (var i = 0; i < 30; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(media, $"abcdef0123{i:D2}-notes.pdf"), "%PDF-1.4\n%%EOF\n"u8.ToArray());
        }

        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);

        var first = await admin.GetStringAsync(new Uri("/admin/media?q=notes", UriKind.Relative));
        var second = await admin.GetStringAsync(new Uri("/admin/media?q=notes&page=2", UriKind.Relative));

        await Assert.That(Regex.Count(first, "class=\"name\" href=")).IsEqualTo(24);
        await Assert.That(Regex.Count(second, "class=\"name\" href=")).IsEqualTo(6);
        await Assert.That(first).Contains("1–24 of 30");

        // Archive and Delete come back to the same page.
        await Assert.That(second).Contains("back=%2Fadmin%2Fmedia%3Fq%3Dnotes%26page%3D2");
    }

    [Test]
    public async Task The_audit_log_is_filtered_and_paged()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);
        var audit = factory.Services.GetRequiredService<IAuditLog>();
        for (var i = 0; i < 60; i++)
        {
            await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow.AddMinutes(-i), $"writer{i % 2}@example.com", "item-saved", $"posts/p{i}.md"), CancellationToken.None);
        }

        var first = await admin.GetStringAsync(new Uri("/admin/audit", UriKind.Relative));
        var filtered = await admin.GetStringAsync(new Uri("/admin/audit?q=writer1", UriKind.Relative));

        await Assert.That(first).Contains("1–50 of 60");
        await Assert.That(first).Contains("href=\"/admin/audit?page=2\" rel=\"next\"");
        await Assert.That(Regex.Count(filtered, "<td>writer1@example.com</td>")).IsEqualTo(30);
        await Assert.That(filtered).DoesNotContain("writer0@example.com");
        await Assert.That(filtered).DoesNotContain("rel=\"next\"");
    }

    [Test]
    public async Task Saving_a_page_of_tags_keeps_the_others()
    {
        using var copy = new SiteCopy();
        var tags = string.Join(", ", Enumerable.Range(1, 35).Select(i => $"t{i:D2}"));
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "tagged.md"), $"---\ntitle: Tagged\nslug: tagged\ndate: 2026-01-01\ntags: [{tags}]\n---\nText.\n");
        var settings = Path.Combine(copy.Root, "settings", "site.json");
        // The seed's site.json has tags already: add one to them rather than a second key.
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(settings))!.AsObject();
        var stored = json["tags"] as System.Text.Json.Nodes.JsonObject ?? [];
        stored["t01"] = new System.Text.Json.Nodes.JsonObject { ["colour"] = "teal" };
        json["tags"] = stored;
        await File.WriteAllTextAsync(settings, json.ToJsonString());
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Client(factory);

        var all = factory.Services.GetRequiredService<Weesite.Content.ContentCatalogProvider>().Current.Tags.Keys.Order(StringComparer.Ordinal).ToList();
        var page = await admin.GetStringAsync(new Uri("/admin/tags?page=2", UriKind.Relative));
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "tags",
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value,
            ["Form.ETag"] = WebUtility.HtmlDecode(Regex.Match(page, "name=\"Form.ETag\" value=\"([^\"]*)\"").Groups[1].Value),
        };
        foreach (Match row in Regex.Matches(page, "name=\"Form.Tags\\[(\\d+)\\].Tag\" value=\"([^\"]*)\""))
        {
            fields[$"Form.Tags[{row.Groups[1].Value}].Tag"] = row.Groups[2].Value;
        }

        fields["Form.Tags[0].Description"] = "On page two.";
        using var form = new FormUrlEncodedContent(fields);
        using var saved = await admin.PostAsync(new Uri("/admin/tags?page=2", UriKind.Relative), form);
        var after = await File.ReadAllTextAsync(settings);

        await Assert.That(page).Contains($"name=\"Form.Tags[0].Tag\" value=\"{all[30]}\"");
        await Assert.That(saved.Headers.Location?.PathAndQuery).IsEqualTo("/admin/tags?saved=1&page=2");
        await Assert.That(after).Contains("On page two.");
        await Assert.That(after).Contains("\"t01\"");
        await Assert.That(after).Contains("teal");
    }

    private static int Rows(string html) => Regex.Count(html, "<tr><th scope=\"row\">");

    private static HttpClient Client(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));
        return client;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
