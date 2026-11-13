using System.Net;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Azurite;
using Weesite.Web.Admin;
using Weesite.Web.Email;

namespace Weesite.Web.Tests;

/// <summary>The allowlist: the change rules, the editors and audit pages, and the Table Storage stores.</summary>
internal sealed partial class EditorTests
{
    private static ConfiguredAdmins Admins(string value) =>
        new(new ConfigurationBuilder().AddInMemoryCollection([new("Site:Admins", value)]).Build());

    private static System.Security.Claims.ClaimsPrincipal As(string email, EditorRole role = EditorRole.Admin) =>
        EditorClaims.Principal("Test", "x", email, null, role);

    [Test]
    [Arguments("name@example.com", true)]
    [Arguments(" name@example.com ", true)]
    [Arguments("Name <name@example.com>", false)]
    [Arguments("name@localhost", false)]
    [Arguments("not an address", false)]
    [Arguments("", false)]
    public async Task Only_plain_addresses_are_accepted(string email, bool ok) =>
        await Assert.That(EditorChanges.IsAddress(email)).IsEqualTo(ok);

    [Test]
    public async Task Changes_are_saved_audited_and_refused_for_self_and_configured_admins()
    {
        var store = new MemoryEditorStore();
        var audit = new MemoryAuditLog();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var tokens = new Weesite.Web.Agents.OAuth.OAuthTokens(
            new Weesite.Web.Agents.OAuth.MemoryOAuthStore(TimeProvider.System),
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(),
            Admins("boss@example.com"),
            audit,
            cache,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Weesite.Web.Agents.OAuth.OAuthTokens>.Instance);
        var changes = new EditorChanges(Admins("boss@example.com"), store, audit, cache, tokens, TimeProvider.System);
        var me = As("me@example.com");

        var added = await changes.SetAsync(me, "new@example.com", "Author", CancellationToken.None);
        var changed = await changes.SetAsync(me, "new@example.com", "Admin", CancellationToken.None);
        await Assert.That(added).IsEqualTo(new EditorChange("saved", added.Saved, RoleChanged: false));
        await Assert.That(changed.Status).IsEqualTo("saved");
        await Assert.That(changed.RoleChanged).IsTrue();
        await Assert.That(changed.Saved?.Role).IsEqualTo(EditorRole.Admin);
        await Assert.That((await changes.SetAsync(me, "me@example.com", "Author", CancellationToken.None)).Status).IsEqualTo("self");
        await Assert.That((await changes.SetAsync(me, "BOSS@example.com", "Author", CancellationToken.None)).Status).IsEqualTo("configured");
        await Assert.That((await changes.SetAsync(me, "x@example.com", "Owner", CancellationToken.None)).Status).IsEqualTo("invalid");
        await Assert.That((await changes.SetAsync(me, "x@example.com", "7", CancellationToken.None)).Status).IsEqualTo("invalid");
        await Assert.That((await store.FindAsync("NEW@example.com", CancellationToken.None))?.Role).IsEqualTo(EditorRole.Admin);

        await Assert.That(await changes.RemoveAsync(me, "new@example.com", CancellationToken.None)).IsEqualTo("removed");
        await Assert.That(await changes.RemoveAsync(me, "new@example.com", CancellationToken.None)).IsEqualTo("missing");
        await Assert.That((await audit.LatestAsync(10, CancellationToken.None)).Select(e => e.Action))
            .IsEquivalentTo(["editor-removed", "editor-role-changed", "editor-added"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Configured_admins_come_first_then_stored_editors()
    {
        var store = new MemoryEditorStore();
        await store.SetAsync(new Editor("author@example.com", EditorRole.Author, "x", DateTimeOffset.UnixEpoch), CancellationToken.None);
        await store.SetAsync(new Editor("boss@example.com", EditorRole.Author, "x", DateTimeOffset.UnixEpoch), CancellationToken.None);
        var directory = new EditorDirectory(Admins("boss@example.com"), store);

        await Assert.That(await directory.FindRoleAsync("boss@example.com", CancellationToken.None)).IsEqualTo(EditorRole.Admin);
        await Assert.That(await directory.FindRoleAsync("author@example.com", CancellationToken.None)).IsEqualTo(EditorRole.Author);
        await Assert.That(await directory.FindRoleAsync("nobody@example.com", CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Authors_cannot_open_the_editors_page()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = TestAuth.As("/admin/editors", EditorRole.Author);

        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.AbsolutePath).IsEqualTo("/admin/forbidden");
    }

    [Test]
    public async Task Admins_add_and_remove_editors_and_see_it_in_the_audit_log()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn().WithWebHostBuilder(b => b.UseSetting("Site:Admins", "boss@example.com"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        var page = await client.GetStringAsync(new Uri("/admin/editors", UriKind.Relative));
        using var add = await PostAsync(client, "/admin/editors", page, ("email", "writer@example.com"), ("role", "Author"));
        var after = await client.GetStringAsync(new Uri("/admin/editors?status=saved", UriKind.Relative));
        using var again = await PostAsync(client, "/admin/editors", after, ("email", "Writer@example.com"), ("role", "Author"));
        var unchanged = await client.GetStringAsync(again.Headers.Location!);
        var invalid = await client.GetStringAsync(new Uri("/admin/editors?status=invalid", UriKind.Relative));
        using var remove = await PostAsync(client, "/admin/editors/remove", after, ("email", "writer@example.com"));
        var audit = await client.GetStringAsync(new Uri("/admin/audit", UriKind.Relative));

        await Assert.That(page).Contains("<li>boss@example.com</li>");
        await Assert.That(add.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=saved");
        await Assert.That(after).Contains("<th scope=\"row\" class=\"email\">writer@example.com</th>");
        await Assert.That(after).Contains("Saved.");
        await Assert.That(remove.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=removed");
        await Assert.That(audit).Contains("<td><span class=\"pill\">editor-added</span></td>");
        await Assert.That(audit).Contains("<td><span class=\"pill\">editor-removed</span></td>");

        // F-0091 L12: the same role again changes and logs nothing; errors are alerts.
        await Assert.That(again.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=unchanged");
        await Assert.That(unchanged).Contains("<p class=\"notice\" role=\"status\">That address already has that role; nothing changed.</p>");
        await Assert.That(audit).DoesNotContain("editor-role-changed");
        await Assert.That(invalid).Contains("<p class=\"notice\" role=\"alert\">");

        // F-0091 L13: removing asks first (with scripts).
        await Assert.That(after).Contains("data-confirm=\"Remove writer@example.com? They");

        // ADR 0010: without email, the page says so, and the invitation is there to copy.
        await Assert.That(WebUtility.HtmlDecode(page)).Contains("Email isn't set up, so no invitation is sent");
        await Assert.That(after).Contains("<textarea id=\"invitation-text-0\" readonly rows=\"10\">editor@example.com has added you to Example Site as an author.");
        await Assert.That(after).Contains("Sign in at http://localhost/admin with the Google or Microsoft account of this address, writer@example.com.");
    }

    [Test]
    public async Task Adding_an_editor_or_changing_their_role_sends_an_invitation()
    {
        var sender = new RecordingSender();
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn().WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(sender)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        var page = await client.GetStringAsync(new Uri("/admin/editors", UriKind.Relative));
        using var add = await PostAsync(client, "/admin/editors", page, ("email", "writer@example.com"), ("role", "Author"));
        using var change = await PostAsync(client, "/admin/editors", page, ("email", "writer@example.com"), ("role", "Admin"));
        using var same = await PostAsync(client, "/admin/editors", page, ("email", "writer@example.com"), ("role", "Admin"));
        var after = await client.GetStringAsync(add.Headers.Location!);
        var audit = await client.GetStringAsync(new Uri("/admin/audit", UriKind.Relative));

        await Assert.That(page).Contains("They get an invitation by email with the sign-in link.");
        await Assert.That(add.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=saved&invite=sent");
        await Assert.That(change.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=saved&invite=sent");
        await Assert.That(same.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=unchanged");
        await Assert.That(after).Contains("<p class=\"notice\" role=\"status\">Saved. The invitation was sent by email.</p>");
        await Assert.That(sender.Sent.Select(m => m.Subject)).IsEquivalentTo(["You can now edit Example Site", "Your role on Example Site has changed"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(sender.Sent.All(m => m.To == "writer@example.com" && m.FromName == "Example Site")).IsTrue();
        await Assert.That(sender.Sent[0].Text).Contains("http://localhost/admin");
        await Assert.That(audit).Contains("<td><span class=\"pill\">editor-invited</span></td>");
    }

    [Test]
    public async Task An_invitation_that_cant_be_sent_still_adds_the_editor_and_says_so()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn().WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(new RecordingSender(fail: true))));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));

        var page = await client.GetStringAsync(new Uri("/admin/editors", UriKind.Relative));
        using var add = await PostAsync(client, "/admin/editors", page, ("email", "writer@example.com"), ("role", "Author"));
        var after = await client.GetStringAsync(add.Headers.Location!);
        var audit = await client.GetStringAsync(new Uri("/admin/audit", UriKind.Relative));

        await Assert.That(add.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=saved&invite=failed");
        await Assert.That(after).Contains("<th scope=\"row\" class=\"email\">writer@example.com</th>");
        await Assert.That(WebUtility.HtmlDecode(after)).Contains("<p class=\"notice\" role=\"alert\">Saved. The invitation couldn't be sent by email: copy it with the Invitation button in their row and send it yourself.</p>");
        await Assert.That(audit).Contains("<td><span class=\"pill\">editor-invite-failed</span></td>");
        await Assert.That(audit).Contains("IOException: The server refused the connection.");
    }

    [Test]
    public async Task Editor_posts_need_the_antiforgery_token()
    {
        await using var site = new SiteFactory();
        await using var factory = site.WithTestSignIn();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", nameof(EditorRole.Admin));
        using var form = new FormUrlEncodedContent([new("email", "writer@example.com"), new("role", "Author")]);

        using var response = await client.PostAsync(new Uri("/admin/editors", UriKind.Relative), form);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    [Category("containers")]
    public async Task Table_stores_keep_editors_and_audit_entries()
    {
        await using var azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.37.0").Build();
        await azurite.StartAsync();
        var tables = new TableServiceClient(azurite.GetConnectionString());
        var store = new TableEditorStore(tables);
        var audit = new TableAuditLog(tables, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<TableAuditLog>.Instance);

        await store.SetAsync(new Editor("Writer#1@example.com", EditorRole.Author, "boss@example.com", DateTimeOffset.UtcNow), CancellationToken.None);
        await store.SetAsync(new Editor("writer#1@EXAMPLE.com", EditorRole.Admin, "boss@example.com", DateTimeOffset.UtcNow), CancellationToken.None);
        await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow.AddMinutes(-1), "boss@example.com", "first"), CancellationToken.None);
        await audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, "boss@example.com", "second"), CancellationToken.None);

        var editors = await store.ListAsync(CancellationToken.None);
        await Assert.That(editors.Count).IsEqualTo(1);
        await Assert.That(editors[0].Role).IsEqualTo(EditorRole.Admin);
        await Assert.That((await audit.LatestAsync(10, CancellationToken.None)).Select(e => e.Action))
            .IsEquivalentTo(["second", "first"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(await store.RemoveAsync("WRITER#1@example.com", CancellationToken.None)).IsTrue();
        await Assert.That(await store.RemoveAsync("writer#1@example.com", CancellationToken.None)).IsFalse();
    }

    // Posts a form the way the page does: with the antiforgery token from the page's own form.
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string page, params (string Name, string Value)[] fields)
    {
        var token = Forms.Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new("__RequestVerificationToken", token)]);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    // Records what it sends, or fails like a server that can't be reached.
    private sealed class RecordingSender(bool fail = false) : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public bool IsConfigured => true;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new IOException("The server refused the connection.");
            }

            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
