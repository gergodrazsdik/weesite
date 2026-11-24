using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>Limits on what the contact form takes, and the reply links to its senders (T-0177).</summary>
internal sealed partial class ContactFormTests
{
    // An address RFC 5322 allows, whose local part looks like mailto: fields.
    private const string Tricky = "x?bcc=other%40example.net&body=hi@example.org";

    [Test]
    public async Task A_form_over_the_size_limit_is_refused_before_it_is_read()
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root);
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var large = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", new string('a', VisitorBody.MaxBytes)));
        using var trailing = await PostTo(client, "/contact/", page, new string('a', VisitorBody.MaxBytes));

        await Assert.That(large.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(trailing.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(Messages(site)).IsEmpty();
    }

    [Test]
    public async Task A_message_far_over_the_limit_is_refused_as_too_long()
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root);
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var sent = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", new string('a', (ContactInbox.MessageMax * VisitorText.SlackFactor) + 1)));

        await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(WebUtility.HtmlDecode(await sent.Content.ReadAsStringAsync())).Contains("1000");
        await Assert.That(Messages(site)).IsEmpty();
    }

    [Test]
    public async Task A_form_token_takes_one_message()
    {
        await using var root = new SiteFactory();
        await using var site = Fast(root);
        using var client = NoRedirects(site);

        var page = await client.GetStringAsync(new Uri("/contact", UriKind.Relative));
        using var first = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", "Once."));
        using var again = await Send(client, page, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", "Twice."));
        var refused = await again.Content.ReadAsStringAsync();
        var answer = WebUtility.HtmlDecode(refused);
        using var resent = await Send(client, refused, ("Name", "Ada"), ("Email", "ada@example.org"), ("Message", "Twice."));

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(answer).Contains("Please press Send again");
        await Assert.That(resent.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(Messages(site).Select(m => m.Text)).IsEquivalentTo(["Once.", "Twice."]);
    }

    [Test]
    public async Task Reply_links_keep_the_whole_address_in_the_to_field()
    {
        await Assert.That(ContactFormSettings.IsAddress(Tricky)).IsTrue();
        await Assert.That(MessagesDigest.MailtoOf("ada@example.org")).IsEqualTo("mailto:ada@example.org");
        await Assert.That(MessagesDigest.MailtoOf(Tricky)).IsEqualTo("mailto:x%3Fbcc%3Dother%2540example.net%26body%3Dhi@example.org");

        await using var root = new SiteFactory();
        await using var factory = root.WithTestSignIn();
        var store = factory.Services.GetRequiredService<IMessageStore>();
        var now = DateTimeOffset.UtcNow;
        var message = new ContactMessage(ContactMessage.NewId(now), "Ada", Tricky, "Hello.", now, Read: false, Unverified: false);
        await store.AddAsync(message, CancellationToken.None);
        using var admin = Admin(factory);

        var view = await admin.GetStringAsync(new Uri("/admin/messages/view?id=" + message.Id, UriKind.Relative));
        var mail = MessagesDigest.Build(factory.Services.GetRequiredService<ContentCatalogProvider>().Current.Site, "owner@example.com", [message], link: null);

        await Assert.That(view).Contains("href=\"mailto:x%3Fbcc%3Dother%2540example.net%26body%3Dhi@example.org\"");
        await Assert.That(view).DoesNotContain("href=\"mailto:x?");
        await Assert.That(mail.Html).Contains("<a href=\"mailto:x%3Fbcc%3Dother%2540example.net%26body%3Dhi@example.org\">");
    }

    [Test]
    public async Task A_digest_lists_at_most_fifty_messages_and_counts_the_rest()
    {
        await using var root = new SiteFactory();
        var site = root.Services.GetRequiredService<ContentCatalogProvider>().Current.Site;
        var now = DateTimeOffset.UtcNow;
        var arrived = Enumerable.Range(0, MessagesDigest.MaxListed + 3)
            .Select(i => new ContactMessage(ContactMessage.NewId(now.AddSeconds(i)), "Ada", "ada@example.org", $"Message number {i}.", now.AddSeconds(i), Read: false, Unverified: false))
            .ToList();

        var mail = MessagesDigest.Build(site, "owner@example.com", arrived, link: null);

        await Assert.That(mail.Text).Contains($"Message number {MessagesDigest.MaxListed - 1}.").And.DoesNotContain($"Message number {MessagesDigest.MaxListed}.");
        await Assert.That(mail.Text).Contains("3 more messages aren't listed here.");
    }

    // The contact form's post to a path of choice.
    private static async Task<HttpResponseMessage> PostTo(HttpClient client, string path, string page, string message)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "contact",
            ["__RequestVerificationToken"] = Forms.Token().Match(page).Groups[1].Value,
            ["form-token"] = WebUtility.HtmlDecode(FormToken().Match(page).Groups[1].Value),
            ["Form.Name"] = "Ada",
            ["Form.Email"] = "ada@example.org",
            ["Form.Message"] = message,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new FormUrlEncodedContent(values) };
        request.Headers.Add("X-Forwarded-For", Address);
        return await client.SendAsync(request);
    }
}
