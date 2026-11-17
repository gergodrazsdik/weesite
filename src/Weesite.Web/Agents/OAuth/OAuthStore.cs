using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;

namespace Weesite.Web.Agents.OAuth;

/// <summary>What the editor let an app do through the editor MCP endpoint (F-0070).</summary>
internal enum AppAccess
{
    /// <summary>Read, create drafts, edit items that are still drafts, upload media: nothing visitors see changes.</summary>
    Drafts = 0,

    /// <summary>Also edit published and scheduled items, keeping their status.</summary>
    Edit = 1,

    /// <summary>Also change any item's status: publish, schedule, unpublish, archive.</summary>
    Publish = 2,
}

/// <summary>A client that registered itself (RFC 7591), or one described by its metadata document.</summary>
/// <param name="Id">The client ID: random for a registered client, the document's URL otherwise.</param>
/// <param name="Name">The name it gave, shown on the consent page.</param>
/// <param name="RedirectUris">Where it may receive codes.</param>
/// <param name="FromDocument">Whether the client is described by a Client ID Metadata Document.</param>
internal sealed record OAuthClient(string Id, string Name, IReadOnlyList<string> RedirectUris, bool FromDocument);

/// <summary>What the clean-up needs to know about a client that registered itself (F-0060).</summary>
/// <param name="Id">The client ID.</param>
/// <param name="RegisteredAt">When it registered.</param>
/// <param name="Granted">Whether an editor ever granted it access.</param>
/// <param name="IdleSince">Since when it has had no grant, once the clean-up noticed; null while it has one.</param>
internal sealed record RegisteredClient(string Id, DateTimeOffset RegisteredAt, bool Granted, DateTimeOffset? IdleSince);

/// <summary>A code an editor's consent produced, waiting to be exchanged once.</summary>
/// <param name="Hash">The SHA-256 of the code (the code itself is never stored).</param>
/// <param name="ClientId">The client it was issued to.</param>
/// <param name="ClientName">The client's name, for the grant.</param>
/// <param name="RedirectUri">The redirect URI of the request, which the exchange must repeat.</param>
/// <param name="CodeChallenge">The PKCE challenge (S256).</param>
/// <param name="Email">The editor who consented.</param>
/// <param name="Resource">The resource the tokens are for.</param>
/// <param name="ExpiresAt">When the code stops working.</param>
/// <param name="Access">What the editor let the app do.</param>
/// <param name="UsedBy">The grant it was exchanged for, once it was; a second exchange revokes that grant (RFC 6749 §4.1.2).</param>
/// <param name="Reused">Whether a second exchange came before the first had stored its grant: the first then revokes it itself.</param>
internal sealed record AuthorizationCode(
    string Hash,
    string ClientId,
    string ClientName,
    string RedirectUri,
    string CodeChallenge,
    string Email,
    string Resource,
    DateTimeOffset ExpiresAt,
    AppAccess Access = AppAccess.Drafts,
    string? UsedBy = null,
    bool Reused = false);

/// <summary>An editor's consent to one client: the refresh token belongs to it, access tokens name it.</summary>
/// <param name="Id">The grant ID, the first part of the refresh token.</param>
/// <param name="Email">The editor.</param>
/// <param name="ClientId">The client.</param>
/// <param name="ClientName">The client's name when it was granted.</param>
/// <param name="Resource">The resource its tokens are for.</param>
/// <param name="CreatedAt">When the editor consented.</param>
/// <param name="UsedAt">When a refresh token was last exchanged.</param>
/// <param name="RefreshHash">The SHA-256 of the current refresh token's secret.</param>
/// <param name="PreviousRefreshHash">The one before: presenting it again means the token leaked.</param>
/// <param name="RefreshExpiresAt">When the current refresh token stops working.</param>
/// <param name="Access">What the editor let the app do.</param>
/// <param name="Version">The stored version, for replacing it only when nobody else has.</param>
/// <param name="RetiredRefreshHashes">
/// Older refresh tokens of the grant, space-separated, newest last (at most
/// <see cref="OAuthTokens.RetiredKept"/>): presenting one again means a token leaked.
/// </param>
/// <param name="Retried">Whether the current token came from a retry in the grace window: only one per rotation.</param>
internal sealed record OAuthGrant(
    string Id,
    string Email,
    string ClientId,
    string ClientName,
    string Resource,
    DateTimeOffset CreatedAt,
    DateTimeOffset UsedAt,
    string RefreshHash,
    string? PreviousRefreshHash,
    DateTimeOffset RefreshExpiresAt,
    AppAccess Access = AppAccess.Drafts,
    string? Version = null,
    string? RetiredRefreshHashes = null,
    bool Retried = false);

