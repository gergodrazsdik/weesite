using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weesite.Web.Admin;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The web app on a copy of the repo's seed/ directory (so editor tests can save without touching
/// the seed), no storage needed. A page that sends <c>X-Test-Role: Admin</c> or <c>Author</c> is
/// signed in as that editor (brief, Testing); without it, nobody is.
/// </summary>
internal sealed class SiteFactory : WebApplicationFactory<Program>
{
    private readonly string _copy = CopySeed();

    /// <summary>Gets this site's copy of the seed, for tests that change it before the server starts.</summary>
    public string ContentPath => _copy;

    /// <summary>Gets the repo's seed/ directory.</summary>
    public static string SeedPath { get; } = Path.Combine(FindRepoRoot(), "seed");

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Content:Path", _copy);

        // Placeholder clients, so the sign-in page shows both providers' buttons (axe, screenshots).
        // No test starts a sign-in: tests sign in through TestAuthHandler.
        builder.UseSetting("Authentication:Microsoft:ClientId", "e2e-microsoft");
        builder.UseSetting("Authentication:Microsoft:ClientSecret", "e2e-secret");
        builder.UseSetting("Authentication:Google:ClientId", "e2e-google");
        builder.UseSetting("Authentication:Google:ClientSecret", "e2e-secret");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName);
        });
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_copy))
        {
            Directory.Delete(_copy, recursive: true);
        }
    }

    private static string CopySeed()
    {
        var root = Directory.CreateTempSubdirectory("weesite-e2e-").FullName;
        foreach (var file in Directory.EnumerateFiles(SeedPath, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(SeedPath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return root;
    }

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

/// <summary>Signs in a fake editor when a request carries <c>X-Test-Role</c>.</summary>
[SuppressMessage("Performance", "CA1812", Justification = "Created by the authentication middleware.")]
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-Role", out var value) || !Enum.TryParse<EditorRole>(value, out var role))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = EditorClaims.Principal("Test", "test-user", "editor@example.com", "Test Editor", role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
