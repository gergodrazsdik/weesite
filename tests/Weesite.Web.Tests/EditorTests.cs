using System.Net;
using System.Text.RegularExpressions;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Testcontainers.Azurite;
using Weesite.Web.Admin;

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
        var changes = new EditorChanges(Admins("boss@example.com"), store, audit, cache, TimeProvider.System);
        var me = As("me@example.com");

        await Assert.That(await changes.SetAsync(me, "new@example.com", "Author", CancellationToken.None)).IsEqualTo("saved");
        await Assert.That(await changes.SetAsync(me, "new@example.com", "Admin", CancellationToken.None)).IsEqualTo("saved");
        await Assert.That(await changes.SetAsync(me, "me@example.com", "Author", CancellationToken.None)).IsEqualTo("self");
        await Assert.That(await changes.SetAsync(me, "BOSS@example.com", "Author", CancellationToken.None)).IsEqualTo("configured");
        await Assert.That(await changes.SetAsync(me, "x@example.com", "Owner", CancellationToken.None)).IsEqualTo("invalid");
        await Assert.That(await changes.SetAsync(me, "x@example.com", "7", CancellationToken.None)).IsEqualTo("invalid");
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
        await Assert.That(response.Headers.Location?.AbsolutePath).IsEqualTo("/admin/denied");
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
        using var remove = await PostAsync(client, "/admin/editors/remove", after, ("email", "writer@example.com"));
        var audit = await client.GetStringAsync(new Uri("/admin/audit", UriKind.Relative));

        await Assert.That(page).Contains("<li>boss@example.com</li>");
        await Assert.That(add.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=saved");
        await Assert.That(after).Contains("<th scope=\"row\">writer@example.com</th>");
        await Assert.That(after).Contains("Saved.");
        await Assert.That(remove.Headers.Location?.ToString()).IsEqualTo("/admin/editors?status=removed");
        await Assert.That(audit).Contains("<td>editor-added</td>");
        await Assert.That(audit).Contains("<td>editor-removed</td>");
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
        var audit = new TableAuditLog(tables, TimeProvider.System);

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
        var token = Token().Match(page).Groups[1].Value;
        using var form = new FormUrlEncodedContent([.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), new("__RequestVerificationToken", token)]);
        return await client.PostAsync(new Uri(path, UriKind.Relative), form);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();
}
