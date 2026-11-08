using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;

namespace Weesite.Web.Admin;

/// <summary>An editor on the allowlist.</summary>
/// <param name="Email">The email address, as entered.</param>
/// <param name="Role">The role.</param>
/// <param name="AddedBy">Who added or last changed them.</param>
/// <param name="AddedAt">When.</param>
internal sealed record Editor(string Email, EditorRole Role, string AddedBy, DateTimeOffset AddedAt);

/// <summary>The editors added on the editors page (ADR 0004); the configured admins aren't here.</summary>
internal interface IEditorStore
{
    /// <summary>Lists the editors.</summary>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The editors, by email address.</returns>
    Task<IReadOnlyList<Editor>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Finds an editor.</summary>
    /// <param name="email">The email address.</param>
    /// <param name="cancellationToken">Stops the lookup.</param>
    /// <returns>The editor, or null.</returns>
    Task<Editor?> FindAsync(string email, CancellationToken cancellationToken);

    /// <summary>Adds an editor or changes their role.</summary>
    /// <param name="editor">The editor.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <returns>A task.</returns>
    Task SetAsync(Editor editor, CancellationToken cancellationToken);

    /// <summary>Removes an editor.</summary>
    /// <param name="email">The email address.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>Whether they were there.</returns>
    Task<bool> RemoveAsync(string email, CancellationToken cancellationToken);
}

/// <summary>Editors in memory: content from files (tests, the file source), where nothing persists.</summary>
internal sealed class MemoryEditorStore : IEditorStore
{
    private readonly ConcurrentDictionary<string, Editor> _editors = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IReadOnlyList<Editor>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Editor>>([.. _editors.Values.OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)]);

    /// <inheritdoc />
    public Task<Editor?> FindAsync(string email, CancellationToken cancellationToken) =>
        Task.FromResult(_editors.GetValueOrDefault(ConfiguredAdmins.Normalize(email)));

    /// <inheritdoc />
    public Task SetAsync(Editor editor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editors[ConfiguredAdmins.Normalize(editor.Email)] = editor;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string email, CancellationToken cancellationToken) =>
        Task.FromResult(_editors.TryRemove(ConfiguredAdmins.Normalize(email), out _));
}

/// <summary>
/// Editors in the <c>editors</c> table: one partition, the row key the normalized address
/// (escaped, as row keys can't hold <c>/ \ # ?</c>).
/// </summary>
internal sealed class TableEditorStore(TableServiceClient tables) : IEditorStore
{
    private const string Partition = "editors";
    private readonly TableClient _table = tables.GetTableClient("editors");
    private int _created;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Editor>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        var editors = new List<Editor>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(e => e.PartitionKey == Partition, cancellationToken: cancellationToken))
        {
            if (ToEditor(entity) is { } editor)
            {
                editors.Add(editor);
            }
        }

        return [.. editors.OrderBy(e => e.Email, StringComparer.OrdinalIgnoreCase)];
    }

    /// <inheritdoc />
    public async Task<Editor?> FindAsync(string email, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        var entity = await _table.GetEntityIfExistsAsync<TableEntity>(Partition, RowKey(email), cancellationToken: cancellationToken);
        return entity.HasValue ? ToEditor(entity.Value!) : null;
    }

    /// <inheritdoc />
    public async Task SetAsync(Editor editor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(editor);
        await EnsureTableAsync(cancellationToken);
        var entity = new TableEntity(Partition, RowKey(editor.Email))
        {
            ["Email"] = editor.Email.Trim(),
            ["Role"] = editor.Role.ToString(),
            ["AddedBy"] = editor.AddedBy,
            ["AddedAt"] = editor.AddedAt,
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string email, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(cancellationToken);
        try
        {
            using var response = await _table.DeleteEntityAsync(Partition, RowKey(email), ETag.All, cancellationToken);
            return response.Status != 404;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    private static string RowKey(string email) => Uri.EscapeDataString(ConfiguredAdmins.Normalize(email));

    private static Editor? ToEditor(TableEntity entity) =>
        Enum.TryParse<EditorRole>(entity.GetString("Role"), out var role) && entity.GetString("Email") is { } email
            ? new Editor(email, role, entity.GetString("AddedBy") ?? "", entity.GetDateTimeOffset("AddedAt") ?? DateTimeOffset.MinValue)
            : null;

    // In Azure the table comes from infra/app; locally (Azurite) the app creates it.
    private async Task EnsureTableAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _created) == 0)
        {
            await _table.CreateIfNotExistsAsync(cancellationToken);
            Volatile.Write(ref _created, 1);
        }
    }
}

/// <summary>The allowlist: the configured admins first, then the editors added on the editors page.</summary>
internal sealed class EditorDirectory(ConfiguredAdmins admins, IEditorStore store) : IEditorDirectory
{
    /// <inheritdoc />
    public async Task<EditorRole?> FindRoleAsync(string email, CancellationToken cancellationToken) =>
        await admins.FindRoleAsync(email, cancellationToken)
            ?? (await store.FindAsync(email, cancellationToken))?.Role;
}
