using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;

namespace Weesite.Web.Visitors;

/// <summary>Where a comment is in moderation.</summary>
internal enum CommentStatus
{
    /// <summary>New, not yet approved: only its writer's browser shows it.</summary>
    Pending,

    /// <summary>Public on its post. An edit may wait beside it (<see cref="Comment.PendingText"/>).</summary>
    Approved,

    /// <summary>Not published; deleted after <see cref="CommentCleanUp.RejectedDays"/> days.</summary>
    Rejected,

    /// <summary>
    /// Deleted by its writer once public: its place in the thread stays, marked, without its name
    /// or text, so the replies around it still read (T-0172).
    /// </summary>
    Deleted,

    /// <summary>
    /// Published, then rejected by a moderator: its place stays marked as removed by a moderator, as
    /// a deleted one's does. Its writer can no longer edit or delete it; its name and text go after
    /// <see cref="CommentCleanUp.RejectedDays"/> days, and the marker stays (T-0180).
    /// </summary>
    Removed,
}

/// <summary>
/// A comment on a post (ADR 0011). Nothing about its writer is kept but the name they gave and what
/// they wrote: no address, no hash. The token that lets their browser edit or delete it is kept only
/// as its SHA-256.
/// </summary>
/// <param name="Id">Its ID: the time it was written, then random hex, so IDs sort in time order.</param>
/// <param name="Post">The post's source path, such as <c>posts/hello.md</c>: a changed slug doesn't change it.</param>
/// <param name="Name">The name its writer gave, shown with it.</param>
/// <param name="Text">The text: the public one once approved; while pending, the one waiting.</param>
/// <param name="PendingText">An approved comment's edit, waiting for moderation; null when there's none.</param>
/// <param name="Status">Where it is in moderation.</param>
/// <param name="Created">When it was written.</param>
/// <param name="Edited">When its writer last edited it, if they did.</param>
/// <param name="Moderated">When an editor last approved or rejected it, if one did.</param>
/// <param name="TokenHash">The SHA-256 of its token, lowercase hex.</param>
internal sealed record Comment(
    string Id,
    string Post,
    string Name,
    string Text,
    string? PendingText,
    CommentStatus Status,
    DateTimeOffset Created,
    DateTimeOffset? Edited,
    DateTimeOffset? Moderated,
    string TokenHash)
{
    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdef");

    /// <summary>Gets a value indicating whether it waits for an editor: new, or an approved one with an edit.</summary>
    public bool Waiting => Status == CommentStatus.Pending || (Status == CommentStatus.Approved && PendingText is not null);

    /// <summary>Gets who wrote it, when a signed-in editor did: <c>author</c> (the post's author) or
    /// <c>editor</c>; null for a visitor (T-0161).</summary>
    public string? By { get; init; }

    /// <summary>Gets when its public text last changed: an approved edit, or a moderator's own edit
    /// of a published comment (F-0204); null while it's as first published.</summary>
    public DateTimeOffset? Revised { get; init; }

    /// <summary>Gets when its writer last wrote it: created, or edited.</summary>
    public DateTimeOffset Written => Edited ?? Created;

    /// <summary>Gets the version the store read it at (its ETag): a change saves only at the version
    /// it read, so two changes can't overwrite each other (F-0209). Null for one not yet kept.</summary>
    public string? Version { get; init; }

    /// <summary>Gets a short stamp of <see cref="Version"/> that moderators send back with a decision,
    /// so it applies only to the comment as they read it (F-0209); null for one not yet kept.</summary>
    public string? Stamp => Version is null ? null : Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Version)))[..16];

    /// <summary>Makes an ID for a comment written at a moment.</summary>
    /// <param name="at">When.</param>
    /// <returns>19 digits of ticks and 8 random hex digits.</returns>
    public static string NewId(DateTimeOffset at) =>
        string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks:D19}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");

    /// <summary>Checks that a text is an ID as <see cref="NewId"/> makes them.</summary>
    /// <param name="id">The text.</param>
    /// <returns>True for 19 digits and 8 lowercase hex digits.</returns>
    public static bool IsId(string? id) =>
        id is { Length: 27 } && id.AsSpan(0, 19).IndexOfAnyExceptInRange('0', '9') < 0
        && !id.AsSpan(19).ContainsAnyExcept(HexDigits);

    /// <summary>Gets the status as the browser and apps see it.</summary>
    public string StatusName => Status switch
    {
        CommentStatus.Approved => "approved",
        CommentStatus.Rejected => "rejected",
        CommentStatus.Deleted => "deleted",
        CommentStatus.Removed => "removed",
        _ => "pending",
    };

    /// <summary>Makes a new token: 32 random bytes, base64url.</summary>
    /// <returns>The token, to give the writer once.</returns>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The SHA-256 of a token, as kept.</summary>
    /// <param name="token">The token.</param>
    /// <returns>Lowercase hex.</returns>
    public static string HashOf(string token) => SecretHash.Of(token);

    /// <summary>Whether a token is this comment's, compared in fixed time.</summary>
    /// <param name="token">The token sent.</param>
    /// <returns>True when it matches.</returns>
    public bool HasToken(string? token) =>
        !string.IsNullOrEmpty(token)
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(HashOf(token)), System.Text.Encoding.ASCII.GetBytes(TokenHash));
}

