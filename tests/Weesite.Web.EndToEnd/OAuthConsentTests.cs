using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TUnit.Playwright;

namespace Weesite.Web.EndToEnd;

/// <summary>
/// The OAuth consent page in a real browser (ADR 0007, T-0082): accessible, within the CSP, and
/// its decision reaches the app. Browsers check the redirect after a form post against
/// <c>form-action</c>, so the page has to allow the app's origin.
/// </summary>
[ClassDataSource<SiteServer>(Shared = SharedType.PerTestSession)]
internal sealed class OAuthConsentTests(SiteServer site) : PageTest
{
    // Registered without a port: native apps listen on whatever port they get (RFC 8252).
    private const string Callback = "http://127.0.0.1/callback";

    [Before(Test)]
    public async Task SignInAsAdmin() =>
        await Page.SetExtraHTTPHeadersAsync(new Dictionary<string, string> { ["X-Test-Role"] = "Admin" });

    [Test]
    public async Task Allowing_sends_the_code_to_the_app()
    {
        var clientId = await RegisterAsync();
        using var app = new LoopbackApp();
        var callback = $"http://127.0.0.1:{app.Port}/callback";
        await Page.WatchCspAsync();

        await Page.OpenAsync(site.At(AuthorizePath(clientId, callback)));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Allow access to your account?" })).ToBeVisibleAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();
        var screens = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens");
        await Page.ScreenshotAsync(new() { Path = Path.Combine(screens, "oauth-consent-1280-light.png"), FullPage = true });
        await Page.SetViewportSizeAsync(390, 844);
        await Page.ScreenshotAsync(new() { Path = Path.Combine(screens, "oauth-consent-390-light.png"), FullPage = true });

        await Page.GetByRole(AriaRole.Button, new() { Name = "Allow" }).ClickAsync();
        var target = await app.Request.WaitAsync(TimeSpan.FromSeconds(10));

        var reply = QueryHelpers.ParseQuery(target[target.IndexOf('?', StringComparison.Ordinal)..]);
        await Assert.That(target).StartsWith("/callback?");
        await Assert.That(reply["code"].ToString()).IsNotEmpty();
        await Assert.That(reply["state"].ToString()).IsEqualTo("xyz");
    }

    [Test]
    public async Task Connected_apps_are_listed_and_revoked_on_your_profile()
    {
        var store = site.Services.GetRequiredService<Weesite.Web.Agents.OAuth.IOAuthStore>();
        var now = DateTimeOffset.UtcNow;
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await store.AddGrantAsync(new Weesite.Web.Agents.OAuth.OAuthGrant(id, "editor@example.com", "c", "Claude Code", site.At("/mcp/editor"), now.AddDays(-2), now.AddHours(-1), "h", null, now.AddDays(30), CanPublish: true), CancellationToken.None);
        await Page.WatchCspAsync();

        await Page.OpenAsync(site.At("/admin/profile"));
        var apps = Page.GetByRole(AriaRole.Region, new() { Name = "Connected apps" });
        await Expect(apps.GetByRole(AriaRole.Rowheader, new() { Name = "Claude Code" })).ToBeVisibleAsync();
        var axe = await Page.RunAxe();
        await Assert.That(axe.Violations.Select(v => $"{v.Id}: {v.Help}")).IsEmpty();
        await Assert.That(await Page.CspViolationsAsync()).IsEmpty();
        var screens = Path.Combine(SiteFactory.SeedPath, "..", "artifacts", "screens");
        await apps.ScreenshotAsync(new() { Path = Path.Combine(screens, "admin-connected-apps-1280-light.png") });

        await apps.GetByRole(AriaRole.Button, new() { Name = "Revoke Claude Code" }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Status)).ToHaveTextAsync("The app's access is revoked.");
        await Assert.That(await store.FindGrantAsync(id, CancellationToken.None)).IsNull();
    }

    private async Task<string> RegisterAsync()
    {
        using var http = new HttpClient { BaseAddress = site.BaseAddress };
        using var response = await http.PostAsJsonAsync(new Uri("/oauth/register", UriKind.Relative), new { client_name = "Browser test app", redirect_uris = new[] { Callback } });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    private static string AuthorizePath(string clientId, string callback)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = callback,
            ["state"] = "xyz",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });
    }

    /// <summary>The app's side: a loopback listener that takes one request and answers it.</summary>
    private sealed class LoopbackApp : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);

        public LoopbackApp()
        {
            _listener.Start();
            Request = AcceptAsync();
        }

        public int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Gets the path and query of the first request.</summary>
        public Task<string> Request { get; }

        public void Dispose() => _listener.Dispose();

        private async Task<string> AcceptAsync()
        {
            using var connection = await _listener.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var line = await reader.ReadLineAsync() ?? "";
            var body = "Done. You can close this tab."u8.ToArray();
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
            return line.Split(' ') is [_, var target, ..] ? target : line;
        }
    }
}
