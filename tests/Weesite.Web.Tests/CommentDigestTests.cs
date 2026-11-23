using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Weesite.Content;
using Weesite.Web.Email;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The digest's mails, one recipient at a time (F-0231, VIS-02, AGT-03).</summary>
internal sealed partial class CommentTests
{
    private const string Moderators = "first@example.com, refused@example.com, third@example.com";

    [Test]
    public async Task A_recipient_the_server_refuses_for_good_doesnt_make_the_others_get_it_again()
    {
        // F-0231: a recipient refused at RCPT made the digest go back, and the next minute sent the
        // first moderator the same mail again, every minute; the third never got theirs.
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        await using var site = Site(root, clock: clock, more: b => b.UseSetting("Site:Admins", Moderators));
        using var client = site.CreateClient();
        await SendAsync(client, site, "Ada", "A comment to moderate.");
        clock.Advance(DigestSender.Settle + TimeSpan.FromMinutes(1));

        var mail = new ScriptedSender { ["refused@example.com"] = () => new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, MailboxAddress.Parse("refused@example.com"), "No such mailbox") };
        using var sender = Digests(site, mail, clock);
        var sent = new List<int>();
        for (var minute = 0; minute < 5; minute++)
        {
            sent.Add(await sender.RunOnceAsync(CancellationToken.None));
            clock.Advance(DigestSender.Tick);
        }

        await Assert.That(sent).IsEquivalentTo([2, 0, 0, 0, 0], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(mail.Sent.Select(m => m.To)).IsEquivalentTo(["first@example.com", "third@example.com"]);
        await Assert.That(mail.Tried.Count(to => to == "refused@example.com")).IsEqualTo(1);
    }