/// <summary>Keeps the comments (ADR 0011), and a version that moves with every change, so each replica knows when to reload.</summary>
internal interface ICommentStore
{
    /// <summary>
    /// Adds a comment, or replaces one at the version it was read at (F-0209): a comment without a
    /// <see cref="Comment.Version"/> is added; one with a version replaces the kept one only while
    /// that's still at the version.
    /// </summary>
    /// <param name="comment">The comment.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>The comment as kept, with its new version; null when it changed or went since it was
    /// read, or, for a new one, when one with its ID is already there: read it again.</returns>
    Task<Comment?> SaveAsync(Comment comment, CancellationToken cancellationToken);

    /// <summary>Finds a comment.</summary>
    /// <param name="post">The post's source path.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The comment, or null.</returns>
    Task<Comment?> GetAsync(string post, string id, CancellationToken cancellationToken);

    /// <summary>Deletes a comment.</summary>
    /// <param name="post">The post's source path.</param>
    /// <param name="id">The comment's ID.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <param name="version">Only while it's at this version (F-0209); null: whatever it is now.</param>
    /// <returns>True when there was one (at that version).</returns>
    Task<bool> DeleteAsync(string post, string id, CancellationToken cancellationToken, string? version = null);

    /// <summary>Lists every comment, oldest first. A personal site has few: the lists filter in memory.</summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The comments.</returns>
    Task<IReadOnlyList<Comment>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Gets the version, which every change moves.</summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>An opaque text.</returns>
    Task<string> VersionAsync(CancellationToken cancellationToken);
}

