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

        var files = new List<SourceFile>();
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (!blob.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var download = await container.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken);
            files.Add(new SourceFile(blob.Name, download.Value.Content.ToString()));
        }

        return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
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
