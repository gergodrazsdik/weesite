using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content;
using Weesite.Web.Admin;
using Weesite.Web.Analytics;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The site's time zone and clock (T-0157): stored times stay UTC; people see them in the site's zone.</summary>
internal sealed class SiteTimeTests
{
    private static readonly TimeZoneInfo Budapest = SiteTime.Zone("Europe/Budapest");

    [Test]
    public async Task Times_show_in_the_zone_on_its_clock_and_say_UTC_only_in_UTC()
    {
        var at = new DateTimeOffset(2026, 11, 19, 17, 6, 51, TimeSpan.Zero);

        await Assert.That(SiteTime.Format(at, TimeZoneInfo.Utc, twelveHour: false)).IsEqualTo("2026-11-19 17:06 UTC");
        await Assert.That(SiteTime.Format(at, Budapest, twelveHour: false)).IsEqualTo("2026-11-19 18:06");
        await Assert.That(SiteTime.Format(at, Budapest, twelveHour: false, seconds: true)).IsEqualTo("2026-11-19 18:06:51");
        await Assert.That(SiteTime.Format(at, Budapest, twelveHour: true)).IsEqualTo("2026-11-19 6:06 PM");
        await Assert.That(SiteTime.Format(at.AddHours(-12), Budapest, twelveHour: true, seconds: true)).IsEqualTo("2026-11-19 6:06:51 AM");
        await Assert.That(SiteTime.Zone("Mars/Olympus_Mons")).IsEqualTo(TimeZoneInfo.Utc);
        await Assert.That(SiteTime.Zone(null)).IsEqualTo(TimeZoneInfo.Utc);
        await Assert.That(SiteTime.Names()[0]).IsEqualTo("UTC");
        await Assert.That(SiteTime.Names()).Contains("Europe/Budapest");
    }

    [Test]
    public async Task A_day_is_the_zones_day()
    {
        var lateEvening = new DateTimeOffset(2026, 11, 19, 23, 30, 0, TimeSpan.Zero);

        await Assert.That(SiteTime.Day(lateEvening, TimeZoneInfo.Utc)).IsEqualTo(new DateOnly(2026, 11, 19));
        await Assert.That(SiteTime.Day(lateEvening, Budapest)).IsEqualTo(new DateOnly(2026, 11, 20));
    }

    [Test]
    public async Task A_wall_clock_time_is_its_moment_but_not_in_a_daylight_saving_gap()
    {
        var plain = SiteTime.FromLocal(new DateTime(2026, 11, 20, 9, 0, 0), Budapest);
        var gap = SiteTime.FromLocal(new DateTime(2026, 3, 29, 2, 30, 0), Budapest);
        var twice = SiteTime.FromLocal(new DateTime(2026, 10, 25, 2, 30, 0), Budapest);

        await Assert.That(plain?.UtcDateTime).IsEqualTo(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc));
        await Assert.That(gap).IsNull();

