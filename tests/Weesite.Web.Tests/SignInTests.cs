using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>Sign-in for /admin: the trust rules, the pages and the policies (ADR 0004).</summary>
[ClassDataSource<SharedSite>(Shared = SharedType.PerTestSession)]
internal sealed class SignInTests(SharedSite shared)
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
    public async Task Email_addresses_fold_only_ascii_letters()
    {
        // T-0122: Unicode folding made "admın@" (dotless i) the same as "admin@".
        await Assert.That(ConfiguredAdmins.Normalize(" Admin@Example.com ")).IsEqualTo("ADMIN@EXAMPLE.COM");
        await Assert.That(ConfiguredAdmins.Normalize("admın@example.com")).IsNotEqualTo(ConfiguredAdmins.Normalize("admin@example.com"));
        await Assert.That(ConfiguredAdmins.Normalize("ſam@example.com")).IsNotEqualTo(ConfiguredAdmins.Normalize("sam@example.com"));
    }

    [Test]
    [Arguments("/admin/posts", "/admin/posts")]
    [Arguments("/", "/")]
    [Arguments("//evil.example/x", "/admin")]
    [Arguments("/\\evil.example", "/admin")]
    [Arguments("/\t/evil.example", "/admin")]
    [Arguments("/\r\n/evil.example", "/admin")]
    [Arguments("/admin/\u0000x", "/admin")]
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
        var factory = shared.Factory;
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri("/admin", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.ToString()).IsEqualTo("http://localhost/admin/sign-in?returnUrl=%2Fadmin");
    }

    [Test]
    public async Task Sign_in_page_says_so_when_no_provider_is_configured()
    {
        var factory = shared.Factory;
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

        await Assert.That(html).Matches("<button class=\"btn provider\" type=\"submit\" name=\"provider\" value=\"Microsoft\"><img src=\"/icons/microsoft(\\.[a-z0-9]+)?\\.svg\" alt width=\"20\" height=\"20\" />Sign in with Microsoft</button>");
        await Assert.That(html).DoesNotContain("value=\"Google\"");
        await Assert.That(html).Contains("name=\"__RequestVerificationToken\"");
        await Assert.That(html).Contains("name=\"returnUrl\" value=\"/admin/posts\"");
    }

    [Test]
    [Arguments("Production", SignIn.CookieName, CookieSecurePolicy.Always)]
    [Arguments("Development", SignIn.DevelopmentCookieName, CookieSecurePolicy.SameAsRequest)]
    public async Task The_sign_in_cookie_is_a_host_cookie_outside_development(string environment, string name, CookieSecurePolicy secure)
    {
        // T-0122: __Host- needs Secure, no Domain and the root path; plain HTTP in Development can't have it.
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(b => b.UseEnvironment(environment));
        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(EditorClaims.Scheme).Cookie;

        await Assert.That(cookie.Name).IsEqualTo(name);
        await Assert.That(cookie.SecurePolicy).IsEqualTo(secure);
        await Assert.That(cookie.Path).IsEqualTo("/");
        await Assert.That(cookie.Domain).IsNull();
    }

    [Test]
    [Arguments("boss@example.com", "sub-1", true)]
    [Arguments("stranger@example.com", "sub-2", false)]
    [Arguments("boss@example.com", "", false)]
    public async Task After_the_provider_only_an_allowlisted_address_with_a_subject_gets_in(string email, string subject, bool allowed)
    {
        // T-0122: the handler after the provider's sign-in had no test of its own.
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(b => b
            .UseSetting("Site:Admins", "boss@example.com")
            .UseSetting("Authentication:Google:ClientId", "g-id")
            .UseSetting("Authentication:Google:ClientSecret", "g-secret"));
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(SignIn.Google);
        using var scope = factory.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject), new Claim("email", email), new Claim("email_verified", "true")], "oidc"));
        var context = new TokenValidatedContext(http, new AuthenticationScheme(SignIn.Google, null, typeof(OpenIdConnectHandler)), options, user, new AuthenticationProperties());

        await options.Events.OnTokenValidated(context);

        var audit = await factory.Services.GetRequiredService<IAuditLog>().LatestAsync(1, CancellationToken.None);
        if (allowed)
        {
            await Assert.That(context.Result).IsNull();
            await Assert.That(context.Principal!.FindFirst(ClaimTypes.Email)?.Value).IsEqualTo(email);
            await Assert.That(context.Principal!.IsInRole(nameof(EditorRole.Admin))).IsTrue();
            await Assert.That(EditorClaims.SignedInTime(context.Principal!)).IsNotNull();
            await Assert.That(http.Response.Headers.SetCookie.ToString()).Contains("weesite.editor=1");
            await Assert.That(audit.Single()).IsEqualTo(audit.Single() with { Actor = email, Action = "sign-in", Target = SignIn.Google });
        }
        else
        {
            // Refused without recording the address: only that someone tried.
            await Assert.That(context.Result?.Handled).IsTrue();
            await Assert.That(http.Response.Headers.Location.ToString()).IsEqualTo("/admin/denied");
            await Assert.That(http.Response.Headers.SetCookie.ToString()).DoesNotContain("weesite.editor");
            await Assert.That(audit.Single()).IsEqualTo(audit.Single() with { Actor = "(not an editor)", Action = "sign-in-refused", Target = SignIn.Google });
        }
    }

    [Test]
    public async Task An_editor_s_visit_to_the_admin_renews_the_editor_marker_and_a_visitor_s_doesn_t()
    {
        // The marker keeps an editor's own page views out of the counts for a month (EditorMarker).
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        using var request = TestAuth.As("/admin", EditorRole.Author);

        using var editor = await client.SendAsync(request);
        using var visitor = await client.GetAsync(new Uri("/admin/sign-in", UriKind.Relative));
        using var page = await client.GetAsync(new Uri("/", UriKind.Relative));

        var cookie = editor.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("weesite.editor=", StringComparison.Ordinal));
        await Assert.That(cookie).Contains("max-age=2592000");
        await Assert.That(cookie).DoesNotContain("httponly");
        await Assert.That(visitor.Headers.TryGetValues("Set-Cookie", out var none) && none.Any(c => c.StartsWith("weesite.editor=", StringComparison.Ordinal))).IsFalse();
        await Assert.That(await page.Content.ReadAsStringAsync()).Contains("<a class=\"editor-link\" href=\"/admin\" hidden");
    }

    [Test]
    public async Task Both_providers_show_their_account_chooser()
    {
        await using var app = new SiteFactory();
        await using var factory = app.WithWebHostBuilder(b => b
            .UseSetting("Authentication:Microsoft:ClientId", "ms-id")
            .UseSetting("Authentication:Microsoft:ClientSecret", "ms-secret")
            .UseSetting("Authentication:Google:ClientId", "g-id")
            .UseSetting("Authentication:Google:ClientSecret", "g-secret"));
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();

        await Assert.That(options.Get(SignIn.Microsoft).Prompt).IsEqualTo("select_account");
        await Assert.That(options.Get(SignIn.Google).Prompt).IsEqualTo("select_account");
    }

    [Test]
    [Arguments("microsoft")]
    [Arguments("google")]
    public async Task The_provider_logos_are_served(string provider)
    {
        var factory = shared.Factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri($"/icons/{provider}.svg", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("image/svg+xml");
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
        var factory = shared.Factory;
        using var client = factory.CreateClient();
        using var request = TestAuth.As("/admin", role);

        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(html).Contains("<p class=\"name\">Test Editor</p>");
        await Assert.That(html).Contains($"<p class=\"role\">{label}<span class=\"visually-hidden\">, editor@example.com</span></p>");
        await Assert.That(html).Contains("<h1>Posts</h1>");
        await Assert.That(html).Contains("action=\"/admin/sign-out\"");
    }
}
