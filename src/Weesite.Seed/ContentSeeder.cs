using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace Weesite.Seed;

/// <summary>
/// Uploads a seed directory into Blob Storage: each top-level directory becomes a container and
/// each file a blob named by its path below that directory. Existing blobs are overwritten, so
/// every run starts from the same sample site.
/// </summary>
/// <param name="blobs">The Blob Storage account (Azurite locally).</param>
/// <param name="logger">Where progress goes.</param>
internal sealed partial class ContentSeeder(BlobServiceClient blobs, ILogger<ContentSeeder> logger)
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
                await container.GetBlobClient(name).UploadAsync(file.FullName, overwrite: true, cancellationToken);
                count++;
            }

            LogSeeded(count, container.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Count} blobs into container {Container}")]
    private partial void LogSeeded(int count, string container);
}
