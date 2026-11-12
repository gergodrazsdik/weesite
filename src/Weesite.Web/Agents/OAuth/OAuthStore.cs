using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;

namespace Weesite.Web.Agents.OAuth;

/// <summary>A client that registered itself (RFC 7591), or one described by its metadata document.</summary>
/// <param name="Id">The client ID: random for a registered client, the document's URL otherwise.</param>
/// <param name="Name">The name it gave, shown on the consent page.</param>
/// <param name="RedirectUris">Where it may receive codes.</param>
/// <param name="FromDocument">Whether the client is described by a Client ID Metadata Document.</param>
internal sealed record OAuthClient(string Id, string Name, IReadOnlyList<string> RedirectUris, bool FromDocument);

/// <summary>A code an editor's consent produced, waiting to be exchanged once.</summary>
/// <param name="Hash">The SHA-256 of the code (the code itself is never stored).</param>
/// <param name="ClientId">The client it was issued to.</param>
/// <param name="ClientName">The client's name, for the grant.</param>
/// <param name="RedirectUri">The redirect URI of the request, which the exchange must repeat.</param>
/// <param name="CodeChallenge">The PKCE challenge (S256).</param>
/// <param name="Email">The editor who consented.</param>
/// <param name="Resource">The resource the tokens are for.</param>
/// <param name="ExpiresAt">When the code stops working.</param>
/// <param name="CanPublish">Whether the editor let the app publish, not only write drafts.</param>
internal sealed record AuthorizationCode(
    string Hash,
    string ClientId,
    string ClientName,
    string RedirectUri,
    string CodeChallenge,
    string Email,
    string Resource,
    DateTimeOffset ExpiresAt,
    bool CanPublish = false);

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
/// <param name="CanPublish">Whether the editor let the app publish, not only write drafts.</param>
/// <param name="Version">The stored version, for replacing it only when nobody else has.</param>
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
    bool CanPublish = false,
    string? Version = null);

/// <summary>Where the authorization server keeps registered clients, codes and grants (ADR 0007).</summary>
internal interface IOAuthStore
{
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

    /// <summary>Saves a new code.</summary>
    /// <param name="code">The code.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task SaveCodeAsync(AuthorizationCode code, CancellationToken cancellationToken);

    /// <summary>Removes a code and returns it: only the first caller gets it, on any replica.</summary>
    /// <param name="hash">The code's hash.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The code, or null when it doesn't exist or was taken already.</returns>
    Task<AuthorizationCode?> TakeCodeAsync(string hash, CancellationToken cancellationToken);

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
    /// <returns>Whether there was one.</returns>
    Task<bool> DeleteGrantAsync(string id, CancellationToken cancellationToken);

    /// <summary>Lists grants: an editor's, or everyone's.</summary>
    /// <param name="email">The editor, or null for all.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The grants.</returns>
    Task<IReadOnlyList<OAuthGrant>> ListGrantsAsync(string? email, CancellationToken cancellationToken);
}

/// <summary>The store in memory: content from files (tests, local runs), one replica.</summary>
internal sealed class MemoryOAuthStore : IOAuthStore
{
    private readonly ConcurrentDictionary<string, OAuthClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OAuthGrant> _grants = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task SaveClientAsync(OAuthClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        _clients[client.Id] = client;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OAuthClient?> FindClientAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_clients.GetValueOrDefault(id));

    /// <inheritdoc />
    public Task SaveCodeAsync(AuthorizationCode code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(code);
        _codes[code.Hash] = code;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AuthorizationCode?> TakeCodeAsync(string hash, CancellationToken cancellationToken) =>
        Task.FromResult(_codes.TryRemove(hash, out var code) ? code : null);

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
    public Task<bool> DeleteGrantAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_grants.TryRemove(id, out _));

    /// <inheritdoc />
    public Task<IReadOnlyList<OAuthGrant>> ListGrantsAsync(string? email, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<OAuthGrant>>([.. _grants.Values.Where(g => email is null || SameEditor(g.Email, email))]);

    private static bool SameEditor(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The store in Table Storage, shared by every replica: registered clients in <c>oauthclients</c>,
/// codes and grants in <c>oauthgrants</c> (partitions <c>code</c> and <c>grant</c>). Row keys are
/// random IDs or hashes, so they need no escaping.
/// </summary>
internal sealed class TableOAuthStore(TableServiceClient tables) : IOAuthStore
{
    private const string Codes = "code";
    private const string Grants = "grant";
    private const string Clients = "client";
    private readonly TableClient _clients = tables.GetTableClient("oauthclients");
    private readonly TableClient _grants = tables.GetTableClient("oauthgrants");
    private int _created;

    /// <inheritdoc />
    public async Task SaveClientAsync(OAuthClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await EnsureTablesAsync(cancellationToken);
        var entity = new TableEntity(Clients, client.Id)
        {
            ["Name"] = client.Name,
            ["RedirectUris"] = string.Join('\n', client.RedirectUris),
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
            ["CanPublish"] = code.CanPublish,
        };
        await _grants.AddEntityAsync(entity, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AuthorizationCode?> TakeCodeAsync(string hash, CancellationToken cancellationToken)
    {
        if (!IsKey(hash))
        {
            return null;
        }

        await EnsureTablesAsync(cancellationToken);
        var found = await _grants.GetEntityIfExistsAsync<TableEntity>(Codes, hash, cancellationToken: cancellationToken);
        if (!found.HasValue || found.Value is not { } e)
        {
            return null;
        }

        // Deleting at the version read is what makes a code single-use: of two exchanges at once,
        // only one deletes it. The SDK answers a delete of a missing entity with 404, not an error.
        try
        {
            using var deleted = await _grants.DeleteEntityAsync(Codes, hash, e.ETag, cancellationToken);
            if (deleted.Status == 404)
            {
                return null;
            }
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return null;
        }

        return new AuthorizationCode(
            hash,
            e.GetString("ClientId") ?? "",
            e.GetString("ClientName") ?? "",
            e.GetString("RedirectUri") ?? "",
            e.GetString("CodeChallenge") ?? "",
            e.GetString("Email") ?? "",
            e.GetString("Resource") ?? "",
            e.GetDateTimeOffset("ExpiresAt") ?? DateTimeOffset.MinValue,
            e.GetBoolean("CanPublish") ?? false);
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
    public async Task<bool> DeleteGrantAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsKey(id))
        {
            return false;
        }

        await EnsureTablesAsync(cancellationToken);
        try
        {
            using var response = await _grants.DeleteEntityAsync(Grants, id, ETag.All, cancellationToken);
            return response.Status != 404;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
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
        ["RefreshExpiresAt"] = grant.RefreshExpiresAt,
        ["CanPublish"] = grant.CanPublish,
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
        e.GetBoolean("CanPublish") ?? false,
        e.ETag.ToString());

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
