using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>
/// Signs in a fake editor for tests (brief, Testing): a request with an <c>X-Test-Role</c> header
/// (<c>Admin</c> or <c>Author</c>) is that editor; without it, nobody is signed in.
/// </summary>
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

/// <summary>Helpers for factories with the test sign-in.</summary>
internal static class TestAuth
{
    /// <summary>Gets a factory whose requests authenticate with <see cref="TestAuthHandler"/>.</summary>
    /// <param name="factory">The site.</param>
    /// <returns>The factory.</returns>
    public static WebApplicationFactory<Program> WithTestSignIn(this SiteFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName);
        }));

    /// <summary>Builds a request as a signed-in editor.</summary>
    /// <param name="path">The path.</param>
    /// <param name="role">The editor's role.</param>
    /// <returns>The request.</returns>
    public static HttpRequestMessage As(string path, EditorRole role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-Test-Role", role.ToString());
        return request;
    }
}
