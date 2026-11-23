using Azure.Data.Tables;
using Weesite.Tests;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>The full review's findings on the contact form's digest (T-0177): F-0231, F-0250.</summary>
internal sealed class ContactFullReviewTests
{
    [Test]
    [Category("containers")]
    public async Task The_digest_reads_messages_by_their_keys_and_keeps_what_it_owes()
    {
        await using var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var store = new TableMessageStore(tables);
        var after = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        var until = after.AddHours(1);

        // F-0250: by row keys across the month partitions, not by a scan on Received: the range
        // leaves out its start and keeps its end, as before.
        var times = new[] { after.AddTicks(-1), after, after.AddTicks(1), after.AddMinutes(45), until, until.AddTicks(1) };
        foreach (var (at, i) in times.Select((at, i) => (at, i)))
        {
            await store.AddAsync(new ContactMessage(ContactMessage.NewId(at), $"M{i}", "m@example.org", "Text.", at, Read: false, Unverified: false), CancellationToken.None);
        }

        var received = await store.ReceivedAsync(after, until, CancellationToken.None);

        await Assert.That(received.Select(m => m.Name)).IsEquivalentTo(["M2", "M3", "M4"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        // F-0231: the recipients a digest still owes, and the start of their range, are kept.
        var states = new TableDigestStateStore(tables);
        var version = await states.TrySetAsync("comments", new DigestState(until, until, null) { Owed = ["a@example.com", "b@example.com"], OwedAfter = after }, CancellationToken.None);
        var read = await states.GetAsync("comments", CancellationToken.None);
        await states.TrySetAsync("comments", read! with { Owed = [] }, CancellationToken.None);
        var cleared = await states.GetAsync("comments", CancellationToken.None);

        await Assert.That(version).IsNotNull();
        await Assert.That(read.Owed).IsEquivalentTo(["a@example.com", "b@example.com"]);
        await Assert.That(read.OwedAfter).IsEqualTo(after);
        await Assert.That(cleared!.Owed).IsEmpty();
    }
}
