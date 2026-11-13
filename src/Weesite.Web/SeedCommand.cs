using Weesite.Content;
using Weesite.Content.Sources;
using Weesite.ServiceDefaults;
using Weesite.Web.Admin;

namespace Weesite.Web;

/// <summary>
/// <c>seed &lt;directory&gt;</c> (T-0128): uploads a seed directory, such as the repository's
/// <c>seed/</c>, into the storage account named by the <c>blobs</c> connection, then bumps the
/// version marker so running replicas load it, and asks the re-index job to embed it. The seed
/// workflow runs it against test; the image no longer carries the seed. Files changed in the editor
/// are kept, and unchanged ones aren't written again (<see cref="ContentSeeder"/>), so it can run
/// as often as needed.
/// </summary>
internal static partial class SeedCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="directory">The seed directory.</param>
    /// <param name="args">The rest of the command line.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string directory, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddAzureBlobServiceClient("blobs");
        builder.AddAzureQueueServiceClient("queues");
        builder.Services.AddContentFromBlobs();
        builder.Services.AddSingleton<QueueReindexRequests>();

        // The host isn't started: none of the web app's background services run here.
        using var host = builder.Build();
        var services = host.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.SeedCommand");
        var cancel = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        if (!Directory.Exists(directory))
        {
            LogMissing(logger, directory);
            return 2;
        }

        await services.GetRequiredService<ContentSeeder>().SeedAsync(directory, cancel);
        await services.GetRequiredService<IContentStore>().BumpVersionAsync(cancel);
        var asked = await services.GetRequiredService<QueueReindexRequests>().RequestAsync("seed workflow", cancel);
        LogDone(logger, directory, asked);
        return 0;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "No seed directory at {Directory}")]
    private static partial void LogMissing(ILogger logger, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded {Directory}; re-embed asked for: {Asked}")]
    private static partial void LogDone(ILogger logger, string directory, bool asked);
}
