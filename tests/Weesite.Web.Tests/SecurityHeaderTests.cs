using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Weesite.Web.Tests;

/// <summary>The security headers (T-0053; brief, Security hardening).</summary>
internal sealed class SecurityHeaderTests
{
    [Test]
    [Arguments("/")]
    [Arguments("/blog/hello-world")]
    [Arguments("/feed.xml")]
    [Arguments("/no-such-page")]
    [Arguments("/admin")]
    public async Task Every_response_gets_the_policy_and_headers(string path)
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(Header(response, "Content-Security-Policy")).IsEqualTo(SecurityHeaders.ContentSecurityPolicy);
        await Assert.That(Header(response, "X-Content-Type-Options")).IsEqualTo("nosniff");
        await Assert.That(Header(response, "Referrer-Policy")).IsEqualTo("strict-origin-when-cross-origin");
        await Assert.That(Header(response, "Permissions-Policy")).Contains("camera=()");
    }

    [Test]
    public async Task The_policy_forbids_inline_code_framing_and_plugins()
    {
        var policy = SecurityHeaders.ContentSecurityPolicy;

        await Assert.That(policy).Contains("script-src 'self';");
        await Assert.That(policy).Contains("style-src 'self';");
        await Assert.That(policy).Contains("object-src 'none'");
        await Assert.That(policy).Contains("frame-ancestors 'none'");
        await Assert.That(policy).DoesNotContain("unsafe");
    }

    [Test]
    public async Task Only_the_sign_in_page_may_send_its_form_to_the_identity_providers()
    {
        await using var factory = new SiteFactory();
        using var client = factory.CreateClient();

        using var signIn = await client.GetAsync(new Uri("/admin/sign-in", UriKind.Relative));

        await Assert.That(Header(signIn, "Content-Security-Policy")).IsEqualTo(SecurityHeaders.SignInContentSecurityPolicy);
        await Assert.That(SecurityHeaders.SignInContentSecurityPolicy).Contains("form-action 'self' https://login.microsoftonline.com https://accounts.google.com;");
    }

    [Test]
    public async Task Https_responses_outside_development_get_hsts()
    {
        await using var site = new SiteFactory();
        await using var production = site.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://example.org") });

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        await Assert.That(Header(response, "Strict-Transport-Security")).IsEqualTo("max-age=31536000");
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;
}
