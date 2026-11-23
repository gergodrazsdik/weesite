using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Azure;
using Azure.Data.Tables;

namespace Weesite.Web.Visitors;

/// <summary>A message sent with the contact form (ADR 0011). Nothing else about the sender is kept.</summary>
/// <param name="Id">The message's ID: its row key, newest first.</param>
/// <param name="Name">The sender's name, as they gave it.</param>
/// <param name="Email">The sender's address, as they gave it.</param>
/// <param name="Text">The message, plain text.</param>
/// <param name="Received">When it arrived.</param>
/// <param name="Read">Whether an admin opened it.</param>
/// <param name="Unverified">Whether it came without a proof of work (no JavaScript, or a failed answer).</param>
internal sealed record ContactMessage(string Id, string Name, string Email, string Text, DateTimeOffset Received, bool Read, bool Unverified)
{
    private static readonly System.Buffers.SearchValues<char> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdef");

    /// <summary>Makes an ID for a message received at a moment: sorts newest first, and says the month.</summary>
    /// <param name="received">When it arrived.</param>
    /// <returns>19 digits of reversed ticks and 8 random hex digits.</returns>
    public static string NewId(DateTimeOffset received) =>
        string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.MaxValue.UtcTicks - received.UtcTicks:D19}{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");

    /// <summary>Reads when a message arrived from its ID.</summary>
    /// <param name="id">The ID.</param>
    /// <returns>The time, or null when the ID isn't one.</returns>
    public static DateTimeOffset? ReceivedOf(string? id) =>
        id is { Length: 27 } && long.TryParse(id.AsSpan(0, 19), NumberStyles.None, CultureInfo.InvariantCulture, out var reversed)
            && reversed is >= 0 && reversed <= DateTimeOffset.MaxValue.UtcTicks
            && !id.AsSpan(19).ContainsAnyExcept(HexDigits)
            ? new DateTimeOffset(DateTimeOffset.MaxValue.UtcTicks - reversed, TimeSpan.Zero)
            : null;
}

/// <summary>A page of messages and how many there are.</summary>
/// <param name="Messages">The page, newest first.</param>
/// <param name="Total">How many messages there are in all.</param>
/// <param name="Unread">How many of them are unread.</param>
internal sealed record MessagePage(IReadOnlyList<ContactMessage> Messages, int Total, int Unread);

/// <summary>Keeps the contact form's messages (ADR 0011).</summary>
internal interface IMessageStore
{
    /// <summary>Adds a message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task.</returns>
    Task AddAsync(ContactMessage message, CancellationToken cancellationToken);

    /// <summary>Lists a page of messages, newest first.</summary>
    /// <param name="skip">How many to skip.</param>
    /// <param name="take">How many to return.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The page.</returns>
    Task<MessagePage> ListAsync(int skip, int take, CancellationToken cancellationToken);

    /// <summary>Finds a message.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The message, or null.</returns>
    Task<ContactMessage?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>Marks a message read.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>True when it was unread.</returns>
    Task<bool> MarkReadAsync(string id, CancellationToken cancellationToken);

    /// <summary>Deletes a message.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>True when there was one.</returns>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>Deletes every message that has been read.</summary>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>How many went.</returns>
    Task<int> DeleteReadAsync(CancellationToken cancellationToken);

    /// <summary>Counts the unread messages.</summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The count.</returns>
    Task<int> CountUnreadAsync(CancellationToken cancellationToken);

    /// <summary>Lists the messages received in a time range, oldest first: for the digest.</summary>
    /// <param name="after">The start, left out.</param>
    /// <param name="until">The end, included.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The messages.</returns>
    Task<IReadOnlyList<ContactMessage>> ReceivedAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>Deletes messages received before a moment.</summary>
    /// <param name="before">The moment.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>How many went.</returns>
    Task<int> DeleteOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary>Messages in memory: content from files (tests).</summary>
internal sealed class MemoryMessageStore : IMessageStore
{
    private readonly Lock _lock = new();
    private readonly SortedDictionary<string, ContactMessage> _messages = new(StringComparer.Ordinal);

