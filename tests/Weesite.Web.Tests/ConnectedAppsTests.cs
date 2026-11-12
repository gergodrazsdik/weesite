using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;

namespace Weesite.Web.Tests;

/// <summary>Connected apps on Your profile and Editors (ADR 0007, T-0082).</summary>
internal sealed partial class ConnectedAppsTests
{
    [Test]
    public async Task Your_profile_lists_your_apps_and_revokes_one()
    {
        await using var site = new OAuthTests.OAuthSite();
        using var browser = site.Client();
        var clientId = await site.RegisterAsync("http://127.0.0.1:5555/callback", "Claude Code");
        var tokens = await OAuthTests.OAuthSite.GrantAsync(browser, clientId);

        var profile = await browser.GetStringAsync(new Uri("/admin/profile", UriKind.Relative));
        await Assert.That(profile).Contains("<th scope=\"row\">Claude Code</th>");
        await Assert.That(profile).Contains("Drafts only");

        using var revoked = await PostRevokeAsync(browser, profile, GrantId(profile));
        await Assert.That(revoked.Headers.Location!.OriginalString).IsEqualTo("/admin/profile?apps=revoked#connected-apps");
        await Assert.That(await site.Tokens.ValidateAsync(tokens.GetProperty("access_token").GetString(), "http://localhost/mcp/editor", CancellationToken.None)).IsNull();
        var after = await browser.GetStringAsync(new Uri("/admin/profile?apps=revoked", UriKind.Relative));
        await Assert.That(after).Contains("No apps are connected.");
        await Assert.That(after).Contains("The app&#x27;s access is revoked.");
    }

    [Test]
    public async Task An_author_cannot_revoke_someone_else_s_app()
    {
        await using var site = new OAuthTests.OAuthSite(role: EditorRole.Author);
        var store = site.Services.GetRequiredService<IOAuthStore>();
        var now = DateTimeOffset.UtcNow;
        await store.AddGrantAsync(new OAuthGrant("theirs123", "boss@example.com", "c", "Their app", "http://localhost/mcp/editor", now, now, "h", null, now.AddDays(30)), CancellationToken.None);
        using var author = site.Client(EditorRole.Author);

        var profile = await author.GetStringAsync(new Uri("/admin/profile", UriKind.Relative));
        using var refused = await PostRevokeAsync(author, profile, "theirs123");

        await Assert.That(profile).DoesNotContain("Their app");
        await Assert.That(refused.Headers.Location!.OriginalString).IsEqualTo("/admin/profile?apps=missing#connected-apps");
        await Assert.That(await store.FindGrantAsync("theirs123", CancellationToken.None)).IsNotNull();
    }

    [Test]
    public async Task Editors_lists_everyone_s_apps_and_removing_an_editor_revokes_theirs()
    {
        await using var site = new OAuthTests.OAuthSite();
        var store = site.Services.GetRequiredService<IOAuthStore>();
        await site.Services.GetRequiredService<IEditorStore>().SetAsync(new Editor("writer@example.com", EditorRole.Author, "test", DateTimeOffset.UtcNow), CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await store.AddGrantAsync(new OAuthGrant("writer123", "writer@example.com", "c", "Writer's app", "http://localhost/mcp/editor", now, now, "h", null, now.AddDays(30), CanPublish: true), CancellationToken.None);
        using var admin = site.Client();

        var editors = await admin.GetStringAsync(new Uri("/admin/editors", UriKind.Relative));
        await Assert.That(editors).Contains("Writer&#x27;s app");
        await Assert.That(editors).Contains("<td class=\"email\">writer@example.com</td>");
        await Assert.That(editors).Contains("Drafts and publishing");

        using var form = new FormUrlEncodedContent([new("__RequestVerificationToken", Token().Match(editors).Groups[1].Value), new("email", "writer@example.com")]);
        using var removed = await admin.PostAsync(new Uri("/admin/editors/remove", UriKind.Relative), form);

        await Assert.That(removed.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(await store.FindGrantAsync("writer123", CancellationToken.None)).IsNull();
        var audit = await site.Services.GetRequiredService<IAuditLog>().LatestAsync(5, CancellationToken.None);
        await Assert.That(audit.Any(e => e.Action == "oauth-revoke" && e.Actor == "editor@example.com" && e.Target == "Writer's app (writer@example.com)")).IsTrue();
    }

    private static async Task<HttpResponseMessage> PostRevokeAsync(HttpClient client, string page, string grant)
    {
        using var form = new FormUrlEncodedContent([
            new("__RequestVerificationToken", Token().Match(page).Groups[1].Value),
            new("grant", grant),
            new("returnTo", "profile"),
        ]);
        return await client.PostAsync(new Uri("/admin/connected-apps/revoke", UriKind.Relative), form);
    }

    private static string GrantId(string page) => GrantField().Match(page).Groups[1].Value;

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [GeneratedRegex("name=\"grant\" value=\"([^\"]+)\"")]
    private static partial Regex GrantField();
}