/// <summary>An upload address an app asked for (T-0137): one file, once, for a few minutes.</summary>
/// <param name="Hash">The SHA-256 of the upload token (the token itself is never stored).</param>
/// <param name="GrantId">The grant of the app that asked: revoking it ends the address too.</param>
/// <param name="Email">The editor the app works for.</param>
/// <param name="ClientName">The app's name, for the audit log.</param>
/// <param name="FileName">The file name the upload is stored under.</param>
/// <param name="Alt">An image's description.</param>
/// <param name="ExpiresAt">When the address stops working.</param>
internal sealed record MediaUploadTicket(
    string Hash,
    string GrantId,
    string Email,
    string ClientName,
    string FileName,
    string? Alt,
    DateTimeOffset ExpiresAt);

/// <summary>Where the authorization server keeps registered clients, codes, grants and upload addresses (ADR 0007).</summary>
internal interface IOAuthStore
{
    /// <summary>Saves a new upload address.</summary>
    /// <param name="ticket">The address.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task SaveUploadAsync(MediaUploadTicket ticket, CancellationToken cancellationToken);

    /// <summary>
    /// Takes an upload address: deletes it and returns it, expired or not. Only the first caller,
    /// on any replica, gets it; everyone after gets null.
    /// </summary>
    /// <param name="hash">The token's hash.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The address, or null.</returns>
    Task<MediaUploadTicket?> TakeUploadAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Counts a grant's upload addresses that haven't expired.</summary>
    /// <param name="grantId">The grant.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the count.</param>
    /// <returns>How many.</returns>
    Task<int> CountUploadsAsync(string grantId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Deletes the upload addresses that expired unused.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many it deleted.</returns>
    Task<int> DeleteExpiredUploadsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Saves a registered client.</summary>
    /// <param name="client">The client.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task SaveClientAsync(OAuthClient client, CancellationToken cancellationToken);

    /// <summary>Finds a registered client.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The client, or null.</returns>
    Task<OAuthClient?> FindClientAsync(string id, CancellationToken cancellationToken);

    /// <summary>Lists the registered clients, for the clean-up.</summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The clients.</returns>
    Task<IReadOnlyList<RegisteredClient>> ListClientsAsync(CancellationToken cancellationToken);

    /// <summary>Notes that a registered client has a grant: it was granted, and isn't idle.</summary>
    /// <param name="id">Its ID; nothing happens for a client that isn't stored (a metadata document's).</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task MarkClientGrantedAsync(string id, CancellationToken cancellationToken);

    /// <summary>Notes since when a registered client has had no grant.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="since">Since when.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task MarkClientIdleAsync(string id, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>Deletes a registered client.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>A task.</returns>
    Task DeleteClientAsync(string id, CancellationToken cancellationToken);

    /// <summary>Saves a new code.</summary>
    /// <param name="code">The code.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task SaveCodeAsync(AuthorizationCode code, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a code as exchanged for a grant and returns it as it was before: only the first caller,
    /// on any replica, gets it without <see cref="AuthorizationCode.UsedBy"/>. A used code is kept
    /// until <paramref name="keepUntil"/>, so that a second exchange learns which grant to revoke.
    /// </summary>
    /// <param name="hash">The code's hash.</param>
    /// <param name="grantId">The grant the exchange creates.</param>
    /// <param name="keepUntil">Until when to keep the used code.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The code (with <see cref="AuthorizationCode.UsedBy"/> set when it was used before), or null when it doesn't exist.</returns>
    Task<AuthorizationCode?> UseCodeAsync(string hash, string grantId, DateTimeOffset keepUntil, CancellationToken cancellationToken);

    /// <summary>Reads a code, used or not, without changing it.</summary>
    /// <param name="hash">The code's hash.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The code, or null when it doesn't exist.</returns>
    Task<AuthorizationCode?> FindCodeAsync(string hash, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a used code as exchanged again before its grant was stored, so the first exchange
    /// revokes the grant once it has stored it (T-0122).
    /// </summary>
    /// <param name="hash">The code's hash.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task.</returns>
    Task MarkCodeReusedAsync(string hash, CancellationToken cancellationToken);

    /// <summary>Deletes the codes past their time: unused ones that expired, used ones kept long enough.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>How many it deleted.</returns>
    Task<int> DeleteExpiredCodesAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Adds a new grant.</summary>
    /// <param name="grant">The grant.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task AddGrantAsync(OAuthGrant grant, CancellationToken cancellationToken);

    /// <summary>Finds a grant.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The grant with its version, or null.</returns>
    Task<OAuthGrant?> FindGrantAsync(string id, CancellationToken cancellationToken);

    /// <summary>Replaces a grant, unless it changed since it was read (its <see cref="OAuthGrant.Version"/>).</summary>
    /// <param name="grant">The new grant, with the version it was read at.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether it was replaced.</returns>
    Task<bool> ReplaceGrantAsync(OAuthGrant grant, CancellationToken cancellationToken);

    /// <summary>Deletes a grant.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <param name="version">Only at this version, when given: a grant refreshed since is kept.</param>
    /// <returns>Whether there was one (at that version).</returns>
    Task<bool> DeleteGrantAsync(string id, CancellationToken cancellationToken, string? version = null);

    /// <summary>Lists grants: an editor's, or everyone's.</summary>
    /// <param name="email">The editor, or null for all.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The grants.</returns>
    Task<IReadOnlyList<OAuthGrant>> ListGrantsAsync(string? email, CancellationToken cancellationToken);
}

/// <summary>The store in memory: content from files (tests, local runs), one replica.</summary>
/// <param name="time">The clock, for registration times.</param>
internal sealed class MemoryOAuthStore(TimeProvider time) : IOAuthStore
{
    private readonly ConcurrentDictionary<string, OAuthClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RegisteredClient> _registered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OAuthGrant> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MediaUploadTicket> _uploads = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveUploadAsync(MediaUploadTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        _uploads[ticket.Hash] = ticket;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<MediaUploadTicket?> TakeUploadAsync(string hash, CancellationToken cancellationToken) =>
        Task.FromResult(_uploads.TryRemove(hash, out var ticket) ? ticket : null);

    /// <inheritdoc />
    public Task<int> CountUploadsAsync(string grantId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_uploads.Values.Count(u => u.GrantId == grantId && u.ExpiresAt > now));

    /// <inheritdoc />
    public Task<int> DeleteExpiredUploadsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_uploads.Where(u => u.Value.ExpiresAt <= now).Count(u => _uploads.TryRemove(u)));

    /// <inheritdoc />
    public Task SaveClientAsync(OAuthClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients[client.Id] = client;
        _registered[client.Id] = new RegisteredClient(client.Id, time.GetUtcNow(), Granted: false, IdleSince: null);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OAuthClient?> FindClientAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_clients.GetValueOrDefault(id));

    /// <inheritdoc />
    public Task<IReadOnlyList<RegisteredClient>> ListClientsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RegisteredClient>>([.. _registered.Values]);

    /// <inheritdoc />
    public Task MarkClientGrantedAsync(string id, CancellationToken cancellationToken)
    {
        if (_registered.TryGetValue(id, out var client))
        {
            _registered.TryUpdate(id, client with { Granted = true, IdleSince = null }, client);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkClientIdleAsync(string id, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (_registered.TryGetValue(id, out var client))
        {
            _registered.TryUpdate(id, client with { IdleSince = since }, client);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteClientAsync(string id, CancellationToken cancellationToken)
    {
        _registered.TryRemove(id, out _);
        _clients.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveCodeAsync(AuthorizationCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        _codes[code.Hash] = code;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AuthorizationCode?> UseCodeAsync(string hash, string grantId, DateTimeOffset keepUntil, CancellationToken cancellationToken)
    {
        while (_codes.TryGetValue(hash, out var code))
        {
            if (code.UsedBy is not null || _codes.TryUpdate(hash, code with { UsedBy = grantId, ExpiresAt = keepUntil }, code))
            {
                return Task.FromResult<AuthorizationCode?>(code);
            }
        }

        return Task.FromResult<AuthorizationCode?>(null);
    }

    /// <inheritdoc />
    public Task<AuthorizationCode?> FindCodeAsync(string hash, CancellationToken cancellationToken) =>
        Task.FromResult(_codes.TryGetValue(hash, out var code) ? code : null);

    /// <inheritdoc />
    public Task MarkCodeReusedAsync(string hash, CancellationToken cancellationToken)
    {
        while (_codes.TryGetValue(hash, out var code) && !code.Reused && !_codes.TryUpdate(hash, code with { Reused = true }, code))
        {
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeleteExpiredCodesAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_codes.Where(c => c.Value.ExpiresAt <= now).Count(c => _codes.TryRemove(c)));

    /// <inheritdoc />
    public Task AddGrantAsync(OAuthGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!_grants.TryAdd(grant.Id, grant with { Version = Guid.NewGuid().ToString("N") }))
        {
            throw new InvalidOperationException("A grant with this ID exists.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OAuthGrant?> FindGrantAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_grants.GetValueOrDefault(id));

    /// <inheritdoc />
    public Task<bool> ReplaceGrantAsync(OAuthGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return Task.FromResult(
            _grants.TryGetValue(grant.Id, out var stored)
            && stored.Version == grant.Version
            && _grants.TryUpdate(grant.Id, grant with { Version = Guid.NewGuid().ToString("N") }, stored));
    }

    /// <inheritdoc />
    public Task<bool> DeleteGrantAsync(string id, CancellationToken cancellationToken, string? version = null) =>
        Task.FromResult(version is null
            ? _grants.TryRemove(id, out _)
            : _grants.TryGetValue(id, out var stored) && stored.Version == version && _grants.TryRemove(new KeyValuePair<string, OAuthGrant>(id, stored)));

    /// <inheritdoc />
    public Task<IReadOnlyList<OAuthGrant>> ListGrantsAsync(string? email, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OAuthGrant>>([.. _grants.Values.Where(g => email is null || SameEditor(g.Email, email))]);

    private static bool SameEditor(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The store in Table Storage, shared by every replica: registered clients in <c>oauthclients</c>,
/// codes, grants and upload addresses in <c>oauthgrants</c> (partitions <c>code</c>, <c>grant</c>
/// and <c>upload</c>). Row keys are random IDs or hashes, so they need no escaping.
/// </summary>
/// <param name="tables">The Table Storage account.</param>
/// <param name="time">The clock, for registration times.</param>
internal sealed class TableOAuthStore(TableServiceClient tables, TimeProvider time) : IOAuthStore
{
    private const string Codes = "code";
    private const string Grants = "grant";
    private const string Clients = "client";
    private const string Uploads = "upload";
    private readonly TableClient _clients = tables.GetTableClient("oauthclients");
    private readonly TableClient _grants = tables.GetTableClient("oauthgrants");
    private int _created;

    /// <inheritdoc />
    public async Task SaveUploadAsync(MediaUploadTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        await EnsureTablesAsync(cancellationToken);
        var entity = new TableEntity(Uploads, ticket.Hash)
        {
            ["GrantId"] = ticket.GrantId,
            ["Email"] = ticket.Email,
            ["ClientName"] = ticket.ClientName,
            ["FileName"] = ticket.FileName,
            ["Alt"] = ticket.Alt,
            ["ExpiresAt"] = ticket.ExpiresAt,
        };
        await _grants.AddEntityAsync(entity, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MediaUploadTicket?> TakeUploadAsync(string hash, CancellationToken cancellationToken)
    {
        if (!IsKey(hash))
        {
            return null;
        }

        await EnsureTablesAsync(cancellationToken);
        var found = await _grants.GetEntityIfExistsAsync<TableEntity>(Uploads, hash, cancellationToken: cancellationToken);
        if (!found.HasValue || found.Value is not { } e)
        {
            return null;
        }

        // Deleting it at the version read is what makes it single-use: of two uploads at once,
        // only one deletes it; the other gets 404 or 412.
        try
        {
            using var response = await _grants.DeleteEntityAsync(Uploads, hash, e.ETag, cancellationToken);
            if (response.Status == 404)
            {
                return null;
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return null;
        }

        return new MediaUploadTicket(
            hash,
            e.GetString("GrantId") ?? "",
            e.GetString("Email") ?? "",
            e.GetString("ClientName") ?? "",
            e.GetString("FileName") ?? "",
            e.GetString("Alt"),
            e.GetDateTimeOffset("ExpiresAt") ?? DateTimeOffset.MinValue);
    }

    /// <inheritdoc />
    public async Task<int> CountUploadsAsync(string grantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var count = 0;
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Uploads} and GrantId eq {grantId} and ExpiresAt gt {now}");
        await foreach (var _ in _grants.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: cancellationToken))
        {
            count++;
        }

        return count;
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredUploadsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var deleted = 0;
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Uploads} and ExpiresAt le {now}");
        await foreach (var e in _grants.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: cancellationToken))
        {
            try
            {
                using var response = await _grants.DeleteEntityAsync(Uploads, e.RowKey, e.ETag, cancellationToken);
                deleted += response.Status == 404 ? 0 : 1;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412)
            {
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task SaveClientAsync(OAuthClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await EnsureTablesAsync(cancellationToken);
        var entity = new TableEntity(Clients, client.Id)
        {
            ["Name"] = client.Name,
            ["RedirectUris"] = string.Join('\n', client.RedirectUris),
            ["RegisteredAt"] = time.GetUtcNow(),
        };
        await _clients.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OAuthClient?> FindClientAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsKey(id))
        {
            return null;
        }

        await EnsureTablesAsync(cancellationToken);
        var entity = await _clients.GetEntityIfExistsAsync<TableEntity>(Clients, id, cancellationToken: cancellationToken);
        return entity.HasValue && entity.Value is { } e
            ? new OAuthClient(id, e.GetString("Name") ?? "", (e.GetString("RedirectUris") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), FromDocument: false)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegisteredClient>> ListClientsAsync(CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var clients = new List<RegisteredClient>();
        await foreach (var e in _clients.QueryAsync<TableEntity>(e => e.PartitionKey == Clients, select: ["RegisteredAt", "Granted", "IdleSince"], cancellationToken: cancellationToken))
        {
            // Clients registered before F-0060 have no registration time: their last write is close.
            clients.Add(new RegisteredClient(
                e.RowKey,
                e.GetDateTimeOffset("RegisteredAt") ?? e.Timestamp ?? DateTimeOffset.MinValue,
                e.GetBoolean("Granted") ?? false,
                e.GetDateTimeOffset("IdleSince")));
        }

        return clients;
    }

    /// <inheritdoc />
    public async Task MarkClientGrantedAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsKey(id))
        {
            return;
        }

        await EnsureTablesAsync(cancellationToken);
        var found = await _clients.GetEntityIfExistsAsync<TableEntity>(Clients, id, cancellationToken: cancellationToken);
        if (!found.HasValue || found.Value is not { } e || (e.GetBoolean("Granted") == true && !e.ContainsKey("IdleSince")))
        {
            return;
        }

        // A merge can't remove a property, so the client is replaced at the version read. If
        // someone changed it meanwhile, the next clean-up sees the grant and marks it again.
        e["Granted"] = true;
        e.Remove("IdleSince");
        try
        {
            await _clients.UpdateEntityAsync(e, e.ETag, TableUpdateMode.Replace, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
        }
    }

    /// <inheritdoc />
    public async Task MarkClientIdleAsync(string id, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (!IsKey(id))
        {
            return;
        }

        await EnsureTablesAsync(cancellationToken);
        try
        {
            await _clients.UpdateEntityAsync(new TableEntity(Clients, id) { ["IdleSince"] = since }, ETag.All, TableUpdateMode.Merge, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    /// <inheritdoc />
    public async Task DeleteClientAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsKey(id))
        {
            return;
        }

        await EnsureTablesAsync(cancellationToken);
        try
        {
            using var response = await _clients.DeleteEntityAsync(Clients, id, ETag.All, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    /// <inheritdoc />
    public async Task SaveCodeAsync(AuthorizationCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        await EnsureTablesAsync(cancellationToken);
        var entity = new TableEntity(Codes, code.Hash)
        {
            ["ClientId"] = code.ClientId,
            ["ClientName"] = code.ClientName,
            ["RedirectUri"] = code.RedirectUri,
            ["CodeChallenge"] = code.CodeChallenge,
            ["Email"] = code.Email,
            ["Resource"] = code.Resource,
            ["ExpiresAt"] = code.ExpiresAt,
            ["Access"] = code.Access.ToString(),
        };
        await _grants.AddEntityAsync(entity, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AuthorizationCode?> UseCodeAsync(string hash, string grantId, DateTimeOffset keepUntil, CancellationToken cancellationToken)
    {
        if (!IsKey(hash))
        {
            return null;
        }

        await EnsureTablesAsync(cancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var found = await _grants.GetEntityIfExistsAsync<TableEntity>(Codes, hash, cancellationToken: cancellationToken);
            if (!found.HasValue || found.Value is not { } e)
            {
                return null;
            }

            var code = ReadCode(hash, e);
            if (code.UsedBy is not null)
            {
                return code;
            }

            // Marking it at the version read is what makes a code single-use: of two exchanges at
            // once, only one updates it; the other reads it again and finds it used.
            try
            {
                var used = new TableEntity(Codes, hash) { ["UsedBy"] = grantId, ["ExpiresAt"] = keepUntil };
                await _grants.UpdateEntityAsync(used, e.ETag, TableUpdateMode.Merge, cancellationToken);
                return code;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<AuthorizationCode?> FindCodeAsync(string hash, CancellationToken cancellationToken)
    {
        if (!IsKey(hash))
        {
            return null;
        }

        await EnsureTablesAsync(cancellationToken);
        var found = await _grants.GetEntityIfExistsAsync<TableEntity>(Codes, hash, cancellationToken: cancellationToken);
        return found.HasValue && found.Value is { } e ? ReadCode(hash, e) : null;
    }

    /// <inheritdoc />
    public async Task MarkCodeReusedAsync(string hash, CancellationToken cancellationToken)
    {
        if (!IsKey(hash))
        {
            return;
        }

        await EnsureTablesAsync(cancellationToken);
        try
        {
            await _grants.UpdateEntityAsync(new TableEntity(Codes, hash) { ["Reused"] = true }, ETag.All, TableUpdateMode.Merge, cancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private static AuthorizationCode ReadCode(string hash, TableEntity e) =>
        new(
            hash,
            e.GetString("ClientId") ?? "",
            e.GetString("ClientName") ?? "",
            e.GetString("RedirectUri") ?? "",
            e.GetString("CodeChallenge") ?? "",
            e.GetString("Email") ?? "",
            e.GetString("Resource") ?? "",
            e.GetDateTimeOffset("ExpiresAt") ?? DateTimeOffset.MinValue,
            ReadAccess(e),
            e.GetString("UsedBy"),
            e.GetBoolean("Reused") == true);

    /// <inheritdoc />
    public async Task<int> DeleteExpiredCodesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var deleted = 0;
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Codes} and ExpiresAt le {now}");
        await foreach (var e in _grants.QueryAsync<TableEntity>(filter, select: ["RowKey"], cancellationToken: cancellationToken))
        {
            // At the version read: a code being exchanged right now gets a later time to be deleted.
            try
            {
                using var response = await _grants.DeleteEntityAsync(Codes, e.RowKey, e.ETag, cancellationToken);
                deleted += response.Status == 404 ? 0 : 1;
            }
            catch (RequestFailedException ex) when (ex.Status is 404 or 412)
            {
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task AddGrantAsync(OAuthGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        await EnsureTablesAsync(cancellationToken);
        await _grants.AddEntityAsync(ToEntity(grant), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OAuthGrant?> FindGrantAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsKey(id))
        {
            return null;
        }

        await EnsureTablesAsync(cancellationToken);
        var found = await _grants.GetEntityIfExistsAsync<TableEntity>(Grants, id, cancellationToken: cancellationToken);
        return found.HasValue && found.Value is { } e ? ToGrant(e) : null;
    }

    /// <inheritdoc />
    public async Task<bool> ReplaceGrantAsync(OAuthGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        await EnsureTablesAsync(cancellationToken);
        try
        {
            await _grants.UpdateEntityAsync(ToEntity(grant), new ETag(grant.Version ?? ""), TableUpdateMode.Replace, cancellationToken);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteGrantAsync(string id, CancellationToken cancellationToken, string? version = null)
    {
        if (!IsKey(id))
        {
            return false;
        }

        await EnsureTablesAsync(cancellationToken);
        try
        {
            using var response = await _grants.DeleteEntityAsync(Grants, id, version is null ? ETag.All : new ETag(version), cancellationToken);
            return response.Status != 404;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OAuthGrant>> ListGrantsAsync(string? email, CancellationToken cancellationToken)
    {
        await EnsureTablesAsync(cancellationToken);
        var grants = new List<OAuthGrant>();
        await foreach (var entity in _grants.QueryAsync<TableEntity>(e => e.PartitionKey == Grants, cancellationToken: cancellationToken))
        {
            var grant = ToGrant(entity);
            if (email is null || string.Equals(grant.Email.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                grants.Add(grant);
            }
        }

        return grants;
    }

    // IDs and hashes are base64url or hex: anything else can't be a stored key (and could hold
    // characters row keys refuse).
    private static bool IsKey(string value) =>
        value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static TableEntity ToEntity(OAuthGrant grant) => new(Grants, grant.Id)
    {
        ["Email"] = grant.Email,
        ["ClientId"] = grant.ClientId,
        ["ClientName"] = grant.ClientName,
        ["Resource"] = grant.Resource,
        ["CreatedAt"] = grant.CreatedAt,
        ["UsedAt"] = grant.UsedAt,
        ["RefreshHash"] = grant.RefreshHash,
        ["PreviousRefreshHash"] = grant.PreviousRefreshHash,
        ["RetiredRefreshHashes"] = grant.RetiredRefreshHashes,
        ["Retried"] = grant.Retried,
        ["RefreshExpiresAt"] = grant.RefreshExpiresAt,
        ["Access"] = grant.Access.ToString(),

        // For an older version of the app after a rollback, which reads only this.
        ["CanPublish"] = grant.Access == AppAccess.Publish,
    };

    private static OAuthGrant ToGrant(TableEntity e) => new(
        e.RowKey,
        e.GetString("Email") ?? "",
        e.GetString("ClientId") ?? "",
        e.GetString("ClientName") ?? "",
        e.GetString("Resource") ?? "",
        e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
        e.GetDateTimeOffset("UsedAt") ?? DateTimeOffset.MinValue,
        e.GetString("RefreshHash") ?? "",
        e.GetString("PreviousRefreshHash"),
        e.GetDateTimeOffset("RefreshExpiresAt") ?? DateTimeOffset.MinValue,
        ReadAccess(e),
        e.ETag.ToString(),
        // Grants retried before the retired list kept their one retired token apart.
        string.Join(' ', new[] { e.GetString("SupersededRefreshHash"), e.GetString("RetiredRefreshHashes") }.Where(h => !string.IsNullOrEmpty(h))) is { Length: > 0 } retired ? retired : null,
        e.GetBoolean("Retried") ?? e.GetString("SupersededRefreshHash") is not null);

    // Grants made before F-0070 have only CanPublish; anything unknown is the least access.
    private static AppAccess ReadAccess(TableEntity e) =>
        Enum.TryParse<AppAccess>(e.GetString("Access"), out var access) && Enum.IsDefined(access) ? access
        : e.GetBoolean("CanPublish") == true ? AppAccess.Publish
        : AppAccess.Drafts;

    // In Azure the tables come from infra/app; locally (Azurite) the app creates them.
    private async Task EnsureTablesAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _created) == 0)
        {
            await _clients.CreateIfNotExistsAsync(cancellationToken);
            await _grants.CreateIfNotExistsAsync(cancellationToken);
            Volatile.Write(ref _created, 1);
        }
    }
}
