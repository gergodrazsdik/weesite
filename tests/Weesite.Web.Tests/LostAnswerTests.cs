using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Testcontainers.Azurite;
using Weesite.Tests;
using Weesite.Web.Visitors;

namespace Weesite.Web.Tests;

/// <summary>
/// Writes whose answer is lost after they landed, as the Azure SDK meets them on a flaky network:
/// it sends the write again, which the service answers 409 or 412, or every try fails. The stores
/// mark each write with its own ID (F-0215, F-0232) and recognize it as done; these drive them
/// through Azurite with a pipeline policy that loses the answers (VIS-01, VIS-07).
/// </summary>
internal sealed class LostAnswerTests
{
    private const string PostPath = "posts/hello-world.md";

    [Test]
    [Category("containers")]
    public async Task A_comment_change_whose_answer_was_lost_counts_and_moves_the_version()
    {
        await using var azurite = await StartAsync();
        var lose = new LoseAnswers();
        var store = new TableCommentStore(Tables(azurite, lose));
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var first = new Comment(Comment.NewId(now), PostPath, "Ada", "One", null, CommentStatus.Pending, now, null, null, "hash");

        // An add sent again after it landed answers 409: it's this one, not another comment.
        var before = await store.VersionAsync(CancellationToken.None);
        lose.Next(CommentWrites);
        var added = await store.SaveAsync(first, CancellationToken.None);
        var afterAdd = await store.VersionAsync(CancellationToken.None);

        // An update sent again answers 412: it's this one, not someone else's change.
        lose.Next(CommentWrites);
        var approved = await store.SaveAsync(added! with { Status = CommentStatus.Approved }, CancellationToken.None);
        var afterUpdate = await store.VersionAsync(CancellationToken.None);

        // Every try's answer lost: the SDK gives up, and the write ID still tells it landed.
        lose.Next(CommentWrites, times: 10);
        var edited = await store.SaveAsync(approved! with { Text = "Edited" }, CancellationToken.None);
        var afterEdit = await store.VersionAsync(CancellationToken.None);

        // The request gone just after the write landed: the version still moves.
        using var gone = new CancellationTokenSource();
        lose.Next(CommentWrites, cancel: gone);
        var removed = await store.SaveAsync(edited! with { Status = CommentStatus.Removed }, gone.Token);
        var afterRemove = await store.VersionAsync(CancellationToken.None);

        // A delete sent again answers 404: the version moves all the same.
        lose.Next(CommentWrites);
        await store.DeleteAsync(PostPath, first.Id, CancellationToken.None, removed!.Version);
        var afterDelete = await store.VersionAsync(CancellationToken.None);

        await Assert.That(added).IsNotNull();
        await Assert.That(approved?.Status).IsEqualTo(CommentStatus.Approved);
        await Assert.That(edited?.Text).IsEqualTo("Edited");
        await Assert.That(removed.Status).IsEqualTo(CommentStatus.Removed);
        await Assert.That(new[] { before, afterAdd, afterUpdate, afterEdit, afterRemove, afterDelete }.Distinct().Count()).IsEqualTo(6);
        await Assert.That(await store.ListAsync(CancellationToken.None)).IsEmpty();
        await Assert.That(lose.Lost).IsGreaterThanOrEqualTo(6);
    }

    [Test]
    [Category("containers")]
    public async Task A_stale_comment_change_is_still_refused()
    {
        await using var azurite = await StartAsync();
        var store = new TableCommentStore(Tables(azurite, new LoseAnswers()));
        var now = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var kept = await store.SaveAsync(new Comment(Comment.NewId(now), PostPath, "Ada", "One", null, CommentStatus.Pending, now, null, null, "hash"), CancellationToken.None);
        await store.SaveAsync(kept! with { Text = "Two" }, CancellationToken.None);

        // Someone else's change meanwhile: no write ID of this save's on the row, so it's refused.
        await Assert.That(await store.SaveAsync(kept with { Text = "Stale" }, CancellationToken.None)).IsNull();
        await Assert.That((await store.GetAsync(PostPath, kept.Id, CancellationToken.None))!.Text).IsEqualTo("Two");
    }

    [Test]
    [Category("containers")]
    public async Task A_counter_or_challenge_whose_answer_was_lost_counts_once()
    {
        await using var azurite = await StartAsync();
        var lose = new LoseAnswers();
        var tables = Tables(azurite, lose);
        var store = new TableVisitorStore(tables);
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        lose.Next(VisitorWrites);
        var first = await store.TryCountAsync("comment", "hash", at, CountWindow.Hour, 5, CancellationToken.None);
        lose.Next(VisitorWrites);
        var second = await store.TryCountAsync("comment", "hash", at, CountWindow.Hour, 5, CancellationToken.None);
        var counted = await CountAsync(tables, at);
        lose.Next(VisitorWrites);
        var uncounted = await store.TryUncountAsync("comment", "hash", at, CountWindow.Hour, CancellationToken.None);
        var afterUncount = await CountAsync(tables, at);

        var expires = at.AddMinutes(10);
        lose.Next(VisitorWrites);
        var used = await store.TryUseChallengeAsync("challenge", expires, CancellationToken.None);
        var again = await store.TryUseChallengeAsync("challenge", expires, CancellationToken.None);

        await Assert.That((first, second, uncounted)).IsEqualTo((true, true, true));
        await Assert.That(counted).IsEqualTo(2);
        await Assert.That(afterUncount).IsEqualTo(1);
        await Assert.That(used).IsTrue();
        await Assert.That(again).IsFalse();
        await Assert.That(lose.Lost).IsEqualTo(4);
    }