    /// <summary>Gets every message, newest first, for tests.</summary>
    public IReadOnlyList<ContactMessage> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _messages.Values];
            }
        }
    }

    /// <inheritdoc />
    public Task AddAsync(ContactMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
        {
            _messages[message.Id] = message;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MessagePage> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(new MessagePage([.. _messages.Values.Skip(skip).Take(take)], _messages.Count, _messages.Values.Count(m => !m.Read)));
        }
    }

    /// <inheritdoc />
    public Task<ContactMessage?> GetAsync(string id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(id is not null && _messages.TryGetValue(id, out var message) ? message : null);
        }
    }

    /// <inheritdoc />
    public Task<bool> MarkReadAsync(string id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (id is null || !_messages.TryGetValue(id, out var message) || message.Read)
            {
                return Task.FromResult(false);
            }

            _messages[id] = message with { Read = true };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(id is not null && _messages.Remove(id));
        }
    }

    /// <inheritdoc />
    public Task<int> DeleteReadAsync(CancellationToken cancellationToken) => Remove(m => m.Read);

    /// <inheritdoc />
    public Task<int> CountUnreadAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_messages.Values.Count(m => !m.Read));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContactMessage>> ReceivedAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ContactMessage>>([.. _messages.Values.Where(m => m.Received > after && m.Received <= until).Reverse()]);
        }
    }

    /// <inheritdoc />
    public Task<int> DeleteOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken) => Remove(m => m.Received < before);

    private Task<int> Remove(Func<ContactMessage, bool> which)
    {
        lock (_lock)
        {
            var ids = _messages.Values.Where(which).Select(m => m.Id).ToList();
            foreach (var id in ids)
            {
                _messages.Remove(id);
            }

            return Task.FromResult(ids.Count);
        }
    }
}

/// <summary>
/// Messages in the <c>messages</c> table: one partition per month received (<c>yyyy-MM</c>, UTC),
/// row keys newest first (<see cref="ContactMessage.NewId"/>), so the ID alone finds the row.
/// A page of the list reads every message's keys and read flag, a few bytes each, for the order
/// and the counts, and only the page's messages whole (F-0225). The unread count the admin's menu
/// shows is kept for <see cref="UnreadFor"/>, and forgotten when this replica changes a message.
/// </summary>
/// <param name="tables">The table service.</param>
internal sealed class TableMessageStore(TableServiceClient tables) : IMessageStore
{
    /// <summary>How long the unread count is kept: a message another replica took shows within it.</summary>
    public static readonly TimeSpan UnreadFor = TimeSpan.FromSeconds(30);

    private readonly TableClient _table = tables.GetTableClient("messages");
    private readonly TableCreation _creation = new();
    private UnreadCount? _unread;

    /// <inheritdoc />
    public async Task AddAsync(ContactMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _creation.EnsureAsync(_table, cancellationToken);
        await _table.AddEntityAsync(ToEntity(message), cancellationToken);
        ForgetUnread();
    }

