using Azure;
using Azure.Storage.Blobs;

namespace Weesite.Content.Sources;

/// <summary>Reads content from Blob Storage (Azurite with Aspire, the environment's account in Azure).</summary>
/// <param name="blobs">The storage account, authenticated with Entra ID.</param>
public sealed class BlobContentSource(BlobServiceClient blobs) : IContentSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceFile>> ReadContentAsync(CancellationToken cancellationToken)
    {
        var container = blobs.GetBlobContainerClient(ContentContainers.Content);
        if (!await container.ExistsAsync(cancellationToken))
        {
            return [];
        }

        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (blob.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(blob.Name);
            }
        }

        // Eight at a time; a file deleted between the listing and its download (on another
        // replica) is skipped, not the whole load (F-0108).
        var files = new System.Collections.Concurrent.ConcurrentBag<SourceFile>();
        await Parallel.ForEachAsync(names, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (name, token) =>
        {
            try
            {
                var download = await container.GetBlobClient(name).DownloadContentAsync(token);
                files.Add(new SourceFile(name, download.Value.Content.ToString()));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
            }
        });

        return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public Task<string?> ReadSettingsAsync(CancellationToken cancellationToken) =>
        ReadSettingsFileAsync(ContentContainers.SiteSettingsFile, cancellationToken);

    /// <inheritdoc />
    public async Task<string?> ReadSettingsFileAsync(string name, CancellationToken cancellationToken)
    {
        var blob = blobs.GetBlobContainerClient(ContentContainers.Settings).GetBlobClient(name);
        try
        {
            var download = await blob.DownloadContentAsync(cancellationToken);
            return download.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> ReadVersionAsync(CancellationToken cancellationToken)
    {
        var blob = blobs.GetBlobContainerClient(ContentContainers.Settings).GetBlobClient(ContentContainers.VersionFile);
        try
        {
            return (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value.ETag.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}