    [Test]
    public async Task A_recipient_whose_send_failed_gets_it_a_minute_later_and_the_others_only_once()
    {
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        await using var site = Site(root, clock: clock, more: b => b.UseSetting("Site:Admins", Moderators));
        using var client = site.CreateClient();
        await SendAsync(client, site, "Ada", "A comment to moderate.");
        clock.Advance(DigestSender.Settle + TimeSpan.FromMinutes(1));

        // The second recipient's send fails once, as a server that's busy for a moment does.
        var failures = 1;
        var mail = new ScriptedSender { ["refused@example.com"] = () => failures-- > 0 ? new IOException("The server didn't answer.") : null };
        using var one = Digests(site, mail, clock);
        using var two = Digests(site, mail, clock, one.States);
        var first = await one.RunOnceAsync(CancellationToken.None);

        // A comment that arrives meanwhile waits for the next digest; it isn't in the owed mail.
        await SendAsync(client, site, "Bob", "A later comment.");
        clock.Advance(DigestSender.Tick);
        var retried = await Task.WhenAll(one.RunOnceAsync(CancellationToken.None), two.RunOnceAsync(CancellationToken.None));
        clock.Advance(DigestSender.Tick);
        var quiet = await one.RunOnceAsync(CancellationToken.None);

        await Assert.That(first).IsEqualTo(2);
        await Assert.That(retried.Sum()).IsEqualTo(1);
        await Assert.That(quiet).IsEqualTo(0);
        await Assert.That(mail.Sent.Select(m => m.To)).IsEquivalentTo(["first@example.com", "third@example.com", "refused@example.com"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(mail.Sent[^1].Text).Contains("A comment to moderate.").And.DoesNotContain("A later comment.");

        // The next digest, an hour after the first, has the later one for everyone.
        clock.Advance(DigestSender.Interval);
        await Assert.That(await one.RunOnceAsync(CancellationToken.None)).IsEqualTo(3);
        await Assert.That(mail.Sent.Skip(3).All(m => m.Text.Contains("A later comment.", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task A_mail_still_failing_when_the_next_digest_is_due_is_given_up()
    {
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        await using var site = Site(root, clock: clock, more: b => b.UseSetting("Site:Admins", Moderators));
        using var client = site.CreateClient();
        await SendAsync(client, site, "Ada", "A comment to moderate.");
        clock.Advance(DigestSender.Settle + TimeSpan.FromMinutes(1));

        var mail = new ScriptedSender { ["refused@example.com"] = () => new IOException("The server didn't answer.") };
        using var sender = Digests(site, mail, clock);
        await sender.RunOnceAsync(CancellationToken.None);
        for (var minute = 0; minute < 3; minute++)
        {
            clock.Advance(DigestSender.Tick);
            await sender.RunOnceAsync(CancellationToken.None);
        }

        var triesWithinTheHour = mail.Tried.Count(to => to == "refused@example.com");
        clock.Advance(DigestSender.Interval);
        await sender.RunOnceAsync(CancellationToken.None);
        clock.Advance(DigestSender.Tick);
        await sender.RunOnceAsync(CancellationToken.None);

        await Assert.That(triesWithinTheHour).IsEqualTo(4);
        await Assert.That(mail.Tried.Count(to => to == "refused@example.com")).IsEqualTo(4);
        await Assert.That(mail.Sent.Count(m => m.To == "first@example.com")).IsEqualTo(1);
        await Assert.That((await sender.States.GetAsync("comments", CancellationToken.None))!.Owed).IsEmpty();
    }

    [Test]
    public async Task Only_a_refused_recipient_or_an_unreadable_address_is_a_failure_for_good()
    {
        var mailbox = MailboxAddress.Parse("someone@example.com");
        var unreadable = new ParseException("Invalid addr-spec token.", 0, 0);

        await Assert.That(EmailFailure.IsPermanent(new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, mailbox, "No such mailbox"))).IsTrue();
        await Assert.That(EmailFailure.IsPermanent(unreadable)).IsTrue();

        // A full or busy mailbox (4xx), a refused sender, or no connection may pass.
        await Assert.That(EmailFailure.IsPermanent(new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, mailbox, "Try later"))).IsFalse();
        await Assert.That(EmailFailure.IsPermanent(new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxUnavailable, mailbox, "Not you"))).IsFalse();
        await Assert.That(EmailFailure.IsPermanent(new IOException("No connection."))).IsFalse();
    }

    [Test]
    public async Task The_comments_digest_reads_the_catalog_not_the_whole_table()
    {
        // F-0250: the digest listed every comment ever kept, every minute on every replica.
        var clock = new OAuthTests.TestClock();
        await using var root = new SiteFactory();
        var counting = new CountingCommentStore();
        await using var site = Site(root, clock: clock, more: b => b.UseSetting("Site:Admins", "boss@example.com").ConfigureServices(s => s.AddSingleton<ICommentStore>(counting)));
        using var client = site.CreateClient();
        await SendAsync(client, site, "Ada", "A comment to moderate.");
        clock.Advance(DigestSender.Settle + TimeSpan.FromMinutes(1));
        var digest = site.Services.GetServices<IDigest>().OfType<CommentsDigest>().Single();
        await digest.BuildAsync(clock.GetUtcNow().AddDays(-1), clock.GetUtcNow(), CancellationToken.None);

        var listsBefore = counting.Lists;
        var mails = await digest.BuildAsync(clock.GetUtcNow().AddDays(-1), clock.GetUtcNow(), CancellationToken.None);
        var quiet = await digest.BuildAsync(clock.GetUtcNow(), clock.GetUtcNow().AddMinutes(1), CancellationToken.None);

        await Assert.That(mails.Count).IsEqualTo(1);
        await Assert.That(mails[0].Text).Contains("A comment to moderate.");
        await Assert.That(quiet).IsEmpty();
        await Assert.That(counting.Lists).IsEqualTo(listsBefore);
    }

    private static DigestRun Digests(WebApplicationFactory<Program> site, IEmailSender mail, TimeProvider clock, IDigestStateStore? states = null) =>
        new(site.Services.GetServices<IDigest>(), states ?? new MemoryDigestStateStore(), mail, site.Services.GetRequiredService<ContentCatalogProvider>(), clock);

    /// <summary>A digest sender with the state it keeps, for a second replica to share.</summary>
    private sealed class DigestRun(IEnumerable<IDigest> digests, IDigestStateStore states, IEmailSender mail, ContentCatalogProvider content, TimeProvider clock) : IDisposable
    {
        private readonly DigestSender _sender = new(digests, states, mail, content, clock, NullLogger<DigestSender>.Instance);

        public IDigestStateStore States => states;

        public Task<int> RunOnceAsync(CancellationToken cancellationToken) => _sender.RunOnceAsync(cancellationToken);

        public void Dispose() => _sender.Dispose();
    }

    /// <summary>Sends, or fails for the recipients given a failure.</summary>
    private sealed class ScriptedSender : IEmailSender
    {
        private readonly Dictionary<string, Func<Exception?>> _failures = new(StringComparer.OrdinalIgnoreCase);

        public List<EmailMessage> Sent { get; } = [];

        public List<string> Tried { get; } = [];

        public bool IsConfigured => true;

        public Func<Exception?> this[string to]
        {
            set => _failures[to] = value;
        }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Tried.Add(message.To);
            if (_failures.GetValueOrDefault(message.To)?.Invoke() is { } failure)
            {
                throw failure;
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>The memory store, counting how often the whole table is listed.</summary>
    private sealed class CountingCommentStore : ICommentStore
    {
        private readonly MemoryCommentStore _inner = new();
        private int _lists;

        public int Lists => Volatile.Read(ref _lists);

        public Task<Comment?> SaveAsync(Comment comment, CancellationToken cancellationToken) => _inner.SaveAsync(comment, cancellationToken);

        public Task<Comment?> GetAsync(string post, string id, CancellationToken cancellationToken) => _inner.GetAsync(post, id, cancellationToken);

        public Task<bool> DeleteAsync(string post, string id, CancellationToken cancellationToken, string? version = null) => _inner.DeleteAsync(post, id, cancellationToken, version);

        public Task<IReadOnlyList<Comment>> ListAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lists);
            return _inner.ListAsync(cancellationToken);
        }

        public Task<string> VersionAsync(CancellationToken cancellationToken) => _inner.VersionAsync(cancellationToken);
    }
}
