using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Weesite.Web.Admin;
using Weesite.Web.Email;

namespace Weesite.Web.Tests;

/// <summary>
/// The Key Vault secrets in the background (T-0192): the host doesn't wait for them, public pages
/// don't either, and sign-in, the admins and mail do.
/// </summary>
internal sealed class BackgroundSecretsTests
{
    private static readonly TimeSpan Enough = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, string?> Secrets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Authentication:Google:ClientId"] = "g-id",
        ["Authentication:Google:ClientSecret"] = "g-secret",
        ["Site:Admins"] = "admin@example.com",
        ["Email:Smtp:Host"] = "smtp.example.com",
        ["Email:From"] = "noreply@example.com",
    };

    // Secrets that come in when the test says so, as Key Vault's do some seconds into a start.
    private static (BackgroundSecrets Source, TaskCompletionSource Release) Held()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BackgroundSecrets(async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return Secrets;
        });
        return (source, release);
    }

    private static ConfigurationManager Configuration(BackgroundSecrets source)
    {
        var configuration = new ConfigurationManager();
        ((IConfigurationBuilder)configuration).Add(source);
        return configuration;
    }

    private static WebApplicationFactory<Program> With(SiteFactory site, BackgroundSecrets source) =>
        site.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, configuration) => configuration.Add(source)));

    [Test]
    public async Task The_secrets_arrive_with_a_reload()
    {
        var (source, release) = Held();
        using var configuration = Configuration(source);
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = ChangeToken.OnChange(((IConfiguration)configuration).GetReloadToken, () => reloaded.TrySetResult());

        await Assert.That(configuration["Site:Admins"]).IsNull();

        release.SetResult();
        await reloaded.Task.WaitAsync(Enough);

        await Assert.That(configuration["Site:Admins"]).IsEqualTo("admin@example.com");
        await Assert.That(source.Count).IsEqualTo(Secrets.Count);
        await Assert.That(source.LoadedAt).IsNotNull();
    }

    [Test]
    public async Task A_failed_load_is_tried_again()
    {
        var attempts = 0;
        var source = new BackgroundSecrets(_ => Interlocked.Increment(ref attempts) == 1
            ? throw new InvalidOperationException("No token yet.")
            : Task.FromResult<IReadOnlyDictionary<string, string?>>(Secrets));
        using var configuration = Configuration(source);

        await source.Loaded.WaitAsync(Enough);

        await Assert.That(configuration["Site:Admins"]).IsEqualTo("admin@example.com");
        await Assert.That(source.Failures).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Without_background_secrets_nothing_waits()
    {
        using var configuration = new ConfigurationManager();
        var gate = new SecretsGate(configuration);
        var ran = false;
        gate.OnLoaded(() => ran = true);

        await gate.WhenLoadedAsync(CancellationToken.None);

        await Assert.That(gate.IsLoaded).IsTrue();
        await Assert.That(ran).IsTrue();
    }

    [Test]
    public async Task A_role_lookup_waits_for_the_configured_admins()
    {
        // An admin's cookie is checked again on any request: a replica without the admins yet
        // would otherwise sign them out.
        var (source, release) = Held();
        using var configuration = Configuration(source);
        var admins = new ConfiguredAdmins(configuration, new SecretsGate(configuration));

        var role = admins.FindRoleAsync("Admin@Example.com", CancellationToken.None);
        await Assert.That(role.IsCompleted).IsFalse();

        release.SetResult();

        await Assert.That(await role.WaitAsync(Enough)).IsEqualTo(EditorRole.Admin);
        await Assert.That(admins.Addresses).IsEquivalentTo(["admin@example.com"]);
    }

    [Test]
    public async Task Mail_is_set_up_once_the_secrets_are_in()
    {
        var (source, release) = Held();
        using var configuration = Configuration(source);
        IEmailSender sender = new ConfiguredEmailSender(configuration, new SecretsGate(configuration));

        var ready = sender.WhenReadyAsync(CancellationToken.None);
        await Assert.That(ready.IsCompleted).IsFalse();
        await Assert.That(sender.IsConfigured).IsFalse();

        release.SetResult();
        await ready.WaitAsync(Enough);

        await Assert.That(sender.IsConfigured).IsTrue();
    }

    [Test]
    public async Task Public_pages_dont_wait_for_the_secrets_and_sign_in_does()
    {
        var (source, release) = Held();
        await using var site = new SiteFactory();
        await using var factory = With(site, source);
        using var client = factory.CreateClient();

        using var home = await client.GetAsync(new Uri("/", UriKind.Relative));
        await Assert.That(home.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var signIn = client.GetStringAsync(new Uri("/admin/sign-in", UriKind.Relative));
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await Assert.That(signIn.IsCompleted).IsFalse();

        release.SetResult();
        var html = await signIn.WaitAsync(Enough);

        // Google's client came with the secrets; Microsoft's didn't, so its scheme stays out.
        await Assert.That(html).Contains("value=\"Google\"");
        await Assert.That(html).DoesNotContain("value=\"Microsoft\"");
        var schemes = factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        await Assert.That(await schemes.GetSchemeAsync(SignIn.Google)).IsNotNull();
        await Assert.That(await schemes.GetSchemeAsync(SignIn.Microsoft)).IsNull();
    }

    [Test]
    public async Task Sign_in_answers_503_when_the_secrets_dont_come()
    {
        var (source, _) = Held();
        await using var site = new SiteFactory();
        await using var factory = With(site, source);
        factory.Services.GetRequiredService<SecretsGate>().Limit = TimeSpan.FromMilliseconds(200);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/admin/sign-in", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(response.Headers.RetryAfter?.Delta).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    [Arguments("/admin")]
    [Arguments("/admin/sign-in")]
    [Arguments("/signin-google")]
    [Arguments("/mcp/editor")]
    [Arguments("/oauth/authorize")]
    [Arguments("/.well-known/microsoft-identity-association.json")]
    public async Task These_paths_wait(string path) =>
        await Assert.That(SecretsGate.Needs(new Microsoft.AspNetCore.Http.PathString(path))).IsTrue();

    [Test]
    [Arguments("/")]
    [Arguments("/blog")]
    [Arguments("/mcp")]
    [Arguments("/search")]
    [Arguments("/health")]
    [Arguments("/administrator")]
    public async Task These_paths_dont(string path) =>
        await Assert.That(SecretsGate.Needs(new Microsoft.AspNetCore.Http.PathString(path))).IsFalse();
}
