using MailKit.Security;
using Microsoft.Extensions.Configuration;
using MimeKit;
using Weesite.Web.Admin;
using Weesite.Web.Email;

namespace Weesite.Web.Tests;

/// <summary>The SMTP settings, the message the sender builds, and the invitation's text (ADR 0010).</summary>
internal sealed class EmailTests
{
    private static IConfiguration Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Test]
    public async Task Without_a_host_or_a_sender_nothing_is_sent()
    {
        await Assert.That(SmtpSettings.Read(Settings())).IsNull();
        await Assert.That(SmtpSettings.Read(Settings(("Email:Smtp:Host", "smtp.example.com")))).IsNull();
        await Assert.That(SmtpSettings.Read(Settings(("Email:From", "noreply@example.com")))).IsNull();
        await Assert.That(new NoEmailSender().IsConfigured).IsFalse();
    }

    [Test]
    public async Task Settings_default_to_587_with_starttls()
    {
        var settings = SmtpSettings.Read(Settings(
            ("Email:Smtp:Host", "smtp.example.com"), ("Email:From", "noreply@example.com"),
            ("Email:Smtp:UserName", "apikey"), ("Email:Smtp:Password", "secret")))!;

        await Assert.That(settings).IsEqualTo(new SmtpSettings("smtp.example.com", 587, SecureSocketOptions.StartTls, "apikey", "secret", "noreply@example.com"));
        await Assert.That(settings.ToString()).DoesNotContain("secret");
    }

    [Test]
    [Arguments("smtp.example.com", "465", "SslOnConnect", true)]
    [Arguments("localhost", "1025", "None", true)]
    [Arguments("127.0.0.1", "1025", "None", true)]
    [Arguments("smtp.example.com", "25", "None", false)]
    [Arguments("smtp.example.com", "587", "Auto", false)]
    [Arguments("smtp.example.com", "smtp", "StartTls", false)]
    [Arguments("smtp.example.com", "70000", "StartTls", false)]
    public async Task Without_tls_only_a_server_on_this_machine(string host, string port, string security, bool ok)
    {
        var configuration = Settings(("Email:Smtp:Host", host), ("Email:Smtp:Port", port), ("Email:Smtp:Security", security), ("Email:From", "noreply@example.com"));

        if (ok)
        {
            await Assert.That(SmtpSettings.Read(configuration)).IsNotNull();
        }
        else
        {
            await Assert.That(() => SmtpSettings.Read(configuration)).Throws<InvalidOperationException>();
        }
    }

    [Test]
    public async Task The_sender_must_be_a_plain_address()
    {
        var configuration = Settings(("Email:Smtp:Host", "smtp.example.com"), ("Email:From", "Site <noreply@example.com>"));

        await Assert.That(() => SmtpSettings.Read(configuration)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task The_message_has_the_site_as_sender_and_text_with_an_html_alternative()
    {
        using var message = SmtpEmailSender.Build(new EmailMessage("writer@example.com", "Subject", "Text.\n", "<p>Text.</p>", "Example Site"), "noreply@example.com");

        await Assert.That(message.From.Mailboxes.Single().ToString()).IsEqualTo("\"Example Site\" <noreply@example.com>");
        await Assert.That(message.To.Mailboxes.Single().Address).IsEqualTo("writer@example.com");
        await Assert.That(message.Subject).IsEqualTo("Subject");
        await Assert.That(message.Body).IsTypeOf<MultipartAlternative>();
        await Assert.That(message.TextBody).IsEqualTo("Text.\n");
        await Assert.That(message.HtmlBody).IsEqualTo("<p>Text.</p>");
    }

    [Test]
    public async Task The_invitation_says_who_added_them_the_role_and_where_to_sign_in()
    {
        var author = new Editor("writer@example.com", EditorRole.Author, "boss@example.com", DateTimeOffset.UtcNow);
        var admin = author with { Role = EditorRole.Admin };

        var added = EditorInvitations.Build("Example <Site>", "https://example.com/admin", author, roleChanged: false);
        var changed = EditorInvitations.Build("Example <Site>", "https://example.com/admin", admin, roleChanged: true);

        await Assert.That(added.To).IsEqualTo("writer@example.com");
        await Assert.That(added.FromName).IsEqualTo("Example <Site>");
        await Assert.That(added.Subject).IsEqualTo("You can now edit Example <Site>");
        await Assert.That(added.Text).StartsWith("boss@example.com has added you to Example <Site> as an author.\n\nAs an author, you can write and publish your own posts and upload media.\n\n");
        await Assert.That(added.Text).Contains("Sign in at https://example.com/admin with the Google or Microsoft account of this address, writer@example.com.");
        await Assert.That(added.Html).Contains("<p>boss@example.com has added you to Example &lt;Site&gt; as an author.</p>");
        await Assert.That(added.Html).Contains("<a href=\"https://example.com/admin\">https://example.com/admin</a>");
        await Assert.That(changed.Subject).IsEqualTo("Your role on Example <Site> has changed");
        await Assert.That(changed.Text).StartsWith("boss@example.com has changed your role on Example <Site>: you're now an admin.");
        await Assert.That(changed.Text).Contains("change the site's settings, menu, theme, tags and editors");
    }
}
