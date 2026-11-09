using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Weesite.Content.Media;

namespace Weesite.Content.Sources;

/// <summary>
/// Uploads a seed directory into Blob Storage: each top-level directory becomes a container and
/// each file a blob named by its path below it. A blob is written only when it's missing or its
/// seed file changed, so restarts don't pile up Blob versions, and a file changed in the editor
/// is kept (F-0035). Images in <c>media/</c> go through <see cref="ImageProcessor"/> on the way,
/// as uploads do: metadata stripped, WebP variants beside them. Used locally (Aspire) and in
/// test, never in prod.
/// </summary>
/// <param name="blobs">The storage account.</param>
/// <param name="logger">Where progress goes.</param>
public sealed partial class ContentSeeder(BlobServiceClient blobs, ILogger<ContentSeeder> logger)
{
    /// <summary>The blob metadata key holding the hash of the seed file a blob was written from.</summary>
    public const string SeedHashKey = "seedhash";

    private enum SeedResult
    {
        Written,
        Unchanged,
        Kept,
    }

    /// <summary>Uploads every file below <paramref name="source"/>.</summary>
    /// <param name="source">The seed directory, such as the repo's <c>seed/</c>.</param>
    /// <param name="cancellationToken">Stops the upload.</param>
    /// <returns>A task that completes when every file is uploaded.</returns>
    public async Task SeedAsync(string source, CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(source);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException($"Seed directory not found: {root.FullName}");
        }

        foreach (var directory in root.EnumerateDirectories())
        {
            var container = blobs.GetBlobContainerClient(directory.Name);
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var count = 0;
            var skipped = 0;
            var kept = 0;
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(directory.FullName, file.FullName).Replace('\\', '/');
                if (directory.Name == ContentContainers.Media && ImageProcessor.IsProcessable(name))
                {
                    // Media names carry the file's hash, so an image already stored is this same
                    // image, processed: skip it rather than make its WebP variants again at every
                    // start (minutes on a small replica, F-0033). The original goes up last, after
                    // its variants, so a start cut short leaves nothing that looks done.
                    var original = container.GetBlobClient(name);
                    if (await original.ExistsAsync(cancellationToken))
                    {
                        skipped++;
                        continue;
                    }

                    var image = ImageProcessor.Process(name, await File.ReadAllBytesAsync(file.FullName, cancellationToken));
                    foreach (var variant in image.Variants)
                    {
                        await container.GetBlobClient(variant.Name).UploadAsync(new BinaryData(variant.Data), overwrite: true, cancellationToken);
                        count++;
                    }

                    await original.UploadAsync(new BinaryData(image.Original), overwrite: true, cancellationToken);
                }
                else
                {
                    switch (await SeedFileAsync(container.GetBlobClient(name), file.FullName, cancellationToken))
                    {
                        case SeedResult.Unchanged:
                            skipped++;
                            continue;
                        case SeedResult.Kept:
                            kept++;
                            continue;
                    }
                }

                count++;
            }

            LogSeeded(count, skipped, kept, container.Name);
        }
    }

    // Posts, pages and settings carry the hash of the seed file they were written from. A blob is
    // written when it's missing, or when its seed file changed and it still holds what the seeder
    // wrote. An unchanged file isn't written again (each write is a new Blob version), and a blob
    // saved in the editor (which writes no seed hash) is left alone.
    private static async Task<SeedResult> SeedFileAsync(BlobClient blob, string path, CancellationToken cancellationToken)
    {
        var data = await File.ReadAllBytesAsync(path, cancellationToken);
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var metadata = new Dictionary<string, string> { [SeedHashKey] = hash };
        BlobProperties properties;
        try
        {
            properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            await blob.UploadAsync(new BinaryData(data), new BlobUploadOptions { Metadata = metadata }, cancellationToken);
            return SeedResult.Written;
        }

        if (properties.Metadata.TryGetValue(SeedHashKey, out var seeded))
        {
            if (string.Equals(seeded, hash, StringComparison.Ordinal))
            {
                return SeedResult.Unchanged;
            }

            await blob.UploadAsync(new BinaryData(data), new BlobUploadOptions { Metadata = metadata }, cancellationToken);
            return SeedResult.Written;
        }

        // No seed hash: saved in the editor, or written by an earlier seeder that kept none. If it
        // matches the seed file, mark it as the seed's (once); otherwise it's an edit to keep.
        var current = (await blob.DownloadContentAsync(cancellationToken)).Value.Content.ToMemory();
        if (current.Span.SequenceEqual(data))
        {
            await blob.SetMetadataAsync(metadata, cancellationToken: cancellationToken);
            return SeedResult.Unchanged;
        }

        return SeedResult.Kept;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Count} blobs into container {Container} ({Skipped} unchanged, {Kept} changed in the editor and kept)")]
    private partial void LogSeeded(int count, int skipped, int kept, string container);
}
