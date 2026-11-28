using System.Collections.Concurrent;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Weesite.Web.Agents;

/// <summary>A staged upload: its bytes, or that its retry has used them.</summary>
/// <param name="Data">The bytes; empty once used.</param>
/// <param name="Used">Whether a retry stored them already.</param>
internal sealed record StagedUpload(byte[] Data, bool Used);

/// <summary>
/// Where the files extension's bytes wait between the transfer and the retry (T-0202): every
/// replica reads the same, so the retry may reach another replica than the transfer.
/// </summary>
internal interface IUploadStaging
{
    /// <summary>Stores a target's bytes, unless something was stored for it already.</summary>
    /// <param name="targetId">The target's ID.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>False when the target had bytes already: a target takes one write.</returns>
    Task<bool> TryWriteAsync(string targetId, byte[] data, CancellationToken cancellationToken);

    /// <summary>Reads a target's bytes.</summary>
    /// <param name="targetId">The target's ID.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The staged upload, or null when nothing arrived.</returns>
    Task<StagedUpload?> ReadAsync(string targetId, CancellationToken cancellationToken);

    /// <summary>Drops a target's bytes once a retry stored them, keeping that it was used.</summary>
    /// <param name="targetId">The target's ID.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task.</returns>
    Task MarkUsedAsync(string targetId, CancellationToken cancellationToken);

    /// <summary>Deletes what was staged before a time: no request state can use it any more.</summary>
    /// <param name="before">The time.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>A task.</returns>
    Task SweepAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary>Target IDs: 32 random bytes in base64url, so they can't name anything else.</summary>
internal static class UploadTargetIds
{
    /// <summary>Whether a string is a target ID this app made.</summary>
    /// <param name="id">The string.</param>
    /// <returns>True when it is.</returns>
    public static bool IsValid(string? id) => id is { Length: 43 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>
/// Staged uploads in the private <c>uploads</c> Blob container, which the app creates: one blob per
/// target, written only if it doesn't exist, emptied and marked once used, and deleted by the sweep.
/// </summary>
internal sealed class BlobUploadStaging(BlobServiceClient blobs) : IUploadStaging
{
    /// <summary>The container's name.</summary>
    public const string ContainerName = "uploads";

    private const string UsedKey = "used";

    private int _created;

    private BlobContainerClient Container => blobs.GetBlobContainerClient(ContainerName);

    /// <inheritdoc />
    public async Task<bool> TryWriteAsync(string targetId, byte[] data, CancellationToken cancellationToken)
    {
        if (!UploadTargetIds.IsValid(targetId))
        {
            return false;
        }

        await CreateAsync(cancellationToken);
        try
        {
            // If-None-Match: * makes the write once, whichever replica takes it.
            await Container.GetBlobClient(targetId).UploadAsync(
                BinaryData.FromBytes(data),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
                cancellationToken);
            return true;
        }
        catch (RequestFailedException e) when (e.Status is StatusCodes.Status409Conflict or StatusCodes.Status412PreconditionFailed)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<StagedUpload?> ReadAsync(string targetId, CancellationToken cancellationToken)
    {
        if (!UploadTargetIds.IsValid(targetId))
        {
            return null;
        }

        try
        {
            var download = (await Container.GetBlobClient(targetId).DownloadContentAsync(cancellationToken)).Value;
            return new StagedUpload(download.Content.ToArray(), download.Details.Metadata.ContainsKey(UsedKey));
        }
        catch (RequestFailedException e) when (e.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task MarkUsedAsync(string targetId, CancellationToken cancellationToken)
    {
        if (UploadTargetIds.IsValid(targetId))
        {
            await Container.GetBlobClient(targetId).UploadAsync(
                BinaryData.Empty,
                new BlobUploadOptions { Metadata = new Dictionary<string, string> { [UsedKey] = "true" } },
                cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task SweepAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        if (!await Container.ExistsAsync(cancellationToken))
        {
            return;
        }

        await foreach (var blob in Container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (blob.Properties.LastModified < before)
            {
                await Container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: cancellationToken);
            }
        }
    }

    private async Task CreateAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _created) == 0)
        {
            await Container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            Volatile.Write(ref _created, 1);
        }
    }
}

/// <summary>Staged uploads in memory: content from files (tests and local runs without storage).</summary>
internal sealed class MemoryUploadStaging(TimeProvider time) : IUploadStaging
{
    private readonly ConcurrentDictionary<string, (StagedUpload Upload, DateTimeOffset At)> _uploads = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> TryWriteAsync(string targetId, byte[] data, CancellationToken cancellationToken) =>
        Task.FromResult(UploadTargetIds.IsValid(targetId) && _uploads.TryAdd(targetId, (new StagedUpload(data, false), time.GetUtcNow())));

    /// <inheritdoc />
    public Task<StagedUpload?> ReadAsync(string targetId, CancellationToken cancellationToken) =>
        Task.FromResult(_uploads.TryGetValue(targetId, out var staged) ? staged.Upload : null);

    /// <inheritdoc />
    public Task MarkUsedAsync(string targetId, CancellationToken cancellationToken)
    {
        _uploads[targetId] = (new StagedUpload([], true), time.GetUtcNow());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SweepAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        foreach (var (id, staged) in _uploads)
        {
            if (staged.At < before)
            {
                _uploads.TryRemove(id, out _);
            }
        }

        return Task.CompletedTask;
    }
}
