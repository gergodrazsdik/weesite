using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Weesite.Content.Sources;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>The page of every earlier version (T-0108), and the editor's unsaved marker (T-0109).</summary>
internal sealed class VersionsTests
{
    private const string Post = "/admin/edit?path=posts%2Fhello-world.md";

    [Test]
    public async Task The_editor_shows_five_versions_and_links_to_a_page_with_all_of_them()
    {
        await using var site = new SiteFactory();
        await using var factory = WithVersions(site, 7);
        using var admin = Client(factory, EditorRole.Admin);

        var editor = await admin.GetStringAsync(new Uri(Post, UriKind.Relative));
        var page = await admin.GetStringAsync(new Uri("/admin/versions?path=posts%2Fhello-world.md", UriKind.Relative));

        await Assert.That(editor).Contains("<a class=\"all-versions\" href=\"/admin/versions?path=posts%2Fhello-world.md\">All 7 versions</a>");
        await Assert.That(editor).DoesNotContain("<details");
        await Assert.That(page).Contains("<h1>Versions of Hello, world</h1>");
        await Assert.That(page).Contains("7 versions, newest first");
        await Assert.That(Count(page, "aria-label=\"Open the version of ")).IsEqualTo(6);
        await Assert.That(page).Contains("2026-01-07 12:00 UTC");
        await Assert.That(page).Contains("2026-01-01 12:00 UTC");
    }

    [Test]
    [Arguments("/admin/versions?path=posts%2Fno-such-post.md", EditorRole.Admin, HttpStatusCode.NotFound)]
    [Arguments("/admin/versions", EditorRole.Admin, HttpStatusCode.NotFound)]
    public async Task The_versions_page_needs_an_item(string path, EditorRole role, HttpStatusCode status)
    {
        await using var site = new SiteFactory();
        await using var factory = WithVersions(site, 2);
        using var client = Client(factory, role);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(status);
    }

    [Test]
    public async Task An_author_cant_list_the_versions_of_someone_elses_post()
    {
        await using var site = new SiteFactory();
        await using var factory = WithVersions(site, 2);
        using var author = Client(factory, EditorRole.Author);

        using var response = await author.GetAsync(new Uri("/admin/versions?path=posts%2Fhello-world.md", UriKind.Relative));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location?.AbsolutePath).IsEqualTo("/admin/denied");
    }

    [Test]
    public async Task An_earlier_version_loaded_to_restore_is_marked_unsaved()
    {
        await using var site = new SiteFactory();
        await using var factory = WithVersions(site, 2);
        using var admin = Client(factory, EditorRole.Admin);

        var opened = await admin.GetStringAsync(new Uri(Post, UriKind.Relative));
        var restoring = await admin.GetStringAsync(new Uri(Post + "&version=v1", UriKind.Relative));

        await Assert.That(opened).DoesNotContain("data-unsaved");
        await Assert.That(restoring).Contains("data-unsaved=\"true\"");
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    private static HttpClient Client(WebApplicationFactory<Program> factory, EditorRole role)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-Role", role.ToString());
        return client;
    }

    // The file store keeps no versions; this one reports some for every post, the newest current.
    private static WebApplicationFactory<Program> WithVersions(SiteFactory site, int count) =>
        site.WithTestSignIn().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var existing = services.Last(d => d.ServiceType == typeof(IContentStore));
            services.Remove(existing);
            services.AddSingleton<IContentStore>(provider =>
                new Versioned((IContentStore)(existing.ImplementationInstance ?? existing.ImplementationFactory!(provider)), count));
        }));

    private sealed class Versioned(IContentStore inner, int count) : IContentStore
    {
        public Task<StoredFile?> ReadAsync(string path, CancellationToken cancellationToken) => inner.ReadAsync(path, cancellationToken);

        public Task<string?> WriteAsync(string path, string text, string? ifMatch, CancellationToken cancellationToken) =>
            inner.WriteAsync(path, text, ifMatch, cancellationToken);

        public Task<string?> BumpVersionAsync(CancellationToken cancellationToken) => inner.BumpVersionAsync(cancellationToken);

        public Task<IReadOnlyList<Revision>> ListRevisionsAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Revision>>([.. Enumerable.Range(1, count).Reverse()
                .Select(i => new Revision($"v{i}", new DateTimeOffset(2026, 1, i, 12, 0, 0, TimeSpan.Zero), i == count))]);

        public async Task<string?> ReadRevisionAsync(string path, string versionId, CancellationToken cancellationToken) =>
            (await inner.ReadAsync(path, cancellationToken))?.Text;

        public Task<bool> DeleteAsync(string path, string ifMatch, CancellationToken cancellationToken) => inner.DeleteAsync(path, ifMatch, cancellationToken);

        public Task<StoredFile?> ReadSettingsFileAsync(string name, CancellationToken cancellationToken) => inner.ReadSettingsFileAsync(name, cancellationToken);

        public Task<string?> WriteSettingsFileAsync(string name, string text, string? ifMatch, CancellationToken cancellationToken) =>
            inner.WriteSettingsFileAsync(name, text, ifMatch, cancellationToken);
    }
}