        // Clocks go back at 03:00 to 02:00: the first 02:30 is still summer time.
        await Assert.That(twice?.UtcDateTime).IsEqualTo(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task The_publish_time_is_typed_in_the_zone_and_stored_in_UTC()
    {
        var draft = new ContentDraft
        {
            Kind = ContentKind.Post,
            Title = "A",
            Slug = "a",
            Date = new DateOnly(2026, 11, 20),
            Status = ContentStatus.Scheduled,
            PublishAt = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero),
        };

        var form = EditorForm.From(draft, "posts/a.md", "v1", Budapest);
        var back = form.ToDraft("en", out var error, Budapest);
        form.PublishAt = "2026-03-29 02:30";
        var skipped = form.ToDraft("en", out var gapError, Budapest);

        await Assert.That(EditorForm.From(draft, "posts/a.md", "v1").PublishAt).IsEqualTo("2026-11-20 08:00");
        await Assert.That(form.Status).IsEqualTo("scheduled");
        await Assert.That(error).IsNull();
        await Assert.That(back?.PublishAt).IsEqualTo(new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero));
        await Assert.That(skipped).IsNull();
        await Assert.That(gapError).Contains("clocks skip that time");
    }

    [Test]
    public async Task The_first_publish_time_shown_is_in_the_zone()
    {
        var draft = new ContentDraft
        {
            Kind = ContentKind.Post,
            Title = "A",
            Slug = "a",
            Status = ContentStatus.Scheduled,
            PublishAt = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero),
        };

        await Assert.That(EditorForm.From(draft, "posts/a.md", "v1", Budapest).PublishAt).IsEqualTo("2026-11-20 09:00");
    }

    [Test]
    public async Task Analytics_days_are_the_zones_days()
    {
        // 23:30 UTC on the 8th is half past midnight on the 9th in Budapest.
        PageView View(int day, int hour, int minute) => new(
            new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.Zero), "/", null, ClientKind.Browser, null, "v1", null);
        PageView[] views = [View(8, 12, 0), View(8, 23, 30), View(9, 10, 0)];

        var utc = AnalyticsReport.Build(views, new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 9));
        var local = AnalyticsReport.Build(views, new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 9), Budapest);

        await Assert.That(utc.Days.Select(d => d.Views)).IsEquivalentTo([2, 1], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(local.Days.Select(d => d.Views)).IsEquivalentTo([1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_digest_shows_times_in_the_zone()
    {
        var site = SiteSettings.Empty with { Name = "Example", TimeZone = "Europe/Budapest", Clock = SiteTime.Clock12 };
        var at = new DateTimeOffset(2026, 11, 19, 17, 6, 0, TimeSpan.Zero);
        var message = new ContactMessage(ContactMessage.NewId(at), "Ada", "ada@example.org", "Hello.", at, Read: false, Unverified: false);

        var mail = MessagesDigest.Build(site, "owner@example.org", [message], "https://example.org/admin/messages");

        await Assert.That(mail.Text).Contains("Received: 2026-11-19 6:06 PM\n");
        await Assert.That(mail.Text).DoesNotContain("UTC");
        await Assert.That(mail.Text).Contains("Read, keep or delete it under Messages");
    }

    [Test]
    public async Task Settings_save_the_zone_and_clock_and_the_admin_shows_times_in_them()
    {
        using var copy = new SiteCopy();
        await using var root = new SiteFactory(copy.Root);
        await using var factory = root.WithTestSignIn();
        using var admin = Admin(factory);
        var at = new DateTimeOffset(2026, 11, 19, 17, 6, 51, TimeSpan.Zero);
        var message = new ContactMessage(ContactMessage.NewId(at), "Ada", "ada@example.org", "Hello.", at, Read: false, Unverified: false);
        await factory.Services.GetRequiredService<IMessageStore>().AddAsync(message, CancellationToken.None);

        var before = await admin.GetStringAsync(new Uri("/admin/messages", UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var refused = await Save(admin, page, "Mars/Olympus_Mons", "24h");
        using var saved = await Save(admin, page, "Europe/Budapest", "12h");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json")))!;
        var after = await admin.GetStringAsync(new Uri("/admin/messages", UriKind.Relative));
        var settings = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));

        await Assert.That(page).Contains("<option value=\"UTC\" selected>UTC (the default)</option>");
        await Assert.That(before).Contains(">2026-11-19 17:06:51 UTC</time>");
        await Assert.That(await refused.Content.ReadAsStringAsync()).Contains("Choose a time zone from the list.");
        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(json["timeZone"]?.GetValue<string>()).IsEqualTo("Europe/Budapest");
        await Assert.That(json["clock"]?.GetValue<string>()).IsEqualTo("12h");
        await Assert.That(after).Contains(">2026-11-19 6:06:51 PM</time>");
        await Assert.That(settings).Contains("<option value=\"Europe/Budapest\" selected>Europe/Budapest (UTC&#x2B;01:00)</option>");

        using var back = await Save(admin, settings, "UTC", "24h");
        var reset = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json")))!.AsObject();
        await Assert.That(reset.ContainsKey("timeZone")).IsFalse();
        await Assert.That(reset.ContainsKey("clock")).IsFalse();
    }

    [Test]
    public async Task Message_rows_open_the_message_and_each_has_its_delete_button()
    {
        await using var root = new SiteFactory();
        await using var factory = root.WithTestSignIn();
        using var admin = Admin(factory);
        var at = DateTimeOffset.UtcNow;
        var message = new ContactMessage(ContactMessage.NewId(at), "Ada", "ada@example.org", "Hello.", at, Read: false, Unverified: false);
        await factory.Services.GetRequiredService<IMessageStore>().AddAsync(message, CancellationToken.None);

        var list = await admin.GetStringAsync(new Uri("/admin/messages", UriKind.Relative));
        var view = await admin.GetStringAsync(new Uri("/admin/messages/view?id=" + message.Id, UriKind.Relative));

        await Assert.That(list).Contains("<tr class=\"message-row unread\">");
        await Assert.That(list).Contains($"<a class=\"row-link\" href=\"/admin/messages/view?id={message.Id}\">Ada</a>");
        await Assert.That(list).Contains($"<input type=\"hidden\" name=\"id\" value=\"{message.Id}\" />");
        await Assert.That(list).Contains("Delete<span class=\"visually-hidden\"> the message from Ada</span></button>");
        await Assert.That(list).Contains("class=\"btn refresh\" href=\"/admin/messages\"");
        await Assert.That(view).Contains("<a class=\"btn\" href=\"/admin/messages\">Back to messages</a>");
    }

    private static HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", EditorRole.Admin.ToString());
        return client;
    }

    private static async Task<HttpResponseMessage> Save(HttpClient client, string page, string zone, string clock)
    {
        var values = Forms.Values(page, "settings").Where(f => f.Key is not ("Form.TimeZone" or "Form.Clock")).ToList();
        values.Add(new("Form.TimeZone", zone));
        values.Add(new("Form.Clock", clock));
        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(new Uri("/admin/settings", UriKind.Relative), form);
    }
}
