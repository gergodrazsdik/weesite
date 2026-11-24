using System.Net;
using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Tests;
using Weesite.Web.Analytics;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The layers against spam that every visitor form shares (ADR 0011).</summary>
internal sealed class VisitorGuardTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments("203.0.113.7", "203.0.113.7")]
    [Arguments("203.0.113.7:51234", "203.0.113.7")]
    [Arguments("::ffff:203.0.113.7", "203.0.113.7")]
    [Arguments("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [Arguments("[2001:db8:1:2::9]:443", "2001:db8:1:2::/64")]
    [Arguments("", "")]
    public async Task Addresses_are_counted_per_address_or_per_IPv6_64(string address, string counted)
    {
        await Assert.That(ClientHashes.Normalize(address)).IsEqualTo(counted);
    }

    [Test]
    public async Task Hashes_differ_by_purpose_day_and_browser_and_hold_no_address()
    {
        var monday = new byte[32];
        var tuesday = Enumerable.Repeat((byte)1, 32).ToArray();

        var contact = ClientHashes.Hash(monday, "contact", "203.0.113.7", null);
        var comment = ClientHashes.Hash(monday, "comment", "203.0.113.7", null);
        var nextDay = ClientHashes.Hash(tuesday, "contact", "203.0.113.7", null);
        var browser = ClientHashes.Hash(monday, "contact", "203.0.113.7", "Firefox");

        await Assert.That(contact).IsEqualTo(ClientHashes.Hash(monday, "contact", "203.0.113.7", null));
        await Assert.That(contact).Matches("^[0-9a-f]{16}$");
        await Assert.That(new[] { comment, nextDay, browser }).DoesNotContain(contact);
        await Assert.That(contact).DoesNotContain("203");
    }

    [Test]
    public async Task The_hash_of_a_request_uses_the_clients_address_as_the_forwarded_headers_set_it()
    {
        // The forwarded headers middleware puts the address the ingress added on the connection;
        // what the client sent in X-Forwarded-For itself is never read here.
        var hashes = new ClientHashes(new MemoryPageViewStore(), TimeProvider.System);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.1";
        var mapped = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        mapped.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("::ffff:203.0.113.7");
        var other = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        other.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.8");
        other.Request.Headers["X-Forwarded-For"] = "198.51.100.1";

        var first = await hashes.OfAddressAsync(context, "contact", CancellationToken.None);
        var again = await hashes.OfAddressAsync(mapped, "contact", CancellationToken.None);
        var second = await hashes.OfAddressAsync(other, "contact", CancellationToken.None);

        await Assert.That(ClientAddress.Of(mapped)).IsEqualTo("203.0.113.7");
        await Assert.That(again).IsEqualTo(first);
        await Assert.That(second).IsNotEqualTo(first);
    }

    [Test]
    public async Task Counters_stop_at_the_limit_per_window_and_take_back()
    {
        var store = new MemoryVisitorStore();

        var counted = new List<bool>();
        for (var i = 0; i < 3; i++)
        {
            counted.Add(await store.TryCountAsync("contact", "abc", Noon, CountWindow.Hour, 2, CancellationToken.None));
        }

        var nextHour = await store.TryCountAsync("contact", "abc", Noon.AddHours(1), CountWindow.Hour, 2, CancellationToken.None);
        var otherHash = await store.TryCountAsync("contact", "def", Noon, CountWindow.Hour, 2, CancellationToken.None);
        var takenBack = await store.TryUncountAsync("contact", "abc", Noon, CountWindow.Hour, CancellationToken.None);
        var afterTakeBack = await store.TryCountAsync("contact", "abc", Noon, CountWindow.Hour, 2, CancellationToken.None);
        var nothingToTake = await store.TryUncountAsync("reaction", "abc", Noon, CountWindow.Day, CancellationToken.None);

        await Assert.That(counted).IsEquivalentTo([true, true, false], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(nextHour).IsTrue();
        await Assert.That(otherHash).IsTrue();
        await Assert.That(takenBack).IsTrue();
        await Assert.That(afterTakeBack).IsTrue();
        await Assert.That(nothingToTake).IsFalse();
    }

    [Test]
    public async Task Old_days_are_cleaned_up()
    {
        var store = new MemoryVisitorStore();
        await store.TryCountAsync("contact", "abc", Noon.AddDays(-2), CountWindow.Hour, 5, CancellationToken.None);
        await store.TryCountAsync("contact", "abc", Noon.AddDays(-1), CountWindow.Hour, 5, CancellationToken.None);
        await store.TryCountAsync("posts/a.md|like", "abc", Noon, CountWindow.Day, 1, CancellationToken.None);

        var deleted = await store.CleanUpAsync(DateOnly.FromDateTime(Noon.UtcDateTime).AddDays(-1), CancellationToken.None);

        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(store.Keys).IsEquivalentTo(["2026-11-18/contact|12|abc", "2026-11-19/posts~a.md|like|day|abc"]);
    }

    [Test]
    public async Task A_solved_challenge_works_once_for_its_purpose_and_in_time()
    {
        var clock = new OAuthTests.TestClock();
        var proof = Proof(clock, out _);

        var challenge = proof.Create("contact");
        var answer = ProofOfWork.Solve(challenge);

        await Assert.That(challenge.Algorithm).IsEqualTo("SHA-256");
        await Assert.That(challenge.Salt).StartsWith("contact.");
        await Assert.That(answer).IsNotNull();
        await Assert.That(await proof.VerifyAsync("comment", answer, CancellationToken.None)).IsFalse();
        await Assert.That(await proof.VerifyAsync("contact", answer, CancellationToken.None)).IsTrue();
        await Assert.That(await proof.VerifyAsync("contact", answer, CancellationToken.None)).IsFalse();

        var late = ProofOfWork.Solve(proof.Create("contact"));
        clock.Advance(ProofOfWork.Lifetime + TimeSpan.FromSeconds(1));
        await Assert.That(await proof.VerifyAsync("contact", late, CancellationToken.None)).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("12")]
    [Arguments("x:y")]
    [Arguments("12:not-a-signature")]
    public async Task Answers_that_are_not_answers_fail(string? answer)
    {
        var proof = Proof(new OAuthTests.TestClock(), out _);

        await Assert.That(await proof.VerifyAsync("contact", answer, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task A_wrong_number_fails()
    {
        var proof = Proof(new OAuthTests.TestClock(), out _);
        var challenge = proof.Create("reaction");
        var number = int.Parse(ProofOfWork.Solve(challenge)!.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture);

        var wrong = $"{(number + 1) % (challenge.MaxNumber + 1)}:{challenge.Signature}";

        await Assert.That(await proof.VerifyAsync("reaction", wrong, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Another_sites_signature_fails()
    {
        var proof = Proof(new OAuthTests.TestClock(), out _);
        var other = Proof(new OAuthTests.TestClock(), out _);

        var answer = ProofOfWork.Solve(other.Create("contact"));

        await Assert.That(await proof.VerifyAsync("contact", answer, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task A_form_token_is_used_once_and_stays_used_until_it_has_expired()
    {
        var clock = new OAuthTests.TestClock();
        var options = Options();
        var store = new MemoryVisitorStore();
        var guard = new FormGuard(new EphemeralDataProtectionProvider(), clock, options, store);
        using var cleanUp = new VisitorCleanUp(store, clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<VisitorCleanUp>.Instance);

        var token = guard.Issue("contact");
        var other = guard.Issue("contact");
        clock.Advance(options.MinimumTime);
        var first = await guard.TryUseAsync(token, CancellationToken.None);
        var again = await guard.TryUseAsync(token, CancellationToken.None);
        var otherFirst = await guard.TryUseAsync(other, CancellationToken.None);

        // The daily clean-up keeps the mark while the token could still be sent.
        clock.Advance(options.FormLifetime - options.MinimumTime - TimeSpan.FromMinutes(1));
        await cleanUp.CleanUpAsync(CancellationToken.None);
        var stillUsed = await guard.TryUseAsync(token, CancellationToken.None);
        var check = guard.Check("contact", token);

        await Assert.That((first, again, otherFirst, stillUsed)).IsEqualTo((true, false, true, false));
        await Assert.That(check).IsEqualTo(FormCheck.Ok);
    }

    [Test]
    public async Task Forms_sent_too_soon_or_too_late_are_refused()
    {
        var clock = new OAuthTests.TestClock();
        var options = Options();
        var guard = new FormGuard(new EphemeralDataProtectionProvider(), clock, options, new MemoryVisitorStore());

        var token = guard.Issue("contact");
        var tooFast = guard.Check("contact", token);
        clock.Advance(options.MinimumTime);
        var inTime = guard.Check("contact", token);
        var otherForm = guard.Check("comment", token);
        clock.Advance(options.FormLifetime);
        var late = guard.Check("contact", token);

        await Assert.That(tooFast).IsEqualTo(FormCheck.TooFast);
        await Assert.That(inTime).IsEqualTo(FormCheck.Ok);
        await Assert.That(otherForm).IsEqualTo(FormCheck.Invalid);
        await Assert.That(late).IsEqualTo(FormCheck.Expired);
        await Assert.That(guard.Check("contact", null)).IsEqualTo(FormCheck.Invalid);
        await Assert.That(guard.Check("contact", "forged")).IsEqualTo(FormCheck.Invalid);
        await Assert.That(FormGuard.IsHoneypotFilled("")).IsFalse();
        await Assert.That(FormGuard.IsHoneypotFilled("https://spam.example")).IsTrue();
    }

    [Test]
    public async Task Text_is_normalized()
    {
        var text = "  \r\n Hello\u200B there\t!  \r\n\r\n\r\n\r\n\r\nBye\u0007 \u202Eok\u200D\U0001F44D  \n\n";

        await Assert.That(VisitorText.Normalize(text)).IsEqualTo("Hello there !\n\n\nBye ok\u200D\U0001F44D");
        await Assert.That(VisitorText.Normalize("e\u0301")).IsEqualTo("\u00E9");
        await Assert.That(VisitorText.NormalizeLine("  Ada \n  Lovelace ")).IsEqualTo("Ada Lovelace");
        await Assert.That(VisitorText.Normalize(null)).IsEqualTo("");
    }

    [Test]
    [Arguments("\u00A0\u00A0")]
    [Arguments("\u3000")]
    [Arguments(" \u2003\n\u202F\u00A0 ")]
    public async Task Text_of_only_unicode_spaces_is_empty(string text)
    {
        await Assert.That(VisitorText.Normalize(text)).IsEqualTo("");
        await Assert.That(VisitorText.NormalizeLine(text)).IsEqualTo("");
        await Assert.That(VisitorText.Check(VisitorText.NormalizeLine(text), 100)).IsEqualTo(TextProblem.Empty);
        await Assert.That(VisitorText.NormalizeLine("\u3000Ada\u00A0\u00A0Lovelace\u00A0")).IsEqualTo("Ada Lovelace");
    }

    [Test]
    [Arguments("", TextProblem.Empty)]
    [Arguments("Hello, thanks for the post.", TextProblem.None)]
    [Arguments("If x < y and y > z, then **this** is _Markdown_ <3", TextProblem.None)]
    [Arguments("Buy <a href=\"https://spam.example\">pills</a>", TextProblem.Markup)]
    [Arguments("line<br/>break", TextProblem.Markup)]
    [Arguments("Hi <svg/onload=alert(1)>", TextProblem.Markup)]
    [Arguments("[url=https://spam.example]pills[/url]", TextProblem.Markup)]
    [Arguments("[B]loud[/B]", TextProblem.None)]
    [Arguments("<IMG SRC=x>", TextProblem.Markup)]
    [Arguments("</a>", TextProblem.Markup)]
    [Arguments("<script>alert(1)</script>", TextProblem.Markup)]
    [Arguments("<iframe src=\"https://spam.example\"></iframe>", TextProblem.Markup)]
    [Arguments("<p onclick='go()'>hi</p>", TextProblem.Markup)]
    [Arguments("<x href=https://spam.example>", TextProblem.Markup)]
    [Arguments("[url]https://spam.example[/url]", TextProblem.Markup)]
    [Arguments("[IMG]https://spam.example/a.png[/IMG]", TextProblem.Markup)]
    [Arguments("[link=https://spam.example]x[/link]", TextProblem.Markup)]
    [Arguments("[email]a@spam.example[/email]", TextProblem.Markup)]
    [Arguments("Use List<string> or Task<int> where T is <T>.", TextProblem.None)]
    [Arguments("Press <Enter>, then <Ctrl>+<C>.", TextProblem.None)]
    [Arguments("Dictionary<string, List<int>> and type X<T = string> = T", TextProblem.None)]
    [Arguments("for (var i = 0; i < n; i++) { arr[i] = a<b; }", TextProblem.None)]
    [Arguments("Wrap it in [code]…[/code] or [s]strike[/s], <b>bold</b> or <Foo />", TextProblem.None)]
    [Arguments("See [1] and [the docs](https://example.com/docs)", TextProblem.None)]
    [Arguments("https://a.example https://b.example www.c.example", TextProblem.None)]
    [Arguments("https://a.example https://b.example www.c.example http://d.example", TextProblem.TooManyLinks)]
    public async Task Text_is_checked(string text, TextProblem problem)
    {
        await Assert.That(VisitorText.Check(text, 100)).IsEqualTo(problem);
    }

    [Test]
    public async Task Text_has_a_maximum_length()
    {
        await Assert.That(VisitorText.Check(new string('a', 1000), 1000)).IsEqualTo(TextProblem.None);
        await Assert.That(VisitorText.Check(new string('a', 1001), 1000)).IsEqualTo(TextProblem.TooLong);
        await Assert.That(VisitorText.Check("https://example.com", 100, maxLinks: 0)).IsEqualTo(TextProblem.TooManyLinks);
    }

    [Test]
    public async Task Links_leave_out_the_punctuation_after_them()
    {
        var links = VisitorText.Links("Read https://example.com/a. Or (www.example.org/b), and https://en.example/wiki/X_(y)! Not www.");

        await Assert.That(links.Select(l => l.Url)).IsEquivalentTo(
            ["https://example.com/a", "https://www.example.org/b", "https://en.example/wiki/X_(y)"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(links[1].Length).IsEqualTo("www.example.org/b".Length);
    }

    [Test]
    public async Task The_challenge_endpoint_answers_fresh_challenges_for_known_purposes()
    {
        await using var site = new SiteFactory();
        using var client = site.CreateClient();

        using var first = await client.GetAsync(new Uri("/visitor/challenge?for=contact", UriKind.Relative));
        using var second = await client.GetAsync(new Uri("/visitor/challenge?for=contact", UriKind.Relative));
        using var unknown = await client.GetAsync(new Uri("/visitor/challenge?for=newsletter", UriKind.Relative));
        using var none = await client.GetAsync(new Uri("/visitor/challenge", UriKind.Relative));
        var one = await first.Content.ReadFromJsonAsync<ProofChallenge>();
        var two = await second.Content.ReadFromJsonAsync<ProofChallenge>();

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(first.Headers.CacheControl?.NoStore).IsTrue();
        await Assert.That(one!.MaxNumber).IsEqualTo(100_000);
        await Assert.That(two!.Salt).IsNotEqualTo(one.Salt);
        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(none.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var proof = site.Services.GetRequiredService<ProofOfWork>();
        await Assert.That(await proof.VerifyAsync("contact", ProofOfWork.Solve(one), CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Challenges_have_a_per_minute_limit()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithWebHostBuilder(builder => builder.UseSetting("RateLimits:VisitorPerMinute", "1"));
        using var client = factory.CreateClient();

        using var first = await Get(client, "198.51.100.20");
        using var second = await Get(client, "198.51.100.20");
        using var otherAddress = await Get(client, "198.51.100.21");

        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(otherAddress.StatusCode).IsEqualTo(HttpStatusCode.OK);

        static async Task<HttpResponseMessage> Get(HttpClient client, string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/visitor/challenge?for=reaction", UriKind.Relative));
            request.Headers.Add("X-Forwarded-For", address);
            return await client.SendAsync(request);
        }
    }

    [Test]
    [Category("containers")]
    public async Task The_table_store_counts_across_replicas_and_cleans_up()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var one = new TableVisitorStore(tables);
        var two = new TableVisitorStore(tables);

        // Two replicas at once, ten each, against a limit of five: never more than five get through.
        var attempts = Enumerable.Range(0, 20)
            .Select(i => (i % 2 == 0 ? one : two).TryCountAsync("burst", "abc", Noon, CountWindow.Hour, 5, CancellationToken.None));
        var burst = await Task.WhenAll(attempts);
        await Assert.That(burst.Count(c => c)).IsBetween(1, 5);

        // One after another, across replicas: exactly up to the limit.
        var counted = new List<bool>();
        for (var i = 0; i < 6; i++)
        {
            counted.Add(await (i % 2 == 0 ? one : two).TryCountAsync("contact", "abc", Noon, CountWindow.Hour, 5, CancellationToken.None));
        }

        await Assert.That(counted.Count(c => c)).IsEqualTo(5);
        await Assert.That(await one.TryUncountAsync("contact", "abc", Noon, CountWindow.Hour, CancellationToken.None)).IsTrue();
        await Assert.That(await two.TryCountAsync("contact", "abc", Noon, CountWindow.Hour, 5, CancellationToken.None)).IsTrue();
        await Assert.That(await two.TryUncountAsync("reaction", "abc", Noon, CountWindow.Day, CancellationToken.None)).IsFalse();
        await Assert.That(await one.TryCountAsync("posts/a.md|like", "abc", Noon, CountWindow.Day, 1, CancellationToken.None)).IsTrue();
        await Assert.That(await two.TryCountAsync("posts/a.md|like", "abc", Noon, CountWindow.Day, 1, CancellationToken.None)).IsFalse();

        await Assert.That(await one.TryUseChallengeAsync("c0ffee", Noon, CancellationToken.None)).IsTrue();
        await Assert.That(await two.TryUseChallengeAsync("c0ffee", Noon, CancellationToken.None)).IsFalse();

        await one.TryCountAsync("contact", "old", Noon.AddDays(-3), CountWindow.Hour, 5, CancellationToken.None);
        var deleted = await one.CleanUpAsync(DateOnly.FromDateTime(Noon.UtcDateTime), CancellationToken.None);
        await Assert.That(deleted).IsEqualTo(1);
        await Assert.That(await one.TryUseChallengeAsync("c0ffee", Noon, CancellationToken.None)).IsFalse();
    }

    private static VisitorOptions Options() =>
        new(ContactPerHour: 5, CommentsPerHour: 5, CommentChangesPerHour: 20, ReactionsPerHour: 30, ReactionsPerHourPerAddress: 120, TimeSpan.FromSeconds(3), TimeSpan.FromHours(24), ProofMaxNumber: 2000);

    private static ProofOfWork Proof(TimeProvider clock, out MemoryVisitorStore store)
    {
        store = new MemoryVisitorStore();
        return new ProofOfWork(new EphemeralDataProtectionProvider(), store, clock, Options());
    }
}
