using Weesite.Content;
using Weesite.Web.Admin;

namespace Weesite.Web.Tests;

/// <summary>An item in another language than the site's (ADR 0008, T-0060, F-0072).</summary>
internal sealed class ItemLanguageTests
{
    private const string Hungarian = """
        ---
        title: Egy magyar bejegyzés
        slug: egy-magyar-bejegyzes
        date: 2026-11-01
        lang: hu
        summary: Rövid összefoglaló.
        translation: left-from-the-old-plan
        ---

        Ez a bejegyzés magyarul íródott.
        """;

    [Test]
    public async Task The_editor_keeps_an_items_language_and_defaults_to_the_sites()
    {
        var draft = new ContentDraft { Kind = ContentKind.Post, Title = "Egy", Slug = "egy", Language = "hu", Date = new DateOnly(2026, 11, 1) };

        var kept = EditorForm.From(draft, "posts/egy.md", "etag").ToDraft("en", out var keptError);
        var emptyForm = EditorForm.From(draft, "posts/egy.md", "etag");
        emptyForm.Language = " ";
        var empty = emptyForm.ToDraft("en", out _);
        var wrongForm = EditorForm.From(draft, "posts/egy.md", "etag");
        wrongForm.Language = "not a language";
        var wrong = wrongForm.ToDraft("en", out var wrongError);

        await Assert.That(keptError).IsNull();
        await Assert.That(kept!.Language).IsEqualTo("hu");
        await Assert.That(empty!.Language).IsEqualTo("en");
        await Assert.That(wrong).IsNull();
        await Assert.That(wrongError).IsEqualTo("The language must be a code such as en, en-GB or hu.");
    }

    [Test]
    public async Task A_file_with_the_old_translation_field_loads_and_saves_without_it()
    {
        var parsed = ContentParser.Parse("posts/egy-magyar-bejegyzes.md", Hungarian);

        await Assert.That(parsed.Item).IsNotNull();
        await Assert.That(parsed.Item!.Language).IsEqualTo("hu");
        await Assert.That(ContentDraft.From(parsed.Item).ToMarkdown()).DoesNotContain("translation");
    }

    [Test]
    public async Task A_hungarian_post_on_an_english_site_is_marked_hungarian()
    {
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "egy-magyar-bejegyzes.md"), Hungarian);
        await using var site = new SiteFactory(copy.Root);
        using var client = site.CreateClient();

        var post = await client.GetStringAsync(new Uri("/blog/egy-magyar-bejegyzes", UriKind.Relative));
        var english = await client.GetStringAsync(new Uri("/blog/hello-world", UriKind.Relative));
        var blog = await client.GetStringAsync(new Uri("/blog", UriKind.Relative));
        var feed = await client.GetStringAsync(new Uri("/feed.xml", UriKind.Relative));

        await Assert.That(post).Contains("<html lang=\"en\"");
        await Assert.That(post).Contains("<h1 id=\"post-title\" class=\"page-title\" lang=\"hu\">Egy magyar bejegyzés</h1>");
        await Assert.That(post).Contains("<div class=\"content lead\" lang=\"hu\"");
        await Assert.That(post).Contains("<meta property=\"og:locale\" content=\"hu_HU\" />");
        await Assert.That(english).Contains("<h1 id=\"post-title\" class=\"page-title\">Hello, world</h1>");
        await Assert.That(english).Contains("<meta property=\"og:locale\" content=\"en_US\" />");
        await Assert.That(blog).Contains("<h3 lang=\"hu\"><a href=\"/blog/egy-magyar-bejegyzes\">Egy magyar bejegyzés</a></h3>");
        await Assert.That(blog).Contains("<p lang=\"hu\">Rövid összefoglaló.</p>");
        await Assert.That(feed).Contains("<entry xml:lang=\"hu\">");
    }

    [Test]
    public async Task The_editor_shows_the_items_language()
    {
        using var copy = new SiteCopy();
        await File.WriteAllTextAsync(Path.Combine(copy.Root, "content", "posts", "egy-magyar-bejegyzes.md"), Hungarian);
        await using var site = new SiteFactory(copy.Root);
        await using var factory = site.WithTestSignIn();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        var page = await admin.GetStringAsync(new Uri("/admin/edit?path=posts%2Fegy-magyar-bejegyzes.md", UriKind.Relative));

        await Assert.That(page).Contains("id=\"language\"");
        await Assert.That(page).Contains("value=\"hu\"");
        await Assert.That(page).Contains("<option value=\"hu\"></option>");
    }
}
