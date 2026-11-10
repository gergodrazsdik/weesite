using System.Text.Json.Nodes;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Azurite;
using Weesite.Content.Sources;

namespace Weesite.Content.Tests;

/// <summary>Saving site settings on Blob Storage.</summary>
internal sealed class SettingsSaveBlobTests
{
    [Test]
    [Category("containers")]
    public async Task Settings_save_with_the_etag_they_were_opened_at()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var blobs = new BlobServiceClient(azurite.GetConnectionString());
        await new BlobContentStore(blobs).WriteSettingsFileAsync(ContentContainers.SiteSettingsFile, "{\"name\": \"Before\"}", null, CancellationToken.None);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(blobs);
        services.AddContentFromBlobs();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None);
        var editor = provider.GetRequiredService<SiteSettingsEditor>();

        var opened = await editor.OpenAsync(CancellationToken.None);
        var saved = await editor.SaveAsync(opened.ETag, json => json["name"] = JsonValue.Create("After"), CancellationToken.None);
        var reopened = await editor.OpenAsync(CancellationToken.None);
        var second = await editor.SaveAsync(reopened.ETag, json => json["name"] = JsonValue.Create("Again"), CancellationToken.None);

        await Assert.That(saved).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That((await editor.OpenAsync(CancellationToken.None)).Settings.Name).IsEqualTo("Again");
    }
}
