using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weesite.Content;
using Weesite.Tests;
using Weesite.Web.Admin;
using Weesite.Web.Email;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The contact form, its messages, the admin's Messages page and the hourly digest (ADR 0011).</summary>
internal sealed partial class ContactFormTests
{
    private const string Address = "203.0.113.77";

    [Test]
    public async Task A_message_sent_without_javascript_is_kept_unverified()
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root);
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var sent = await Send(client, page, ("Name", "Ada Lovelace"), ("Email", "ada@example.org"), ("Message", "Hello!\r\nA question about a post."));
        var thanks = await client.GetStringAsync(sent.Headers.Location);

        var kept = Messages(site).Single();
        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(sent.Headers.Location?.PathAndQuery).IsEqualTo("/contact?sent=1");
        await Assert.That(thanks).Contains("Thank you: your message is on its way.");
        await Assert.That(kept.Name).IsEqualTo("Ada Lovelace");
        await Assert.That(kept.Email).IsEqualTo("ada@example.org");
        await Assert.That(kept.Text).IsEqualTo("Hello!\nA question about a post.");
        await Assert.That(kept.Unverified).IsTrue();
        await Assert.That(kept.Read).IsFalse();
    }

    [Test]
    public async Task A_message_with_a_solved_challenge_is_verified()
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root);
        using var client = NoRedirects(site);
        var answer = ProofOfWork.Solve(site.Services.GetRequiredService<ProofOfWork>().Create("contact"))!;

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var sent = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", "Verified."), ("proof", answer));

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(Messages(site).Single().Unverified).IsFalse();
    }

    [Test]
    public async Task A_filled_honeypot_looks_sent_but_nothing_is_kept()
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root);
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var sent = await Send(client, page, ("Name", "Bot"), ("Email", "bot@example.org"), ("Message", "Cheap pills."), ("website", "https://spam.example"));

        await Assert.That(sent.Headers.Location?.PathAndQuery).IsEqualTo("/contact?sent=1");
        await Assert.That(Messages(site)).IsEmpty();
    }

    [Test]
    public async Task The_honeypot_is_hidden_from_everyone_but_bots()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));

        await Assert.That(page).Contains("<div class=\"field contact-extra\">");
        await Assert.That(HoneypotInput().Match(page).Value).Contains("tabindex=\"-1\"").And.Contains("autocomplete=\"off\"");
        await Assert.That(page).Contains("name=\"proof\" value />");
    }

    [Test]
    public async Task A_form_sent_too_soon_keeps_the_text_and_asks_to_send_again()
    {
        await using var site = new SiteFactory();
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var sent = await Send(client, page, ("Name", "Speedy"), ("Email", "fast@example.org"), ("Message", "Typed very fast."));
        var again = await sent.Content.ReadAsStringAsync();

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(WebUtility.HtmlDecode(again)).Contains("That was quicker than anyone types.");
        await Assert.That(again).Contains("role=\"alert\"");
        await Assert.That(again).Contains(">Typed very fast.</textarea>");
        await Assert.That(Messages(site)).IsEmpty();
    }

    [Test]
    public async Task A_form_shown_too_long_ago_or_without_a_token_is_refused()
    {
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        await using var site = Fast(root, b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        clock.Advance(TimeSpan.FromHours(25));
        using var late = await Send(client, page, ("Name", "Slow"), ("Email", "slow@example.org"), ("Message", "Left the tab open."));
        using var forged = await Send(client, page, ("Name", "Forger"), ("Email", "f@example.org"), ("Message", "No token."), ("form-token", "forged"));

        await Assert.That(WebUtility.HtmlDecode(await late.Content.ReadAsStringAsync())).Contains("This page was open for a long time.");
        await Assert.That(WebUtility.HtmlDecode(await forged.Content.ReadAsStringAsync())).Contains("The form couldn't be read.");
        await Assert.That(Messages(site)).IsEmpty();
    }

    [Test]
    [Arguments("", "ada@example.org", "Hello", "Enter your name.", "contact-name")]
    [Arguments("Ada https://spam.example", "ada@example.org", "Hello", "Enter your name as plain text, without links or HTML.", "contact-name")]
    [Arguments("Ada", "not an address", "Hello", "Enter an email address, such as name@example.com.", "contact-email")]
    [Arguments("Ada", "ada@example.org", "   ", "Write a message.", "contact-message")]
    [Arguments("Ada", "ada@example.org", "Buy <a href=\"https://spam.example\">this</a>", "Write the message as plain text: HTML links, images and scripts and [url] or [img] codes aren't allowed.", "contact-message")]
    [Arguments("Ada", "ada@example.org", "[url=https://spam.example]this[/url]", "Write the message as plain text", "contact-message")]
    [Arguments("Ada", "ada@example.org", "https://a.example https://b.example https://c.example https://d.example", "The message can have at most 3 links.", "contact-message")]
    public async Task Text_that_breaks_the_rules_is_refused_with_the_field_named(string name, string email, string message, string error, string field)
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root);
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var sent = await Send(client, page, ("Name", name), ("Email", email), ("Message", message));
        var again = WebUtility.HtmlDecode(await sent.Content.ReadAsStringAsync());

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(again).Contains(error);
        await Assert.That(again).Contains($"<a href=\"#{field}\">Go to the field</a>");
        await Assert.That(Regex.Match(again, $"<[^>]*id=\"{field}\"[^>]*>").Value).Contains("aria-invalid=\"true\"");
        await Assert.That(Messages(site)).IsEmpty();
    }

    [Test]
    public async Task A_message_over_1000_characters_is_refused()
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root);
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var sent = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", new string('a', 1001)));

        await Assert.That(WebUtility.HtmlDecode(await sent.Content.ReadAsStringAsync())).Contains("The message can be at most 1000 characters.");
    }

    [Test]
    public async Task Messages_are_limited_per_address_and_hour()
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root, b => b.UseSetting("Visitor:ContactPerHour", "2"));
        using var client = NoRedirects(site);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
            using var sent = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", $"Message {i}"));
            statuses.Add(sent.StatusCode);
            if (i == 2)
            {
                await Assert.That(WebUtility.HtmlDecode(await sent.Content.ReadAsStringAsync())).Contains("Too many messages from your network in the last hour.");
            }
        }

        var other = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var fromElsewhere = await Send(client, other, "198.51.100.9", ("Name", "Bob"), ("Email", "bob@example.org"), ("Message", "Hi"));

        await Assert.That(statuses).IsEquivalentTo([HttpStatusCode.Redirect, HttpStatusCode.Redirect, HttpStatusCode.OK], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(fromElsewhere.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(Messages(site).Count).IsEqualTo(3);
    }

    [Test]
    public async Task The_address_never_reaches_the_logs_or_the_stored_rows()
    {
        using var log = new LogLines();
        await using var root = new SiteFactory();
        await using var site = Fast(root, b => b.ConfigureLogging(l => l.AddProvider(log).SetMinimumLevel(LogLevel.Trace)));
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var sent = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", "Hello."));
        using var refused = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", "<a href=\"https://spam.example\">Hello</a>"));

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(log.Lines.Any(l => l.Contains("Contact form: a message arrived", StringComparison.Ordinal))).IsTrue();
        await Assert.That(log.Lines.Where(l => l.Contains(Address, StringComparison.Ordinal))).IsEmpty();
        await Assert.That(site.Services.GetRequiredService<MemoryVisitorStore>().Keys.Where(k => k.Contains(Address, StringComparison.Ordinal))).IsEmpty();
        await Assert.That(Messages(site).Select(m => m.ToString()).Where(m => m.Contains(Address, StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task The_page_is_never_cached_and_never_shows_the_recipient()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        using var response = await client.GetAsync(new Uri("/contact", UriKind.Relative));
        var page = await response.Content.ReadAsStringAsync();

        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(page).DoesNotContain("owner@example.com");
        await Assert.That(page).Contains("<h1 id=\"page-title\" class=\"page-title\">Contact</h1>");
        await Assert.That(page).Contains("<label for=\"contact-name\">Your name</label>");
        await Assert.That(page).Contains("autocomplete=\"email\"");
    }

    [Test]
    public async Task Without_the_form_contact_is_its_special_page_or_nothing()
    {
        // The form off, its text is the page at /contact (T-0159).
        using var copy = new SiteCopy();
        Rewrite(copy, json => json["contactForm"] = new JsonObject { ["enabled"] = false, ["page"] = "pages/contact.md" });
        await using var withPage = new SiteFactory(copy.Root);
        using var client = withPage.CreateClient();
        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));

        File.Delete(Path.Combine(copy.Root, "content", "pages", "contact.md"));
        await using var without = new SiteFactory(copy.Root);
        using var other = without.CreateClient();
        using var missing = await other.GetAsync(new Uri("/contact", UriKind.Relative));

        await Assert.That(page).Contains("Questions about a post");
        await Assert.That(page).DoesNotContain("name=\"form-token\"");
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task A_post_to_contact_counts_against_the_visitor_limit()
    {
        var post = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        post.Request.Method = "POST";
        post.Request.Path = "/contact";
        var get = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        get.Request.Path = "/contact";

        await Assert.That(RateLimits.Kind(post)).IsEqualTo("visitor");
        await Assert.That(RateLimits.Kind(get)).IsNull();
    }

    [Test]
    [Arguments(true, "", "", "Enter the address the contact form's messages go to, or turn the form off.")]
    [Arguments(true, "", "nope", "The address messages go to isn't an email address.")]
    [Arguments(true, "pages/about.md", "owner@example.com", "Choose a special page (a page without a slug) to show above the contact form, or none.")]
    [Arguments(false, "pages/no-such-page.md", "", "Choose a special page (a page without a slug) to show above the contact form, or none.")]
    public async Task Settings_check_the_contact_form(bool enabled, string page, string recipient, string error)
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Admin(factory);

        var form = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var saved = await SaveSettings(admin, form, enabled, page, recipient);

        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(WebUtility.HtmlDecode(await saved.Content.ReadAsStringAsync())).Contains(error);
    }

    [Test]
    public async Task Settings_save_the_contact_form_and_turning_it_off_keeps_the_rest()
    {
        using var copy = new SiteCopy();
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = Admin(factory);

        var form = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        await Assert.That(form).Contains("value=\"owner@example.com\"");
        using var saved = await SaveSettings(admin, form, true, "pages/contact.md", "inbox@example.org");
        var on = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json")))!["contactForm"]!;
        var again = await admin.GetStringAsync(new Uri("/admin/settings", UriKind.Relative));
        using var off = await SaveSettings(admin, again, false, "pages/contact.md", "inbox@example.org");
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(copy.Root, "settings", "site.json")))!["contactForm"]!;

        await Assert.That(saved.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(on["enabled"]!.GetValue<bool>()).IsTrue();
        await Assert.That(on["recipient"]!.GetValue<string>()).IsEqualTo("inbox@example.org");
        await Assert.That(off.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(stored["enabled"]!.GetValue<bool>()).IsFalse();
        await Assert.That(stored["page"]!.GetValue<string>()).IsEqualTo("pages/contact.md");
    }

    [Test]
    public async Task Admins_read_and_delete_messages_and_authors_cant()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        var store = factory.Services.GetRequiredService<IMessageStore>();
        var now = DateTimeOffset.UtcNow;
        var first = new ContactMessage(ContactMessage.NewId(now.AddMinutes(-5)), "Ada", "ada@example.org", "First <b>message</b>.", now.AddMinutes(-5), Read: false, Unverified: false);
        var second = new ContactMessage(ContactMessage.NewId(now), "Bob", "bob@example.org", "Second message.", now, Read: false, Unverified: true);
        await store.AddAsync(first, CancellationToken.None);
        await store.AddAsync(second, CancellationToken.None);
        using var admin = Admin(factory);
        using var author = Client(factory, EditorRole.Author);

        var list = await admin.GetStringAsync(new Uri("/admin/messages", UriKind.Relative));
        using var authorList = await author.GetAsync(new Uri("/admin/messages", UriKind.Relative));
        var view = await admin.GetStringAsync(new Uri("/admin/messages/view?id=" + first.Id, UriKind.Relative));
        var afterView = await admin.GetStringAsync(new Uri("/admin/messages", UriKind.Relative));

        await Assert.That(list.IndexOf("Bob", StringComparison.Ordinal)).IsLessThan(list.IndexOf("Ada", StringComparison.Ordinal));
        await Assert.That(list).Contains("unverified");
        await Assert.That(list).Contains("<span class=\"visually-hidden\">, </span>2<span class=\"visually-hidden\"> unread</span>");
        await Assert.That(authorList.StatusCode).IsNotEqualTo(HttpStatusCode.OK);
        await Assert.That(view).Contains("First &lt;b&gt;message&lt;/b&gt;.");
        await Assert.That(view).Contains("href=\"mailto:ada@example.org\"");
        await Assert.That((await store.GetAsync(first.Id, CancellationToken.None))!.Read).IsTrue();
        await Assert.That(afterView).Contains("<span class=\"visually-hidden\">, </span>1<span class=\"visually-hidden\"> unread</span>");

        using var deleted = await PostAdmin(admin, view, "/admin/messages/delete", ("id", first.Id));
        await Assert.That(deleted.Headers.Location?.OriginalString).IsEqualTo("/admin/messages?deleted=1");
        await Assert.That(await store.GetAsync(first.Id, CancellationToken.None)).IsNull();

        await store.MarkReadAsync(second.Id, CancellationToken.None);
        using var deletedRead = await PostAdmin(admin, view, "/admin/messages/delete-read", ("scope", "read"));
        await Assert.That(deletedRead.Headers.Location?.OriginalString).IsEqualTo("/admin/messages?deletedRead=1");
        await Assert.That((await store.ListAsync(0, 10, CancellationToken.None)).Total).IsEqualTo(0);

        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(10, CancellationToken.None);
        await Assert.That(audit.Select(e => e.Action)).Contains("message-deleted").And.Contains("messages-deleted-read");
        await Assert.That(audit.Where(e => (e.Target ?? "").Contains("message", StringComparison.OrdinalIgnoreCase))).IsEmpty();
    }

    [Test]
    public async Task Deleting_needs_the_antiforgery_token_and_an_admin()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var admin = Admin(factory);
        using var author = Client(factory, EditorRole.Author);
        var page = await admin.GetStringAsync(new Uri("/admin/messages", UriKind.Relative));

        using var form = new FormUrlEncodedContent([new("id", "x")]);
        using var noToken = await admin.PostAsync(new Uri("/admin/messages/delete", UriKind.Relative), form);
        using var byAuthor = await PostAdmin(author, page, "/admin/messages/delete", ("id", "x"));

        await Assert.That(noToken.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(byAuthor.Headers.Location?.OriginalString ?? "").DoesNotContain("deleted=1");
    }

    [Test]
    public async Task The_digest_sends_the_first_message_at_once_then_waits_for_the_hour()
    {
        var clock = new OAuthTests.TestClock();
        var mail = new RecordingSender();
        await using var root = new SiteFactory();
        await using var site = root.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<IEmailSender>(mail);
        }));
        var store = site.Services.GetRequiredService<IMessageStore>();
        using var digest = Sender(site, mail, clock);

        await Add(store, clock, "Ada <b>", "ada@example.org", "First & <i>only</i>.", unverified: true);
        clock.Advance(TimeSpan.FromSeconds(30));
        var first = await digest.RunOnceAsync(CancellationToken.None);

        await Add(store, clock, "Bob", "bob@example.org", "Second.", unverified: false);
        clock.Advance(TimeSpan.FromMinutes(10));
        var tooSoon = await digest.RunOnceAsync(CancellationToken.None);
        clock.Advance(DigestSender.Interval);
        var later = await digest.RunOnceAsync(CancellationToken.None);
        clock.Advance(DigestSender.Interval);
        var quiet = await digest.RunOnceAsync(CancellationToken.None);

        await Assert.That((first, tooSoon, later, quiet)).IsEqualTo((1, 0, 1, 0));
        var one = mail.Sent[0];
        await Assert.That(one.To).IsEqualTo("owner@example.com");
        await Assert.That(one.Subject).IsEqualTo("1 new message on Example Site");
        await Assert.That(one.FromName).IsEqualTo("Example Site");
        await Assert.That(one.Text).Contains("From: Ada <b> (ada@example.org)").And.Contains("First & <i>only</i>.").And.Contains("Unverified");
        await Assert.That(one.Html).Contains("Ada &lt;b&gt;").And.Contains("First &amp; &lt;i&gt;only&lt;/i&gt;.").And.Contains("<a href=\"mailto:ada@example.org\">");
        await Assert.That(one.Html).DoesNotContain("<i>only");
        await Assert.That(mail.Sent[1].Text).Contains("Second.").And.DoesNotContain("First");
    }

    [Test]
    public async Task Two_replicas_send_one_digest_and_a_failed_send_is_tried_again()
    {
        var clock = new OAuthTests.TestClock();
        var mail = new RecordingSender();
        await using var root = new SiteFactory();
        await using var site = root.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<IEmailSender>(mail);
        }));
        var store = site.Services.GetRequiredService<IMessageStore>();
        var states = new MemoryDigestStateStore();
        using var one = Sender(site, mail, clock, states);
        using var two = Sender(site, mail, clock, states);
        await Add(store, clock, "Ada", "ada@example.org", "Hello.", unverified: false);
        clock.Advance(TimeSpan.FromMinutes(1));

        var sent = await Task.WhenAll(one.RunOnceAsync(CancellationToken.None), two.RunOnceAsync(CancellationToken.None));
        await Assert.That(sent.Sum()).IsEqualTo(1);

        await Add(store, clock, "Bob", "bob@example.org", "Again.", unverified: false);
        clock.Advance(DigestSender.Interval);
        mail.Fail = true;
        var failed = await one.RunOnceAsync(CancellationToken.None);
        mail.Fail = false;
        clock.Advance(DigestSender.Tick);
        var retried = await two.RunOnceAsync(CancellationToken.None);

        await Assert.That(failed).IsEqualTo(0);
        await Assert.That(retried).IsEqualTo(1);
        await Assert.That(mail.Sent[^1].Text).Contains("Again.");
    }

    [Test]
    public async Task Without_smtp_or_a_recipient_no_digest_goes()
    {
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        await using var site = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var store = site.Services.GetRequiredService<IMessageStore>();
        var states = new MemoryDigestStateStore();
        await Add(store, clock, "Ada", "ada@example.org", "Hello.", unverified: false);
        clock.Advance(TimeSpan.FromMinutes(1));

        using var withoutSmtp = Sender(site, new NoEmailSender(), clock, states);

        await Assert.That(await withoutSmtp.RunOnceAsync(CancellationToken.None)).IsEqualTo(0);
        await Assert.That(await states.GetAsync("messages", CancellationToken.None)).IsNull();

        using var copy = new SiteCopy();
        Rewrite(copy, json => json.Remove("contactForm"));
        await using var noRecipient = new SiteFactory(copy.Root);
        var digest = noRecipient.Services.GetServices<IDigest>().OfType<MessagesDigest>().Single();
        await Assert.That(await digest.BuildAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None)).IsEmpty();
    }

    [Test]
    public async Task Messages_older_than_12_months_are_deleted()
    {
        var clock = new OAuthTests.TestClock();
        var store = new MemoryMessageStore();
        var old = clock.GetUtcNow().AddMonths(-12).AddDays(-1);
        await store.AddAsync(new ContactMessage(ContactMessage.NewId(old), "Old", "o@example.org", "Old.", old, true, false), CancellationToken.None);
        await store.AddAsync(new ContactMessage(ContactMessage.NewId(clock.GetUtcNow()), "New", "n@example.org", "New.", clock.GetUtcNow(), false, false), CancellationToken.None);

        using var cleanUp = new MessageCleanUp(store, clock, NullLogger<MessageCleanUp>.Instance);
        var deleted = await cleanUp.CleanUpAsync(CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(store.All.Single().Name).IsEqualTo("New");
    }

    [Test]
    [Arguments("0000000000000000000abcdef01", true)]
    [Arguments("000000000000000000Xabcdef01", false)]
    [Arguments("0000000000000000000ABCDEF01", false)]
    [Arguments("../../etc", false)]
    [Arguments("", false)]
    public async Task Message_ids_say_when_they_arrived(string id, bool valid)
    {
        await Assert.That(ContactMessage.ReceivedOf(id) is not null).IsEqualTo(valid);
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        await Assert.That(ContactMessage.ReceivedOf(ContactMessage.NewId(now))).IsEqualTo(now);
    }

    [Test]
    public async Task An_excerpt_is_never_cut_inside_a_character()
    {
        // F-0216: cutting at 119 code units could split an emoji's surrogate pair.
        var emojiAcross = new string('a', 118) + "\U0001F600" + new string('b', 10);
        var emojiAfter = new string('a', 119) + "\U0001F600" + new string('b', 10);

        await Assert.That(Weesite.Web.Components.Pages.Admin.Messages.Excerpt(emojiAcross)).IsEqualTo(new string('a', 118) + "…");
        await Assert.That(Weesite.Web.Components.Pages.Admin.Messages.Excerpt(emojiAfter)).IsEqualTo(new string('a', 119) + "…");
        await Assert.That(Weesite.Web.Components.Pages.Admin.Messages.Excerpt("One\n\nline")).IsEqualTo("One line");
    }

    [Test]
    public async Task The_messages_page_reads_the_page_asked_for_and_the_last_past_the_end()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        var store = factory.Services.GetRequiredService<IMessageStore>();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 55; i++)
        {
            var at = now.AddMinutes(-i);
            await store.AddAsync(new ContactMessage(ContactMessage.NewId(at), $"Sender {i:D2}", "s@example.org", "Hello.", at, Read: i % 2 == 0, Unverified: false), CancellationToken.None);
        }

        using var admin = Admin(factory);
        var first = await admin.GetStringAsync(new Uri("/admin/messages", UriKind.Relative));
        var second = await admin.GetStringAsync(new Uri("/admin/messages?page=2", UriKind.Relative));
        var past = await admin.GetStringAsync(new Uri("/admin/messages?page=9", UriKind.Relative));

        await Assert.That(first).Contains("Sender 00").And.Contains("Sender 49").And.DoesNotContain("Sender 50");
        await Assert.That(second).Contains("Sender 50").And.Contains("Sender 54").And.DoesNotContain("Sender 49");
        await Assert.That(past).Contains("Sender 54").And.DoesNotContain("Sender 49");
        await Assert.That(second).Contains("<span class=\"visually-hidden\">, </span>27<span class=\"visually-hidden\"> unread</span>");
    }

    [Test]
    [Category("containers")]
    public async Task The_table_store_reads_a_page_of_messages_across_months()
    {
        // F-0225: a page reads the keys of all and only its own messages whole, newest first.
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var store = new TableMessageStore(new TableServiceClient(azurite.GetConnectionString()));
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var sent = Enumerable.Range(0, 7)
            .Select(i => now.AddDays(-12 * i))
            .Select((at, i) => new ContactMessage(ContactMessage.NewId(at), $"Sender {i}", "s@example.org", $"Message {i}.", at, Read: i % 3 == 0, Unverified: false))
            .ToList();
        foreach (var message in sent.AsEnumerable().Reverse())
        {
            await store.AddAsync(message, CancellationToken.None);
        }

        var page = await store.ListAsync(2, 3, CancellationToken.None);
        var all = await store.ListAsync(0, 50, CancellationToken.None);
        var beyond = await store.ListAsync(10, 3, CancellationToken.None);

        await Assert.That(page.Messages.Select(m => m.Name)).IsEquivalentTo(["Sender 2", "Sender 3", "Sender 4"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(page.Messages[1]).IsEqualTo(sent[3]);
        await Assert.That(page.Total).IsEqualTo(7);
        await Assert.That(page.Unread).IsEqualTo(4);
        await Assert.That(all.Messages).IsEquivalentTo(sent, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(beyond.Messages).IsEmpty();
        await Assert.That(await store.CountUnreadAsync(CancellationToken.None)).IsEqualTo(4);

        // The kept unread count follows this replica's changes.
        await store.MarkReadAsync(sent[1].Id, CancellationToken.None);
        await Assert.That(await store.CountUnreadAsync(CancellationToken.None)).IsEqualTo(3);
        await Assert.That(await store.DeleteReadAsync(CancellationToken.None)).IsEqualTo(4);
        await Assert.That((await store.ListAsync(0, 50, CancellationToken.None)).Total).IsEqualTo(3);
    }

    [Test]
    [Category("containers")]
    public async Task The_table_stores_keep_messages_and_digest_state()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var store = new TableMessageStore(tables);
        var states = new TableDigestStateStore(tables);
        var limits = new TableVisitorStore(tables);
        var now = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var lastMonth = now.AddMonths(-1);
        var old = new ContactMessage(ContactMessage.NewId(lastMonth), "Old", "o@example.org", "Old.", lastMonth, Read: true, Unverified: false);
        var fresh = new ContactMessage(ContactMessage.NewId(now), "New", "n@example.org", "New\nlines.", now, Read: false, Unverified: true);
        await store.AddAsync(old, CancellationToken.None);
        await store.AddAsync(fresh, CancellationToken.None);

        var page = await store.ListAsync(0, 10, CancellationToken.None);
        await Assert.That(page.Messages.Select(m => m.Name)).IsEquivalentTo(["New", "Old"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(page.Messages[0]).IsEqualTo(fresh);
        await Assert.That(await store.CountUnreadAsync(CancellationToken.None)).IsEqualTo(1);
        await Assert.That((await store.ReceivedAsync(lastMonth, now, CancellationToken.None)).Select(m => m.Name)).IsEquivalentTo(["New"]);
        await Assert.That(await store.MarkReadAsync(fresh.Id, CancellationToken.None)).IsTrue();
        await Assert.That(await store.MarkReadAsync(fresh.Id, CancellationToken.None)).IsFalse();
        await Assert.That(await store.DeleteOlderThanAsync(now.AddDays(-1), CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await store.DeleteReadAsync(CancellationToken.None)).IsEqualTo(1);
        await Assert.That(await store.DeleteAsync(fresh.Id, CancellationToken.None)).IsFalse();
        await Assert.That(await store.GetAsync("not-an-id", CancellationToken.None)).IsNull();

        // Two replicas read the same version: only the first write wins.
        await Assert.That(await states.GetAsync("messages", CancellationToken.None)).IsNull();
        var first = await states.TrySetAsync("messages", new DigestState(now, now, null), CancellationToken.None);
        var second = await states.TrySetAsync("messages", new DigestState(now, now, null), CancellationToken.None);
        var read = await states.GetAsync("messages", CancellationToken.None);
        var next = await states.TrySetAsync("messages", new DigestState(now.AddHours(1), now.AddHours(1), read!.Version), CancellationToken.None);
        var stale = await states.TrySetAsync("messages", new DigestState(now, now, read.Version), CancellationToken.None);

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNull();
        await Assert.That(read.Until).IsEqualTo(now);
        await Assert.That(next).IsNotNull();
        await Assert.That(stale).IsNull();

        // The counters' clean-up leaves the state alone.
        await limits.CleanUpAsync(DateOnly.FromDateTime(now.UtcDateTime.AddDays(10)), CancellationToken.None);
        await Assert.That(await states.GetAsync("messages", CancellationToken.None)).IsNotNull();
    }

    // Forms sent at once: the minimum time has a test of its own.
    private static WebApplicationFactory<Program> Fast(SiteFactory site, Action<IWebHostBuilder>? more = null) =>
        site.WithWebHostBuilder(b =>
        {
            b.UseSetting("Visitor:MinimumSeconds", "0");
            more?.Invoke(b);
        });

    private static HttpClient NoRedirects(WebApplicationFactory<Program> site) =>
        site.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpClient Admin(WebApplicationFactory<Program> factory) => Client(factory, EditorRole.Admin);

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    private static IReadOnlyList<ContactMessage> Messages(WebApplicationFactory<Program> site) =>
        site.Services.GetRequiredService<MemoryMessageStore>().All;

    private static Task<HttpResponseMessage> Send(HttpClient client, string page, params (string Name, string Value)[] fields) =>
        Send(client, page, Address, fields);

    private static async Task<HttpResponseMessage> Send(HttpClient client, string page, string address, params (string Name, string Value)[] fields)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "contact",
            ["__RequestVerificationToken"] = Forms.Token().Match(page).Groups[1].Value,
            ["form-token"] = WebUtility.HtmlDecode(FormToken().Match(page).Groups[1].Value),
            ["proof"] = "",
            ["website"] = "",
        };
        foreach (var (name, value) in fields)
        {
            values[name is "proof" or "website" or "form-token" ? name : "Form." + name] = value;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/contact", UriKind.Relative)) { Content = new FormUrlEncodedContent(values) };
        request.Headers.Add("X-Forwarded-For", address);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SaveSettings(HttpClient client, string page, bool enabled, string contactPage, string recipient)
    {
        var values = Forms.Values(page, "settings").Where(f => !f.Key.StartsWith("Form.ContactForm", StringComparison.Ordinal)).ToList();
        if (enabled)
        {
            values.Add(new("Form.ContactFormEnabled", "true"));
        }

        values.Add(new("Form.ContactFormPage", contactPage));
        values.Add(new("Form.ContactFormRecipient", recipient));
        using var form = new FormUrlEncodedContent(values);
        return await client.PostAsync(new Uri("/admin/settings", UriKind.Relative), form);
    }

    private static async Task<HttpResponseMessage> PostAdmin(HttpClient client, string page, string path, params (string Name, string Value)[] fields)
    {
        var token = Forms.Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new("__RequestVerificationToken", token)]);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    private static void Rewrite(SiteCopy copy, Action<JsonObject> change)
    {
        var path = Path.Combine(copy.Root, "settings", "site.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    private static DigestSender Sender(WebApplicationFactory<Program> site, IEmailSender mail, TimeProvider clock, IDigestStateStore? states = null) => new(
        site.Services.GetServices<IDigest>(),
        states ?? new MemoryDigestStateStore(),
        mail,
        site.Services.GetRequiredService<ContentCatalogProvider>(),
        clock,
        NullLogger<DigestSender>.Instance);

    private static Task Add(IMessageStore store, TimeProvider clock, string name, string email, string text, bool unverified)
    {
        var now = clock.GetUtcNow();
        return store.AddAsync(new ContactMessage(ContactMessage.NewId(now), name, email, text, now, Read: false, unverified), CancellationToken.None);
    }

    [GeneratedRegex("name=\"form-token\" value=\"([^\"]+)\"")]
    private static partial Regex FormToken();

    [GeneratedRegex("<input id=\"contact-website\"[^>]*>")]
    private static partial Regex HoneypotInput();

    // Records what it sends, or fails like a server that can't be reached.
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public bool Fail { get; set; }

        public bool IsConfigured => true;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new IOException("The server refused the connection.");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    // Keeps every log line, as "category level: message".
    private sealed class LogLines : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(LogLines owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)}");
        }
    }
}