    [Test]
    [Category("containers")]
    public async Task A_reaction_whose_answer_was_lost_counts_once()
    {
        await using var azurite = await StartAsync();
        var lose = new LoseAnswers();
        var store = new TableReactionStore(Tables(azurite, lose));
        var at = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        // The total's first write: its answer lost, the SDK's resend answered 409.
        lose.Next(m => Writes(m, "reactions"));
        var one = await store.AddAsync(PostPath, ReactionKind.Like, 1, at, CancellationToken.None);

        // The total's update: every try's answer lost, so the SDK gives up; the write ID tells it
        // landed, and its event stays.
        lose.Next(m => Writes(m, "reactions"), times: 10);
        var two = await store.AddAsync(PostPath, ReactionKind.Like, 1, at, CancellationToken.None);

        // The event's add: its answer lost, the resend answered 409.
        lose.Next(m => Writes(m, "reactionevents"));
        var three = await store.AddAsync(PostPath, ReactionKind.Love, 1, at, CancellationToken.None);

        var events = new List<ReactionEvent>();
        await foreach (var e in store.ReadEventsAsync(DateOnly.FromDateTime(at.UtcDateTime), DateOnly.FromDateTime(at.UtcDateTime), CancellationToken.None))
        {
            events.Add(e);
        }

        await Assert.That(one.Like).IsEqualTo(1);
        await Assert.That(two.Like).IsEqualTo(2);
        await Assert.That((three.Like, three.Love)).IsEqualTo((2, 1));
        await Assert.That(await store.GetAsync(PostPath, CancellationToken.None)).IsEqualTo(three);
        await Assert.That(events.Count).IsEqualTo(3);
        await Assert.That(lose.Lost).IsGreaterThanOrEqualTo(3);
    }

    private static async Task<AzuriteContainer> StartAsync()
    {
        var azurite = TestAzurite.Create();
        await azurite.StartAsync();
        return azurite;
    }

    // The table service, with the policy in every try and quick retries.
    private static TableServiceClient Tables(AzuriteContainer azurite, LoseAnswers lose)
    {
        var options = new TableClientOptions();
        options.Retry.Delay = TimeSpan.FromMilliseconds(5);
        options.Retry.MaxDelay = TimeSpan.FromMilliseconds(20);
        options.AddPolicy(lose, HttpPipelinePosition.PerRetry);
        return new TableServiceClient(azurite.GetConnectionString(), options);
    }

    private static async Task<int> CountAsync(TableServiceClient tables, DateTimeOffset at)
    {
        var row = await tables.GetTableClient("visitorlimits").GetEntityAsync<TableEntity>(VisitorKeys.Day(at), VisitorKeys.Counter("comment", "hash", at, CountWindow.Hour));
        return row.Value.GetInt32("Count") ?? 0;
    }

    // A comment's own write: not the version row's.
    private static bool CommentWrites(HttpMessage message) =>
        Writes(message, "comments") && !message.Request.Uri.Path.Contains("version", StringComparison.Ordinal);

    private static bool VisitorWrites(HttpMessage message) => Writes(message, "visitorlimits");

    // A write to a table's rows: an add, an update or a delete, not a read or the table's creation.
    private static bool Writes(HttpMessage message, string table)
    {
        var path = message.Request.Uri.Path;
        var last = path[(path.LastIndexOf('/', StringComparison.Ordinal) + 1)..];
        var paren = last.IndexOf('(', StringComparison.Ordinal);
        return message.Request.Method != RequestMethod.Get && (paren < 0 ? last : last[..paren]) == table;
    }

    /// <summary>
    /// Lets matching requests reach the service, then loses their answers: the SDK sees a broken
    /// connection and sends them again, or, given a token to cancel, sees the request cancelled.
    /// </summary>
    private sealed class LoseAnswers : HttpPipelinePolicy
    {
        private Func<HttpMessage, bool> _match = _ => false;
        private int _times;
        private CancellationTokenSource? _cancel;

        public int Lost { get; private set; }

        public void Next(Func<HttpMessage, bool> match, int times = 1, CancellationTokenSource? cancel = null)
        {
            _match = match;
            _times = times;
            _cancel = cancel;
        }

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            ProcessNext(message, pipeline);
            Lose(message);
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            await ProcessNextAsync(message, pipeline);
            Lose(message);
        }

        private void Lose(HttpMessage message)
        {
            if (_times <= 0 || !_match(message))
            {
                return;
            }

            _times--;
            Lost++;
            if (_cancel is { } cancel)
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            }

            throw new IOException("The answer was lost (test).");
        }
    }
}
