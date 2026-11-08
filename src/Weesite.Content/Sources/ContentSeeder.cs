using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Weesite.Content.Media;

namespace Weesite.Content.Sources;

/// <summary>
/// Uploads a seed directory into Blob Storage: each top-level directory becomes a container and
/// each file a blob named by its path below it. Existing blobs are overwritten, so every start
/// shows the same sample site. Images in <c>media/</c> go through <see cref="ImageProcessor"/>
/// on the way, as uploads will: metadata stripped, WebP variants beside them. Used locally
/// (Aspire) and in test, never in prod.
/// </summary>
/// <param name="blobs">The storage account.</param>
/// <param name="logger">Where progress goes.</param>
public sealed partial class ContentSeeder(BlobServiceClient blobs, ILogger<ContentSeeder> logger)
{
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
            foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(directory.FullName, file.FullName).Replace('\\', '/');
                if (directory.Name == ContentContainers.Media && ImageProcessor.IsProcessable(name))
                {
                    var image = ImageProcessor.Process(name, await File.ReadAllBytesAsync(file.FullName, cancellationToken));
                    await container.GetBlobClient(name).UploadAsync(new BinaryData(image.Original), overwrite: true, cancellationToken);
                    foreach (var variant in image.Variants)
                    {
                        await container.GetBlobClient(variant.Name).UploadAsync(new BinaryData(variant.Data), overwrite: true, cancellationToken);
                        count++;
                    }
                }
                else
                {
                    await container.GetBlobClient(name).UploadAsync(file.FullName, overwrite: true, cancellationToken);
                }

                count++;
            }

            LogSeeded(count, container.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Count} blobs into container {Container}")]
    private partial void LogSeeded(int count, string container);
}
