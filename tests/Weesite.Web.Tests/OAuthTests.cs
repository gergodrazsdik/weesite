using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;

namespace Weesite.Web.Tests;

/// <summary>The authorization server for the editor MCP endpoint (ADR 0007, T-0082).</summary>
internal sealed partial class OAuthTests
{
    private const string Redirect = "http://127.0.0.1:5555/callback";
    private const string Resource = "http://localhost/mcp/editor";

    private static readonly string[] ExtraGrants = ["authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code"];

    [Test]
    public async Task The_metadata_describes_the_server()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();

        using var response = await client.GetAsync(new Uri("/.well-known/oauth-authorization-server", UriKind.Relative));
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("issuer").GetString()).IsEqualTo("http://localhost");
        await Assert.That(json.GetProperty("authorization_endpoint").GetString()).IsEqualTo("http://localhost/oauth/authorize");
        await Assert.That(json.GetProperty("token_endpoint").GetString()).IsEqualTo("http://localhost/oauth/token");
        await Assert.That(json.GetProperty("registration_endpoint").GetString()).IsEqualTo("http://localhost/oauth/register");
        await Assert.That(json.GetProperty("code_challenge_methods_supported")[0].GetString()).IsEqualTo("S256");
        await Assert.That(json.GetProperty("client_id_metadata_document_supported").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task A_client_registers_itself_with_allowed_redirect_uris_only()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();

        using var ok = await client.PostAsJsonAsync(new Uri("/oauth/register", UriKind.Relative), new { client_name = "Test app", redirect_uris = new[] { Redirect } });
        var registered = await ok.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(registered.GetProperty("client_id").GetString()).IsNotNull().And.IsNotEmpty();
        await Assert.That(registered.GetProperty("token_endpoint_auth_method").GetString()).IsEqualTo("none");

        // Grant types Weesite doesn't use are left out rather than refused (RFC 7591).
        using var extra = await client.PostAsJsonAsync(new Uri("/oauth/register", UriKind.Relative), new { redirect_uris = new[] { Redirect }, grant_types = ExtraGrants });
        var trimmed = await extra.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(extra.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(trimmed.GetProperty("grant_types").EnumerateArray().Select(g => g.GetString()!)).IsEquivalentTo(["authorization_code", "refresh_token"]);

        foreach (var (body, error) in new (object Body, string Error)[]
        {
            (new { redirect_uris = new[] { "http://example.com/callback" } }, "invalid_redirect_uri"),
            (new { redirect_uris = new[] { "javascript:alert(1)" } }, "invalid_redirect_uri"),
            (new { redirect_uris = new[] { "https://app.example/cb#x" } }, "invalid_redirect_uri"),
            (new { redirect_uris = Array.Empty<string>() }, "invalid_redirect_uri"),
            (new { redirect_uris = new[] { Redirect }, token_endpoint_auth_method = "client_secret_basic" }, "invalid_client_metadata"),
            (new { redirect_uris = new[] { Redirect }, grant_types = new[] { "client_credentials" } }, "invalid_client_metadata"),
        })
        {
            using var refused = await client.PostAsJsonAsync(new Uri("/oauth/register", UriKind.Relative), body);
            var json = await refused.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(refused.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(json.GetProperty("error").GetString()).IsEqualTo(error);
        }
    }

    [Test]
    public async Task Authorizing_needs_the_editor_sign_in()
    {
        await using var site = new OAuthSite();
        using var anonymous = site.Client(role: null);
        var clientId = await site.RegisterAsync(Redirect);

        using var response = await anonymous.GetAsync(AuthorizeUrl(clientId, Redirect, Challenge(Verifier())));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.OriginalString).StartsWith("http://localhost/admin/sign-in?returnUrl=%2Foauth%2Fauthorize");
    }

    [Test]
    public async Task An_unknown_client_or_redirect_is_shown_an_error_and_never_sent_anything()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect);
        var challenge = Challenge(Verifier());

        using var unknown = await client.GetAsync(AuthorizeUrl("nobody", Redirect, challenge));
        using var elsewhere = await client.GetAsync(AuthorizeUrl(clientId, "https://evil.example/callback", challenge));

        await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await unknown.Content.ReadAsStringAsync()).Contains("isn&#x27;t registered");
        await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await elsewhere.Content.ReadAsStringAsync()).Contains("didn&#x27;t register");
    }

    [Test]
    public async Task Requests_without_PKCE_or_for_another_resource_go_back_with_an_error()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect);

        using var noPkce = await client.GetAsync(AuthorizeUrl(clientId, Redirect, challenge: null));
        using var plain = await client.GetAsync(AuthorizeUrl(clientId, Redirect, Challenge(Verifier()), method: "plain"));
        using var elsewhere = await client.GetAsync(AuthorizeUrl(clientId, Redirect, Challenge(Verifier()), resource: "https://other.example/mcp"));

        foreach (var (response, error) in new[] { (noPkce, "invalid_request"), (plain, "invalid_request"), (elsewhere, "invalid_target") })
        {
            var reply = Reply(response);
            await Assert.That(response.Headers.Location!.OriginalString).StartsWith(Redirect + "?");
            await Assert.That(reply["error"].ToString()).IsEqualTo(error);
            await Assert.That(reply["state"].ToString()).IsEqualTo("xyz");
            await Assert.That(reply["iss"].ToString()).IsEqualTo("http://localhost");
        }
    }

    [Test]
    public async Task The_consent_page_says_who_asks_and_lets_its_form_reach_the_app()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect, "Claude Code");

        using var response = await client.GetAsync(AuthorizeUrl(clientId, Redirect, Challenge(Verifier())));
        var page = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page).Contains("Claude Code wants to work on this site for you.");
        await Assert.That(page).Contains("It acts as editor@example.com, with your role (Admin)");
        await Assert.That(page).Contains("you go back to 127.0.0.1:5555");
        await Assert.That(page).Contains("Weesite can&#x27;t confirm who made it");
        await Assert.That(page).Contains("name=\"publish\" value=\"no\" checked");
        await Assert.That(response.Headers.GetValues("Content-Security-Policy").Single()).Contains("form-action 'self' http://127.0.0.1:5555;");
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
    }

    [Test]
    public async Task A_loopback_redirect_works_on_any_port()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync("http://127.0.0.1/callback");

        using var other = await client.GetAsync(AuthorizeUrl(clientId, "http://127.0.0.1:49152/callback", Challenge(Verifier())));
        using var path = await client.GetAsync(AuthorizeUrl(clientId, "http://127.0.0.1:49152/other", Challenge(Verifier())));

        await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(path.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Consent_gives_a_code_the_client_exchanges_once_for_tokens()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect, "Test app");
        var verifier = Verifier();

        var reply = await OAuthSite.ConsentAsync(client, clientId, Redirect, Challenge(verifier));
        var code = reply["code"].ToString();
        await Assert.That(reply["state"].ToString()).IsEqualTo("xyz");
        await Assert.That(reply["iss"].ToString()).IsEqualTo("http://localhost");

        var tokens = await OAuthSite.TokenAsync(client, ("grant_type", "authorization_code"), ("code", code), ("client_id", clientId), ("redirect_uri", Redirect), ("code_verifier", verifier), ("resource", Resource));
        await Assert.That(tokens.GetProperty("token_type").GetString()).IsEqualTo("Bearer");
        await Assert.That(tokens.GetProperty("expires_in").GetInt32()).IsEqualTo(3600);

        var info = await site.Tokens.ValidateAsync(tokens.GetProperty("access_token").GetString(), Resource, CancellationToken.None);
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Email).IsEqualTo("editor@example.com");
        await Assert.That(info.ClientName).IsEqualTo("Test app");
        await Assert.That(await site.Tokens.ValidateAsync(tokens.GetProperty("access_token").GetString(), "http://localhost/mcp", CancellationToken.None)).IsNull();

        // Once only.
        var again = await OAuthSite.TokenErrorAsync(client, ("grant_type", "authorization_code"), ("code", code), ("client_id", clientId), ("redirect_uri", Redirect), ("code_verifier", verifier));
        await Assert.That(again).IsEqualTo("invalid_grant");

        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "oauth-grant" && e.Actor == "editor@example.com" && e.Target == "Test app (drafts only)")).IsTrue();
    }

    [Test]
    public async Task A_code_needs_its_verifier_client_and_redirect_and_expires_in_a_minute()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect);
        var verifier = Verifier();

        async Task<string> CodeAsync() => (await OAuthSite.ConsentAsync(client, clientId, Redirect, Challenge(verifier)))["code"].ToString();

        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "authorization_code"), ("code", await CodeAsync()), ("client_id", clientId), ("redirect_uri", Redirect), ("code_verifier", Verifier()))).IsEqualTo("invalid_grant");
        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "authorization_code"), ("code", await CodeAsync()), ("client_id", "other"), ("redirect_uri", Redirect), ("code_verifier", verifier))).IsEqualTo("invalid_grant");
        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "authorization_code"), ("code", await CodeAsync()), ("client_id", clientId), ("redirect_uri", "http://127.0.0.1:5555/other"), ("code_verifier", verifier))).IsEqualTo("invalid_grant");

        var late = await CodeAsync();
        site.Clock.Advance(TimeSpan.FromSeconds(61));
        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "authorization_code"), ("code", late), ("client_id", clientId), ("redirect_uri", Redirect), ("code_verifier", verifier))).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task Declining_sends_access_denied()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect);

        var reply = await OAuthSite.ConsentAsync(client, clientId, Redirect, Challenge(Verifier()), decision: "deny");

        await Assert.That(reply["error"].ToString()).IsEqualTo("access_denied");
        await Assert.That(reply.ContainsKey("code")).IsFalse();
    }

    [Test]
    public async Task Refreshing_replaces_the_refresh_token_and_a_reused_one_ends_the_grant()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect);
        var first = await OAuthSite.GrantAsync(client, clientId);
        var firstRefresh = first.GetProperty("refresh_token").GetString()!;

        var second = await OAuthSite.TokenAsync(client, ("grant_type", "refresh_token"), ("refresh_token", firstRefresh), ("client_id", clientId));
        var secondRefresh = second.GetProperty("refresh_token").GetString()!;
        await Assert.That(secondRefresh).IsNotEqualTo(firstRefresh);
        await Assert.That(await site.Tokens.ValidateAsync(second.GetProperty("access_token").GetString(), Resource, CancellationToken.None)).IsNotNull();

        // Another client can't use it.
        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "refresh_token"), ("refresh_token", secondRefresh), ("client_id", "other"))).IsEqualTo("invalid_grant");

        // The first one again: someone else has a copy, so the grant ends, for both tokens.
        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "refresh_token"), ("refresh_token", firstRefresh), ("client_id", clientId))).IsEqualTo("invalid_grant");
        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "refresh_token"), ("refresh_token", secondRefresh), ("client_id", clientId))).IsEqualTo("invalid_grant");
        await Assert.That(await site.Tokens.ValidateAsync(second.GetProperty("access_token").GetString(), Resource, CancellationToken.None)).IsNull();
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "oauth-refresh-reuse")).IsTrue();
    }

    [Test]
    public async Task Access_tokens_expire_after_an_hour()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect);
        var access = (await OAuthSite.GrantAsync(client, clientId)).GetProperty("access_token").GetString();

        site.Clock.Advance(TimeSpan.FromMinutes(61));

        await Assert.That(await site.Tokens.ValidateAsync(access, Resource, CancellationToken.None)).IsNull();
        await Assert.That(await site.Tokens.ValidateAsync("not-a-token", Resource, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Revoking_a_token_ends_its_grant()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect);
        var tokens = await OAuthSite.GrantAsync(client, clientId);
        var refresh = tokens.GetProperty("refresh_token").GetString()!;

        using var form = new FormUrlEncodedContent([new("token", tokens.GetProperty("access_token").GetString()!), new("client_id", clientId)]);
        using var revoked = await client.PostAsync(new Uri("/oauth/revoke", UriKind.Relative), form);
        using var unknown = new FormUrlEncodedContent([new("token", "nothing")]);
        using var ignored = await client.PostAsync(new Uri("/oauth/revoke", UriKind.Relative), unknown);

        await Assert.That(revoked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(ignored.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId))).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task A_removed_editor_gets_no_more_tokens()
    {
        await using var site = new OAuthSite();
        using var client = site.Client();
        var clientId = await site.RegisterAsync(Redirect);
        var refresh = (await OAuthSite.GrantAsync(client, clientId)).GetProperty("refresh_token").GetString()!;

        await site.Services.GetRequiredService<IEditorStore>().RemoveAsync("editor@example.com", CancellationToken.None);

        await Assert.That(await OAuthSite.TokenErrorAsync(client, ("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", clientId))).IsEqualTo("invalid_grant");
    }

    [Test]
    public async Task A_client_described_by_a_metadata_document_needs_a_valid_one()
    {
        var documents = new Dictionary<string, string>
        {
            ["https://app.example/client.json"] = """{"client_id": "https://app.example/client.json", "client_name": "Documented app", "redirect_uris": ["http://127.0.0.1:5555/callback"]}""",
            ["https://app.example/wrong.json"] = """{"client_id": "https://app.example/other.json", "client_name": "Wrong", "redirect_uris": ["http://127.0.0.1:5555/callback"]}""",
            // As VS Code's own document is: it also lists the device code grant, which Weesite ignores.
            ["https://code.example/oauth/client-metadata.json"] = """{"client_name": "Visual Studio Code", "grant_types": ["authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:device_code"], "response_types": ["code"], "token_endpoint_auth_method": "none", "application_type": "native", "client_id": "https://code.example/oauth/client-metadata.json", "redirect_uris": ["http://127.0.0.1:33418/", "https://code.example/redirect"]}""",
            // As Claude's is: it also lists the JWT bearer grant.
            ["https://chat.example/oauth/mcp-oauth-client-metadata"] = """{"client_id": "https://chat.example/oauth/mcp-oauth-client-metadata", "client_name": "Claude", "redirect_uris": ["https://chat.example/api/mcp/auth_callback"], "grant_types": ["authorization_code", "refresh_token", "urn:ietf:params:oauth:grant-type:jwt-bearer"], "response_types": ["code"], "token_endpoint_auth_method": "none"}""",
            ["https://app.example/no-code.json"] = """{"client_id": "https://app.example/no-code.json", "client_name": "No code", "grant_types": ["client_credentials"], "redirect_uris": ["http://127.0.0.1:5555/callback"]}""",
            ["https://app.example/big.json"] = """{"client_id": "https://app.example/big.json", "client_name": """ + "\"" + new string('x', 6000) + "\"" + """, "redirect_uris": ["http://127.0.0.1:5555/callback"]}""",
        };
        await using var site = new OAuthSite(documents);
        using var client = site.Client();
        var challenge = Challenge(Verifier());

        using var documented = await client.GetAsync(AuthorizeUrl("https://app.example/client.json", Redirect, challenge));
        using var wrong = await client.GetAsync(AuthorizeUrl("https://app.example/wrong.json", Redirect, challenge));
        using var big = await client.GetAsync(AuthorizeUrl("https://app.example/big.json", Redirect, challenge));
        using var missing = await client.GetAsync(AuthorizeUrl("https://app.example/missing.json", Redirect, challenge));
        using var vsCode = await client.GetAsync(AuthorizeUrl("https://code.example/oauth/client-metadata.json", "http://127.0.0.1:33418/", challenge));
        using var noCode = await client.GetAsync(AuthorizeUrl("https://app.example/no-code.json", Redirect, challenge));
        using var claude = await client.GetAsync(AuthorizeUrl("https://chat.example/oauth/mcp-oauth-client-metadata", "https://chat.example/api/mcp/auth_callback", challenge));

        await Assert.That(documented.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var page = await documented.Content.ReadAsStringAsync();
        await Assert.That(page).Contains("Documented app wants to work on this site for you.");
        await Assert.That(page).Contains("The app is described by app.example.");
        await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(big.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(vsCode.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await vsCode.Content.ReadAsStringAsync()).Contains("Visual Studio Code wants to work on this site for you.");
        await Assert.That(noCode.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(claude.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await claude.Content.ReadAsStringAsync()).Contains("The app is described by chat.example.");
    }

    [Test]
    public async Task The_token_endpoint_answers_browsers_from_any_origin()
    {
        await using var site = new OAuthSite();
        using var client = site.Client(role: null);
        using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri("/oauth/token", UriKind.Relative));
        preflight.Headers.Add("Origin", "https://inspector.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(preflight);

        await Assert.That(response.Headers.GetValues("Access-Control-Allow-Origin").Single()).IsEqualTo("*");
    }

    [Test]
    [Arguments("8.8.8.8", true)]
    [Arguments("2001:4860:4860::8888", true)]
    [Arguments("10.1.2.3", false)]
    [Arguments("127.0.0.1", false)]
    [Arguments("169.254.169.254", false)]
    [Arguments("172.16.0.1", false)]
    [Arguments("192.168.1.1", false)]
    [Arguments("100.64.0.1", false)]
    [Arguments("0.0.0.0", false)]
    [Arguments("::1", false)]
    [Arguments("fd00::1", false)]
    [Arguments("fe80::1", false)]
    [Arguments("::ffff:10.0.0.1", false)]
    public async Task Metadata_documents_are_fetched_from_public_addresses_only(string address, bool allowed) =>
        await Assert.That(ClientRegistry.IsPublicAddress(IPAddress.Parse(address))).IsEqualTo(allowed);

    [Test]
    [Arguments("https://claude.ai/api/mcp/auth_callback", true)]
    [Arguments("http://localhost:33418/callback", true)]
    [Arguments("http://[::1]/callback", true)]
    [Arguments("cursor://anysphere.cursor-retrieval/oauth/callback", true)]
    [Arguments("http://example.com/callback", false)]
    [Arguments("https://user:pass@app.example/cb", false)]
    [Arguments("javascript:alert(1)", false)]
    [Arguments("data:text/html,x", false)]
    [Arguments("file:///etc/passwd", false)]
    [Arguments("not a uri", false)]
    public async Task Codes_go_to_https_loopback_or_app_schemes_only(string redirect, bool allowed) =>
        await Assert.That(ClientRegistry.IsAllowedRedirect(redirect)).IsEqualTo(allowed);

    [Test]
    public async Task A_verifier_matches_its_S256_challenge_only()
    {
        var verifier = Verifier();
        await Assert.That(OAuthTokens.VerifierMatches(verifier, Challenge(verifier))).IsTrue();
        await Assert.That(OAuthTokens.VerifierMatches(Verifier(), Challenge(verifier))).IsFalse();
        await Assert.That(OAuthTokens.VerifierMatches("short", Challenge("short"))).IsFalse();
        await Assert.That(OAuthTokens.VerifierMatches(verifier + "!", Challenge(verifier + "!"))).IsFalse();
    }

    [Test]
    [Category("containers")]
    public async Task The_table_store_hands_out_a_code_once_and_replaces_a_grant_only_at_its_version()
    {
        await using var azurite = new Testcontainers.Azurite.AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var store = new TableOAuthStore(new TableServiceClient(azurite.GetConnectionString()));
        var now = DateTimeOffset.UtcNow;

        await store.SaveClientAsync(new OAuthClient("client1", "App", [Redirect, "https://app.example/cb"], FromDocument: false), CancellationToken.None);
        var client = await store.FindClientAsync("client1", CancellationToken.None);
        await Assert.That(client!.RedirectUris).IsEquivalentTo([Redirect, "https://app.example/cb"]);
        await Assert.That(await store.FindClientAsync("https://app.example/client.json", CancellationToken.None)).IsNull();

        await store.SaveCodeAsync(new AuthorizationCode("abc123", "client1", "App", Redirect, "challenge", "editor@example.com", Resource, now.AddMinutes(1)), CancellationToken.None);
        var taken = await Task.WhenAll(store.TakeCodeAsync("abc123", CancellationToken.None), store.TakeCodeAsync("abc123", CancellationToken.None));
        await Assert.That(taken.Count(c => c is not null)).IsEqualTo(1);
        await Assert.That(taken.First(c => c is not null)!.Email).IsEqualTo("editor@example.com");

        await store.AddGrantAsync(new OAuthGrant("grant1", "editor@example.com", "client1", "App", Resource, now, now, "h1", null, now.AddDays(30)), CancellationToken.None);
        var grant = await store.FindGrantAsync("grant1", CancellationToken.None);
        await Assert.That(await store.ReplaceGrantAsync(grant! with { RefreshHash = "h2", PreviousRefreshHash = "h1" }, CancellationToken.None)).IsTrue();
        await Assert.That(await store.ReplaceGrantAsync(grant! with { RefreshHash = "h3" }, CancellationToken.None)).IsFalse();
        await Assert.That((await store.FindGrantAsync("grant1", CancellationToken.None))!.RefreshHash).IsEqualTo("h2");
        await Assert.That(await store.DeleteGrantAsync("grant1", CancellationToken.None)).IsTrue();
        await Assert.That(await store.DeleteGrantAsync("grant1", CancellationToken.None)).IsFalse();
        await Assert.That(await store.FindGrantAsync("bad/key", CancellationToken.None)).IsNull();
    }

    private static string Verifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Uri AuthorizeUrl(string clientId, string redirect, string? challenge, string method = "S256", string? resource = Resource)
    {
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["state"] = "xyz",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = method,
            ["resource"] = resource,
        };
        return new Uri(QueryHelpers.AddQueryString("/oauth/authorize", query.Where(p => p.Value is not null)), UriKind.Relative);
    }

    private static Dictionary<string, Microsoft.Extensions.Primitives.StringValues> Reply(HttpResponseMessage response) =>
        QueryHelpers.ParseQuery(new Uri(response.Headers.Location!.OriginalString).Query);

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    /// <summary>A test site with the editor signed in, a clock to move, and metadata documents to serve.</summary>
    internal sealed class OAuthSite : IAsyncDisposable
    {
        private readonly SiteFactory _site;
        private readonly WebApplicationFactory<Program> _factory;

        public OAuthSite(IReadOnlyDictionary<string, string>? documents = null, EditorRole role = EditorRole.Admin, string? contentPath = null)
        {
            _site = new SiteFactory(contentPath);
            _factory = _site.WithTestSignIn().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddHttpClient(ClientRegistry.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new Documents(documents ?? new Dictionary<string, string>()));
            }));
            Services.GetRequiredService<IEditorStore>()
                .SetAsync(new Editor("editor@example.com", role, "test", DateTimeOffset.UtcNow), CancellationToken.None)
                .GetAwaiter().GetResult();
        }

        public TestClock Clock { get; } = new();

        public IServiceProvider Services => _factory.Services;

        public OAuthTokens Tokens => Services.GetRequiredService<OAuthTokens>();

        public HttpClient Client(EditorRole? role = EditorRole.Admin)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            if (role is { } r)
            {
                client.DefaultRequestHeaders.Add("X-Test-Role", r.ToString());
            }

            return client;
        }

        public async Task<string> RegisterAsync(string redirect, string name = "Test app")
        {
            using var client = Client(role: null);
            using var response = await client.PostAsJsonAsync(new Uri("/oauth/register", UriKind.Relative), new { client_name = name, redirect_uris = new[] { redirect } });
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
        }

        public static async Task<Dictionary<string, Microsoft.Extensions.Primitives.StringValues>> ConsentAsync(HttpClient client, string clientId, string redirect, string challenge, string decision = "allow", bool publish = false)
        {
            var url = AuthorizeUrl(clientId, redirect, challenge);
            using var page = await client.GetAsync(url);
            var html = await page.Content.ReadAsStringAsync();
            var fields = QueryHelpers.ParseQuery(url.OriginalString[url.OriginalString.IndexOf('?', StringComparison.Ordinal)..])
                .Select(p => new KeyValuePair<string, string>(p.Key, p.Value.ToString()))
                .Append(new("__RequestVerificationToken", Token().Match(html).Groups[1].Value))
                .Append(new("decision", decision))
                .Append(new("publish", publish ? "yes" : "no"));
            using var form = new FormUrlEncodedContent(fields);
            using var response = await client.PostAsync(new Uri("/oauth/authorize/decision", UriKind.Relative), form);
            return response.StatusCode == HttpStatusCode.Redirect
                ? Reply(response)
                : throw new InvalidOperationException($"The decision answered {response.StatusCode}.");
        }

        public static async Task<JsonElement> GrantAsync(HttpClient client, string clientId, bool publish = false)
        {
            var verifier = Verifier();
            var code = (await ConsentAsync(client, clientId, Redirect, Challenge(verifier), publish: publish))["code"].ToString();
            return await TokenAsync(client, ("grant_type", "authorization_code"), ("code", code), ("client_id", clientId), ("redirect_uri", Redirect), ("code_verifier", verifier));
        }

        public static async Task<JsonElement> TokenAsync(HttpClient client, params (string Name, string Value)[] fields)
        {
            using var response = await PostTokenAsync(client, fields);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return response.StatusCode == HttpStatusCode.OK
                ? json
                : throw new InvalidOperationException($"The token endpoint answered {json}.");
        }

        public static async Task<string?> TokenErrorAsync(HttpClient client, params (string Name, string Value)[] fields)
        {
            using var response = await PostTokenAsync(client, fields);
            return response.StatusCode == HttpStatusCode.BadRequest
                ? (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
                : null;
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            await _site.DisposeAsync();
        }

        private static async Task<HttpResponseMessage> PostTokenAsync(HttpClient client, (string Name, string Value)[] fields)
        {
            using var form = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
            return await client.PostAsync(new Uri("/oauth/token", UriKind.Relative), form);
        }
    }

    /// <summary>A clock tests move forward.</summary>
    internal sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Serves metadata documents by URL, 404 otherwise.</summary>
    private sealed class Documents(IReadOnlyDictionary<string, string> documents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(documents.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
