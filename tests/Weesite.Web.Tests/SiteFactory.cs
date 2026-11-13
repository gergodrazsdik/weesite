using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weesite.Search;

namespace Weesite.Web.Tests;

/// <summary>The web app reading the sample site from the repo's seed/ directory, no storage needed.</summary>
/// <param name="contentPath">The content directory; the repo's seed/ by default.</param>
internal sealed class SiteFactory(string? contentPath = null) : WebApplicationFactory<Program>
{
    /// <summary>Gets the repo's seed/ directory.</summary>
    public static string SeedPath { get; } = Weesite.Tests.RepoPaths.Seed;

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Content:Path", contentPath ?? SeedPath);

        // Every section embedded at start (the fake model is instant), as in the browser tests:
        // with the default 200, every app asked for a re-embed that ran in the background and
        // could still be waiting when a test pressed "Re-embed all" (F-0118).
        builder.UseSetting("Search:EmbedLimit", "100000");

        // A fake embedding model: the real one is tested in Weesite.Search.Tests and end to end.
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmbedder>();
            services.AddSingleton<IEmbedder>(new HashEmbedder());

            // Keys in memory, one set per test app (F-0044): the default key folder in the user
            // profile is shared by every app in this process, and on a fresh CI runner dozens of
            // them race to create its first keys, which broke antiforgery tokens now and then.
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
    }

}