    /// <inheritdoc />
    public async Task<MessagePage> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);

        // Row keys sort newest first across the month partitions too, as the IDs do.
        var keys = new List<(string Partition, string Id, bool Read)>();
        await foreach (var entity in _table.QueryAsync<TableEntity>((string?)null, select: ["PartitionKey", "RowKey", "Read"], cancellationToken: cancellationToken))
        {
            keys.Add((entity.PartitionKey, entity.RowKey, entity.GetBoolean("Read") ?? false));
        }

        keys.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        var unread = keys.Count(k => !k.Read);
        Volatile.Write(ref _unread, new UnreadCount(unread, Environment.TickCount64));

        // A page's messages are a run of row keys in each partition: one query per partition reads them.
        var messages = new List<ContactMessage>();
        foreach (var partition in keys.Skip(skip).Take(take).GroupBy(k => k.Partition))
        {
            var first = partition.First().Id;
            var last = partition.Last().Id;
            var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition.Key} and RowKey ge {first} and RowKey le {last}");
            await foreach (var message in ReadAllAsync(filter, cancellationToken))
            {
                messages.Add(message);
            }
        }

        return new MessagePage([.. messages.OrderBy(m => m.Id, StringComparer.Ordinal)], keys.Count, unread);
    }

    /// <inheritdoc />
    public async Task<ContactMessage?> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (ContactMessage.ReceivedOf(id) is not { } received)
        {
            return null;
        }

        await _creation.EnsureAsync(_table, cancellationToken);
        var entity = await _table.GetEntityIfExistsAsync<TableEntity>(Partition(received), id, cancellationToken: cancellationToken);
        return entity.HasValue && entity.Value is { } found ? FromEntity(found) : null;
    }

    /// <inheritdoc />
    public async Task<bool> MarkReadAsync(string id, CancellationToken cancellationToken)
    {
        if (ContactMessage.ReceivedOf(id) is not { } received)
        {
            return false;
        }

        await _creation.EnsureAsync(_table, cancellationToken);
        var entity = await _table.GetEntityIfExistsAsync<TableEntity>(Partition(received), id, cancellationToken: cancellationToken);
        if (!entity.HasValue || entity.Value is not { } found || found.GetBoolean("Read") == true)
        {
            return false;
        }

        try
        {
            await _table.UpdateEntityAsync(new TableEntity(found.PartitionKey, found.RowKey) { ["Read"] = true }, found.ETag, TableUpdateMode.Merge, cancellationToken);
            ForgetUnread();
            return true;
        }
        catch (RequestFailedException e) when (e.Status is 404 or 412)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (ContactMessage.ReceivedOf(id) is not { } received)
        {
            return false;
        }

        await _creation.EnsureAsync(_table, cancellationToken);

        // The client answers a missing row with its 404 rather than an exception.
        var response = await _table.DeleteEntityAsync(Partition(received), id, ETag.All, cancellationToken);
        ForgetUnread();
        return response.Status != 404;
    }

    /// <inheritdoc />
    public async Task<int> DeleteReadAsync(CancellationToken cancellationToken) =>
        await DeleteRowsAsync("Read eq true", cancellationToken);

    /// <inheritdoc />
    public async Task<int> CountUnreadAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _unread) is { } kept && Environment.TickCount64 - kept.At < (long)UnreadFor.TotalMilliseconds)
        {
            return kept.Count;
        }

        await _creation.EnsureAsync(_table, cancellationToken);
        var count = 0;
        await foreach (var _ in _table.QueryAsync<TableEntity>("Read eq false", select: ["PartitionKey"], cancellationToken: cancellationToken))
        {
            count++;
        }

        Volatile.Write(ref _unread, new UnreadCount(count, Environment.TickCount64));
        return count;
    }

    /// <inheritdoc />
    /// <remarks>
    /// By keys, not by the <c>Received</c> property, which the service would find only by reading
    /// every row (F-0250): the months' partitions, and in them the row keys, which begin with the
    /// reversed time. A key begins with the 19 digits of its time; so a message received after
    /// <paramref name="after"/> has a key below that time's digits, and one received by
    /// <paramref name="until"/> a key at or above them.
    /// </remarks>
    public async Task<IReadOnlyList<ContactMessage>> ReceivedAsync(DateTimeOffset after, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var from = Reversed(until);
        var before = Reversed(after);
        var filter = TableClient.CreateQueryFilter($"PartitionKey ge {Partition(after)} and PartitionKey le {Partition(until)} and RowKey ge {from} and RowKey lt {before}");
        var found = new List<ContactMessage>();
        await foreach (var message in ReadAllAsync(filter, cancellationToken))
        {
            found.Add(message);
        }

        return [.. found.OrderBy(m => m.Received)];
    }

    /// <inheritdoc />
    public async Task<int> DeleteOlderThanAsync(DateTimeOffset before, CancellationToken cancellationToken) =>
        await DeleteRowsAsync(TableClient.CreateQueryFilter($"Received lt {before.ToUniversalTime()}"), cancellationToken);

    private static string Partition(DateTimeOffset received) => received.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // The first 19 digits of the IDs of the messages received at a moment (ContactMessage.NewId).
    private static string Reversed(DateTimeOffset at) =>
        (DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks).ToString("D19", CultureInfo.InvariantCulture);

    private static TableEntity ToEntity(ContactMessage message) => new(Partition(message.Received), message.Id)
    {
        ["Name"] = message.Name,
        ["Email"] = message.Email,
        ["Text"] = message.Text,
        ["Received"] = message.Received.ToUniversalTime(),
        ["Read"] = message.Read,
        ["Unverified"] = message.Unverified,
    };

    private static ContactMessage FromEntity(TableEntity entity) => new(
        entity.RowKey,
        entity.GetString("Name") ?? "",
        entity.GetString("Email") ?? "",
        entity.GetString("Text") ?? "",
        entity.GetDateTimeOffset("Received") ?? DateTimeOffset.MinValue,
        entity.GetBoolean("Read") ?? false,
        entity.GetBoolean("Unverified") ?? false);

    private async IAsyncEnumerable<ContactMessage> ReadAllAsync(string? filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: cancellationToken))
        {
            yield return FromEntity(entity);
        }
    }

    private async Task<int> DeleteRowsAsync(string filter, CancellationToken cancellationToken)
    {
        await _creation.EnsureAsync(_table, cancellationToken);
        var deleted = await TableRows.DeleteAllAsync(_table, filter, cancellationToken);
        ForgetUnread();
        return deleted;
    }

    private void ForgetUnread() => Volatile.Write(ref _unread, null);

    // The unread count, and when it was counted (Environment.TickCount64).
    private sealed record UnreadCount(int Count, long At);
}

/// <summary>Deletes messages older than 12 months, once a day (ADR 0011).</summary>
/// <param name="store">The messages.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class MessageCleanUp(IMessageStore store, TimeProvider time, ILogger<MessageCleanUp> logger) : BackgroundService
{
    /// <summary>How many months messages are kept at most.</summary>
    public const int RetentionMonths = 12;

    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    /// <summary>Deletes what's older than the retention.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many went.</returns>
    public Task<int> CleanUpAsync(CancellationToken cancellationToken) =>
        store.DeleteOlderThanAsync(time.GetUtcNow().AddMonths(-RetentionMonths), cancellationToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(7), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deleted = await CleanUpAsync(stoppingToken);
                    LogCleanedUp(logger, deleted);
                }
                catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogCleanUpFailed(logger, e);
                }

                await Task.Delay(Every, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Messages: deleted {Count} older than 12 months")]
    private static partial void LogCleanedUp(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Messages: the clean-up failed; it runs again tomorrow")]
    private static partial void LogCleanUpFailed(ILogger logger, Exception exception);
}