/// <summary>Comments in memory: content from files (tests).</summary>
internal sealed class MemoryCommentStore : ICommentStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Post, string Id), Comment> _comments = [];
    private long _version;

    /// <inheritdoc />
    public Task<Comment?> SaveAsync(Comment comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);
        lock (_lock)
        {
            var key = (comment.Post, comment.Id);
            var kept = _comments.GetValueOrDefault(key);
            var stale = comment.Version is null
                ? kept is not null
                : kept is null || !string.Equals(kept.Version, comment.Version, StringComparison.Ordinal);
            if (stale)
            {
                return Task.FromResult<Comment?>(null);
            }

            _version++;
            var saved = comment with { Version = _version.ToString(CultureInfo.InvariantCulture) };
            _comments[key] = saved;
            return Task.FromResult<Comment?>(saved);
        }
    }

    /// <inheritdoc />
    public Task<Comment?> GetAsync(string post, string id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_comments.GetValueOrDefault((post, id)));
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string post, string id, CancellationToken cancellationToken, string? version = null)
    {
        lock (_lock)
        {
            if (version is not null && !string.Equals(_comments.GetValueOrDefault((post, id))?.Version, version, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            var removed = _comments.Remove((post, id));
            if (removed)
            {
                _version++;
            }

            return Task.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Comment>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Comment>>([.. _comments.Values.OrderBy(c => c.Id, StringComparer.Ordinal)]);
        }
    }

    /// <inheritdoc />
    public Task<string> VersionAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_version.ToString(CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>
/// Comments in the <c>comments</c> table: one partition per post (its source path, made safe for a
/// key), the comment's ID as the row key, so a post's comments read in time order. The version is a
/// row of its own, in the <c>~state</c> partition, rewritten with every change.
/// </summary>
/// <remarks>
/// Each write marks its row with its own ID (<see cref="TableRows.MarkWrite"/>), so a write the SDK
/// sent again after it had landed, answered 409 or 412, or one whose every answer was lost, is
/// recognized as done rather than reported as someone else's change (F-0232). The version moves
/// after every change that landed, whatever happens to the request meanwhile; when moving it fails,
/// the next read of the version moves it first, so no replica keeps showing what changed.
/// </remarks>
/// <param name="tables">The table service.</param>
internal sealed class TableCommentStore(TableServiceClient tables) : ICommentStore
{
    private const string StatePartition = "~state";
    private const string VersionRow = "version";
    private readonly TableClient _table = tables.GetTableClient("comments");
    private readonly TableCreation _creation = new();

    // How many changes landed whose version move failed: the next read of the version moves it.
    private int _owed;

    /// <inheritdoc />
    public async Task<Comment?> SaveAsync(Comment comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);
        await _creation.EnsureAsync(_table, cancellationToken);
        var write = TableRows.NewWriteId();
        var entity = TableRows.MarkWrite(ToEntity(comment), write);
        Response response;
        try
        {
            // A new one is added, never put over another; a read one replaces only the version read.
            response = comment.Version is null
                ? await _table.AddEntityAsync(entity, cancellationToken)
                : await _table.UpdateEntityAsync(entity, new ETag(comment.Version), TableUpdateMode.Replace, cancellationToken);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        {
            // Someone else's change, or this one sent again after it had landed: then it stands.
            return await WrittenAsync(comment, write) is { } landed ? await TouchedAsync(landed) : null;
        }
        catch (Exception e) when (TableRows.MayHaveLanded(e))
        {
            // Every answer lost, or the request gone: the write may have landed all the same.
            if (await WrittenAsync(comment, write) is { } landed)
            {
                return await TouchedAsync(landed);
            }

            throw;
        }

        return await TouchedAsync(comment with { Version = response.Headers.ETag?.ToString() });
    }

    /// <inheritdoc />
    public async Task<Comment?> GetAsync(string post, string id, CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        var found = await _table.GetEntityIfExistsAsync<TableEntity>(VisitorKeys.Safe(post), VisitorKeys.Safe(id), cancellationToken: cancellationToken);
        return found.HasValue && found.Value is { } entity ? FromEntity(entity) : null;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string post, string id, CancellationToken cancellationToken, string? version = null)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        Response response;
        try
        {
            // A missing entity answers 404 without an exception; another version, 412 with one.
            response = await _table.DeleteEntityAsync(VisitorKeys.Safe(post), VisitorKeys.Safe(id), version is null ? ETag.All : new ETag(version), cancellationToken);
        }
        catch (RequestFailedException e) when (e.Status == 412)
        {
            return false;
        }
        catch (Exception e) when (TableRows.MayHaveLanded(e))
        {
            // It may have gone all the same: the version moves, which costs each replica a reload.
            await TouchAsync();
            throw;
        }

        // Gone already: someone else's delete, or this one sent again after it had landed and its
        // answer was lost. The version moves either way (F-0232).
        await TouchAsync();
        return response.Status != 404;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Comment>> ListAsync(CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        var comments = new List<Comment>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey != StatePartition, cancellationToken: cancellationToken))
        {
            comments.Add(FromEntity(entity));
        }

        return [.. comments.OrderBy(c => c.Id, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<string> VersionAsync(CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        if (Interlocked.Exchange(ref _owed, 0) is var owed and > 0)
        {
            try
            {
                await MoveVersionAsync(cancellationToken);
            }
            catch
            {
                Interlocked.Add(ref _owed, owed);
                throw;
            }
        }

        var found = await _table.GetEntityIfExistsAsync<TableEntity>(StatePartition, VersionRow, cancellationToken: cancellationToken);
        return found.HasValue && found.Value is { } entity ? entity.GetString("Value") ?? "" : "";
    }

    private static TableEntity ToEntity(Comment comment) => new(VisitorKeys.Safe(comment.Post), VisitorKeys.Safe(comment.Id))
    {
        ["Post"] = comment.Post,
        ["Name"] = comment.Name,
        ["Text"] = comment.Text,
        ["PendingText"] = comment.PendingText,
        ["Status"] = comment.Status.ToString(),
        ["Created"] = comment.Created.ToUniversalTime(),
        ["Edited"] = comment.Edited?.ToUniversalTime(),
        ["Moderated"] = comment.Moderated?.ToUniversalTime(),
        ["TokenHash"] = comment.TokenHash,
        ["By"] = comment.By,
        ["Revised"] = comment.Revised?.ToUniversalTime(),
    };

    private static Comment FromEntity(TableEntity entity) => new(
        entity.RowKey,
        entity.GetString("Post") ?? "",
        entity.GetString("Name") ?? "",
        entity.GetString("Text") ?? "",
        entity.GetString("PendingText"),
        Enum.TryParse<CommentStatus>(entity.GetString("Status"), out var status) ? status : CommentStatus.Pending,
        entity.GetDateTimeOffset("Created") ?? DateTimeOffset.MinValue,
        entity.GetDateTimeOffset("Edited"),
        entity.GetDateTimeOffset("Moderated"),
        entity.GetString("TokenHash") ?? "")
    {
        By = entity.GetString("By"),
        Revised = entity.GetDateTimeOffset("Revised"),
        Version = entity.ETag.ToString(),
    };

    // The comment as kept, if the write with this ID changed it; null when it didn't or can't be read.
    private async Task<Comment?> WrittenAsync(Comment comment, string write)
    {
        try
        {
            var row = await _table.GetEntityIfExistsAsync<TableEntity>(VisitorKeys.Safe(comment.Post), VisitorKeys.Safe(comment.Id), cancellationToken: CancellationToken.None);
            return row.HasValue && row.Value is { } entity && TableRows.HasWrite(entity, write) ? FromEntity(entity) : null;
        }
        catch (Exception e) when (TableRows.MayHaveLanded(e))
        {
            return null;
        }
    }

    private async Task<Comment> TouchedAsync(Comment saved)
    {
        await TouchAsync();
        return saved;
    }

    // Moves the version after a change that landed: not with the request's token, since a request
    // gone after its write must still move it. A failure is owed to the next read of the version.
    private async Task TouchAsync()
    {
        try
        {
            await MoveVersionAsync(CancellationToken.None);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _owed);
        }
    }

    // A new random version: every replica that sees it change reloads.
    private async Task MoveVersionAsync(CancellationToken cancellationToken) =>
        await _table.UpsertEntityAsync(new TableEntity(StatePartition, VersionRow) { ["Value"] = Guid.NewGuid().ToString("N") }, TableUpdateMode.Replace, cancellationToken);
}
