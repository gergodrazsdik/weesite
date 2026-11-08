using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Weesite.Web.Tests;

/// <summary>The web app reading the sample site from the repo's seed/ directory, no storage needed.</summary>
internal sealed class SiteFactory : WebApplicationFactory<Program>
{
    /// <summary>Gets the repo's seed/ directory.</summary>
    public static string SeedPath { get; } = Path.Combine(FindRepoRoot(), "seed");

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Content:Path", SeedPath);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Weesite.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Weesite.slnx not found above the test binaries.");
    }
}
