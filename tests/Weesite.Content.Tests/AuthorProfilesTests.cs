using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Weesite.Content.Tests;

/// <summary>Editor profiles in settings/authors.json (ADR 0005).</summary>
internal sealed class AuthorProfilesTests
{
    [Test]
    public async Task Saved_profiles_join_the_authors_and_each_editor_keeps_their_own()
    {
        var root = Directory.CreateTempSubdirectory("weesite-profiles-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "settings"));
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "settings", "site.json"),
                """{ "name": "S", "authors": [ { "id": "editor-aaa", "name": "From site.json" } ] }""");
            using var app = Services(root.FullName);
            var profiles = app.GetRequiredService<AuthorProfiles>();
            var content = app.GetRequiredService<ContentCatalogProvider>();
            await app.GetRequiredService<ContentLoader>().LoadAsync(CancellationToken.None);

            // Saves at once: each reads, writes if nobody else did meanwhile, and retries otherwise.
            await Task.WhenAll(
                profiles.SaveAsync(new Author { Id = "editor-bbb", Name = "Bea" }, CancellationToken.None),
                profiles.SaveAsync(new Author { Id = "editor-ccc", Name = "Cy" }, CancellationToken.None),
                profiles.SaveAsync(new Author { Id = "editor-aaa", Name = "Ignored" }, CancellationToken.None));
            await profiles.SaveAsync(new Author { Id = "editor-bbb", Name = "Bea B.", Bio = "Hello." }, CancellationToken.None);
            await profiles.SaveAsync(new Author { Id = "editor-ddd", Name = "Gergő Ő." }, CancellationToken.None);

            var site = content.Current.Site;
            await Assert.That(site.FindAuthor("editor-bbb")?.Name).IsEqualTo("Bea B.");
            await Assert.That(site.FindAuthor("editor-bbb")?.Bio).IsEqualTo("Hello.");
            await Assert.That(site.FindAuthor("editor-ccc")?.Name).IsEqualTo("Cy");
            await Assert.That(site.FindAuthor("editor-aaa")?.Name).IsEqualTo("From site.json");
            await Assert.That((await profiles.FindAsync("editor-ccc", CancellationToken.None))?.Name).IsEqualTo("Cy");

            // Written as typed, as site.json is (F-0120).
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(root.FullName, "settings", "authors.json"))).Contains("\"Gergő Ő.\"");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("""{ "id": "editor-x" }""")]
    [Arguments("""[ { "name": "No ID" } ]""")]
    public async Task A_missing_or_malformed_file_gives_no_profiles(string? json) =>
        await Assert.That(AuthorProfiles.Parse(json)).IsEmpty();

    private static ServiceProvider Services(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddContentFromFiles(root);
        return services.BuildServiceProvider();
    }
}
