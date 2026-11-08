using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Sources;

namespace Weesite.Content;

/// <summary>Registers the content services.</summary>
public static class ContentServiceCollectionExtensions
{
    /// <summary>Reads content from a directory (tests, previews).</summary>
    /// <param name="services">The services.</param>
    /// <param name="root">The directory with <c>content/</c> and <c>settings/</c>.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddContentFromFiles(this IServiceCollection services, string root)
    {
        services.AddSingleton<IContentSource>(new FileContentSource(root));
        return services.AddContentCore();
    }

    /// <summary>
    /// Reads content from Blob Storage. The <see cref="BlobServiceClient"/> must be registered
    /// already (the web app uses Aspire's client integration).
    /// </summary>
    /// <param name="services">The services.</param>
    /// <returns>The same services.</returns>
    public static IServiceCollection AddContentFromBlobs(this IServiceCollection services)
    {
        services.AddSingleton<IContentSource, BlobContentSource>();
        services.AddSingleton<ContentSeeder>();
        return services.AddContentCore();
    }

    private static IServiceCollection AddContentCore(this IServiceCollection services)
    {
        services.AddSingleton<ContentCatalogProvider>();
        services.AddHostedService<ContentLoader>();
        return services;
    }
}
