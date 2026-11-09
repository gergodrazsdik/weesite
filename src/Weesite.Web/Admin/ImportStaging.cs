using System.Collections.Concurrent;
using Azure.Storage.Blobs;

namespace Weesite.Web.Admin;

/// <summary>
/// Holds an uploaded backup zip between the upload and the admin's choice to apply it (T-0051):
/// every replica can read it, so the review and the apply may land on different ones.
/// </summary>
internal interface IImportStaging
{
    /// <summary>Stores an upload under a new ID.</summary>
    /// <param name="content">The zip, read to its end.</param>
    /// <param name="cancellationToken">Stops the upload.</param>
    /// <returns>The ID.</returns>
    Task<string> SaveAsync(Stream content, CancellationToken cancellationToken);

    /// <summary>Opens a staged zip for reading, seekable.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The stream, or null when there's none by that ID.</returns>
    Task<Stream?> OpenAsync(string id, CancellationToken cancellationToken);

    /// <summary>Deletes a staged zip.</summary>
    /// <param name="id">Its ID.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>IDs of staged imports: 32 hex digits, so they can't name anything else.</summary>
internal static class ImportIds
{
    /// <summary>Makes a new ID.</summary>
    /// <returns>The ID.</returns>
    public static string New() => Guid.NewGuid().ToString("N");

    /// <summary>Whether a string is an ID this app made.</summary>
    /// <param name="id">The string.</param>
    /// <returns>True when it is.</returns>
    public static bool IsValid(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);
}

/// <summary>Staged imports in the private <c>imports</c> Blob container; ones older than a day are deleted on the next upload.</summary>
internal sealed class BlobImportStaging(BlobServiceClient blobs, TimeProvider time) : IImportStaging
{
    private BlobContainerClient Container => blobs.GetBlobContainerClient("imports");

    /// <inheritdoc />
    public async Task<string> SaveAsync(Stream content, CancellationToken cancellationToken)
    {
        await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await foreach (var old in Container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (old.Properties.LastModified < time.GetUtcNow().AddDays(-1))
            {
                await Container.DeleteBlobIfExistsAsync(old.Name, cancellationToken: cancellationToken);
            }
        }

        var id = ImportIds.New();
        await Container.GetBlobClient($"{id}.zip").UploadAsync(content, overwrite: false, cancellationToken);
        return id;
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenAsync(string id, CancellationToken cancellationToken)
    {
        if (!ImportIds.IsValid(id))
        {
            return null;
        }

        var blob = Container.GetBlobClient($"{id}.zip");
        return await blob.ExistsAsync(cancellationToken) ? await blob.OpenReadAsync(cancellationToken: cancellationToken) : null;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (ImportIds.IsValid(id))
        {
            await Container.DeleteBlobIfExistsAsync($"{id}.zip", cancellationToken: cancellationToken);
        }
    }
}

/// <summary>Staged imports in memory: content from files (tests).</summary>
internal sealed class MemoryImportStaging : IImportStaging
{
    private readonly ConcurrentDictionary<string, byte[]> _zips = new();

    /// <inheritdoc />
    public async Task<string> SaveAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var id = ImportIds.New();
        _zips[id] = buffer.ToArray();
        return id;
    }

    /// <inheritdoc />
    public Task<Stream?> OpenAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(_zips.TryGetValue(id, out var zip) ? new MemoryStream(zip, writable: false) : null);

    /// <inheritdoc />
    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        _zips.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}
