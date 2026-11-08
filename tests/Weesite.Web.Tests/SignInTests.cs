using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Sign-in for /admin: the trust rules, the pages and the policies (ADR 0004).</summary>
internal sealed class SignInTests
{
    private static readonly HashSet<string> NoTrustedTenants = [];

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "oidc"));

    [Test]
    public async Task Google_emails_count_only_when_verified()
    {
        await Assert.That(SignIn.VerifiedEmail(SignIn.Google, User(("email", "a@example.com"), ("email_verified", "true")), NoTrustedTenants)).IsEqualTo("a@example.com");
        await Assert.That(SignIn.VerifiedEmail(SignIn.Google, User(("email", "a@example.com"), ("email_verified", "false")), NoTrustedTenants)).IsNull();
        await Assert.That(SignIn.VerifiedEmail(SignIn.Google, User(("email", "a@example.com")), NoTrustedTenants)).IsNull();
    }

    [Test]
    public async Task Microsoft_emails_count_for_personal_accounts_and_trusted_tenants_only()
    {
        const string Work = "11111111-2222-3333-4444-555555555555";
        var personal = User(("email", "a@example.com"), ("tid", SignIn.ConsumerTenant));
        var work = User(("email", "a@example.com"), ("tid", Work));

        await Assert.That(SignIn.VerifiedEmail(SignIn.Microsoft, personal, NoTrustedTenants)).IsEqualTo("a@example.com");
        await Assert.That(SignIn.VerifiedEmail(SignIn.Microsoft, work, NoTrustedTenants)).IsNull();
        await Assert.That(SignIn.VerifiedEmail(SignIn.Microsoft, work, new HashSet<string> { Work })).IsEqualTo("a@example.com");
        await Assert.That(SignIn.VerifiedEmail(SignIn.Microsoft, User(("tid", SignIn.ConsumerTenant)), NoTrustedTenants)).IsNull();
    }

    [Test]
    public async Task Microsoft_issuer_must_name_the_tokens_own_tenant()
    {
        const string Tenant = "11111111-2222-3333-4444-555555555555";
        var token = new JsonWebToken(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object> { ["tid"] = Tenant },
        }));

        await Assert.That(SignIn.ValidateMicrosoftIssuer($"https://login.microsoftonline.com/{Tenant}/v2.0", token)).IsEqualTo($"https://login.microsoftonline.com/{Tenant}/v2.0");
        await Assert.That(() => SignIn.ValidateMicrosoftIssuer("https://login.microsoftonline.com/99999999-0000-0000-0000-000000000000/v2.0", token)).Throws<SecurityTokenInvalidIssuerException>();
    }

    [Test]
    [Arguments("/admin/posts", "/admin/posts")]
    [Arguments("/", "/")]
    [Arguments("//evil.example/x", "/admin")]
    [Arguments("/\\evil.example", "/admin")]
    [Arguments("https://evil.example/", "/admin")]
    [Arguments(null, "/admin")]
    public async Task Return_urls_stay_on_the_site(string? requested, string expected) =>
        await Assert.That(AdminEndpoints.LocalUrl(requested)).IsEqualTo(expected);

    [Test]
    public async Task Configured_admins_match_regardless_of_case_and_spacing()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new("Site:Admins", " One@Example.com ; two@example.com,")]).Build();
        var admins = new ConfiguredAdmins(configuration);

        await Assert.That(await admins.FindRoleAsync("one@example.com", CancellationToken.None)).IsEqualTo(EditorRole.Admin);
        await Assert.That(await admins.FindRoleAsync("TWO@example.com", CancellationToken.None)).IsEqualTo(EditorRole.Admin);
        await Assert.That(await admins.FindRoleAsync("three@example.com", CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Admin_redirects_to_sign_in_when_signed_out()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/admin", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("http://localhost/admin/sign-in?returnUrl=%2Fadmin");
    }

    [Test]
    public async Task Sign_in_page_says_so_when_no_provider_is_configured()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/admin/sign-in", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(html).Contains("No sign-in provider is configured");
        await Assert.That(response.Headers.GetValues("X-Robots-Tag")).Contains("noindex, nofollow");
        await Assert.That(response.Headers.CacheControl?.NoStore).IsTrue();
    }

    [Test]
    public async Task Sign_in_page_offers_the_configured_providers()
    {
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(b => b
            .UseSetting("Authentication:Microsoft:ClientId", "ms-id")
            .UseSetting("Authentication:Microsoft:ClientSecret", "ms-secret")
            .UseSetting("Authentication:Google:ClientId", "g-id"));
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/admin/sign-in?returnUrl=%2Fadmin%2Fposts", UriKind.Relative));

        await Assert.That(html).Contains("<button type=\"submit\" name=\"provider\" value=\"Microsoft\">Sign in with Microsoft</button>");
        await Assert.That(html).DoesNotContain("value=\"Google\"");
        await Assert.That(html).Contains("name=\"__RequestVerificationToken\"");
        await Assert.That(html).Contains("name=\"returnUrl\" value=\"/admin/posts\"");
    }

    [Test]
    public async Task Sign_out_needs_the_antiforgery_token()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var form = new FormUrlEncodedContent([]);
        using var response = await client.PostAsync(new Uri("/admin/sign-out", UriKind.Relative), form);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    [Arguments(EditorRole.Admin, "admin")]
    [Arguments(EditorRole.Author, "author")]
    public async Task Editors_see_the_admin_page(EditorRole role, string label)
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var client = factory.CreateClient();
        using var request = TestAuth.As("/admin", role);

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains($"Signed in as Test Editor (editor@example.com), {label}.");
        await Assert.That(html).Contains("action=\"/admin/sign-out\"");
    }
}
