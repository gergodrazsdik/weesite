using Weesite.Content;

namespace Weesite.Search.Tests;

internal sealed class SearchIndexTests
{
    private static ContentItem Post(string slug, string title, string markdown, string date = "2026-01-01") =>
        new(ContentKind.Post, slug, title, "en", markdown, $"posts/{slug}.md") { Date = DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture) };

    private static ContentItem Page(string slug, string title, string markdown) =>
        new(ContentKind.Page, slug, title, "en", markdown, $"pages/{slug}.md");

    private static SearchIndex Index(params ContentItem[] items) =>
        SearchIndex.Build(ContentCatalog.Build(SiteSettings.Empty, items.Select(i => new ParseResult(i, null))));

    [Test]
    public async Task Chunker_splits_at_headings_but_not_inside_code()
    {
        var item = Post("a", "A", "Intro text.\n\n## First\n\nOne.\n\n```md\n## not a heading\n```\n\n### Second ###\n\nTwo *words*.\n");

        var chunks = Chunker.Split(item);

        await Assert.That(chunks.Select(c => c.Heading)).IsEquivalentTo([null, "First", "Second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(chunks[0].Text).IsEqualTo("Intro text.");
        await Assert.That(chunks[1].Text).IsEqualTo("One.");
        await Assert.That(chunks[1].Code).IsEqualTo("## not a heading");
        await Assert.That(chunks[2].Text).IsEqualTo("Two words.");
        await Assert.That(chunks.Select(c => c.Index)).IsEquivalentTo([0, 1, 2], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Finds_stemmed_words_and_points_at_the_section()
    {
        using var index = Index(
            Post("static", "Static rendering", "Intro.\n\n## Caching\n\nPages are cached at the edge for speed."),
            Post("other", "Something else", "Nothing relevant here."));

        var hits = index.Search("cache");

        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].Item.Slug).IsEqualTo("static");
        await Assert.That(hits[0].Heading).IsEqualTo("Caching");
        await Assert.That(hits[0].SnippetHtml).Contains("<mark>cached</mark>");
        await Assert.That(hits[0].Rank).IsEqualTo(1);
    }

    [Test]
    public async Task Title_matches_rank_above_body_matches_and_each_item_appears_once()
    {
        using var index = Index(
            Post("body", "Notes", "Azure is mentioned here.\n\n## More\n\nAzure again, and Azure once more."),
            Post("title", "Azure tips", "A short post."));

        var hits = index.Search("azure");

        await Assert.That(hits.Select(h => h.Item.Slug)).IsEquivalentTo(["title", "body"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task All_words_are_required_and_the_last_one_is_a_prefix()
    {
        using var index = Index(
            Post("both", "Both", "OpenTofu policy checks."),
            Post("one", "One", "OpenTofu only."));

        await Assert.That(index.Search("opentofu pol").Select(h => h.Item.Slug)).IsEquivalentTo(["both"]);
        await Assert.That(index.Search("opentofu").Count).IsEqualTo(2);
    }

    [Test]
    public async Task Diacritics_are_ignored_and_pages_are_found_too()
    {
        using var index = Index(Page("about", "About", "Árvíztűrő tükörfúrógép."));

        var hits = index.Search("arvizturo");

        await Assert.That(hits.Single().Item.Kind).IsEqualTo(ContentKind.Page);
    }

    [Test]
    [Arguments("\"unbalanced")]
    [Arguments("a OR b NOT c")]
    [Arguments("title:x")]
    [Arguments("NEAR(a b)")]
    [Arguments("*")]
    [Arguments("-x ^y {z}")]
    public async Task Fts5_syntax_in_queries_is_treated_as_words(string query)
    {
        using var index = Index(Post("a", "A", "Text with a b c x y z words."));

        var hits = index.Search(query);

        await Assert.That(hits).IsNotNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("!!! ???")]
    public async Task Queries_without_words_find_nothing(string? query)
    {
        using var index = Index(Post("a", "A", "Text."));

        await Assert.That(index.Search(query)).IsEmpty();
    }

    [Test]
    public async Task Snippets_escape_html_and_mark_only_matches()
    {
        using var index = Index(Post("a", "A", "Use `<script>` & friends: the script element runs code."));

        var snippet = index.Search("script").Single().SnippetHtml;

        await Assert.That(snippet).DoesNotContain("<script>");
        await Assert.That(snippet).Contains("&lt;<mark>script</mark>&gt;");
        await Assert.That(snippet).Contains("&amp;");
    }

    [Test]
    public async Task A_match_only_in_code_shows_its_line_as_code()
    {
        using var index = Index(Post("a", "A", "Some prose.\n\n```sql\nSELECT 1;\nSELECT * FROM chunks WHERE chunks MATCH 'rendering';\nSELECT 2;\n```\n"));

        var hit = index.Search("rendering").Single();

        await Assert.That(hit.IsCode).IsTrue();
        await Assert.That(hit.SnippetHtml).IsEqualTo("SELECT * FROM chunks WHERE chunks MATCH &#39;<mark>rendering</mark>&#39;;");
    }

    [Test]
    public async Task A_match_in_the_text_wins_over_one_in_code()
    {
        using var index = Index(Post("a", "A", "Rendering on the server.\n\n```js\nrendering();\n```\n"));

        var hit = index.Search("rendering").Single();

        await Assert.That(hit.IsCode).IsFalse();
        await Assert.That(hit.SnippetHtml).Contains("<mark>Rendering</mark> on the server");
    }

    [Test]
    public async Task Match_expression_quotes_every_word_and_caps_their_number()
    {
        await Assert.That(SearchIndex.ToMatchExpression("Hello, wörld!")).IsEqualTo("\"Hello\" \"wörld\"*");
        await Assert.That(SearchIndex.ToMatchExpression("a b c d e f g h i j")).IsEqualTo("\"a\" \"b\" \"c\" \"d\" \"e\" \"f\" \"g\" \"h\"*");
        await Assert.That(SearchIndex.ToMatchExpression(new string('x', 500))).IsEqualTo($"\"{new string('x', SearchIndex.MaxQueryLength)}\"*");
    }

    [Test]
    public async Task Provider_rebuilds_when_the_catalog_changes()
    {
        var content = new ContentCatalogProvider();
        content.Set(ContentCatalog.Build(SiteSettings.Empty, [new ParseResult(Post("a", "Alpha", "First."), null)]));
        using var provider = new SearchIndexProvider(content, new NoEmbedder());
        var first = provider.Current;

        content.Set(ContentCatalog.Build(SiteSettings.Empty, [new ParseResult(Post("b", "Beta", "Second."), null)]));

        await Assert.That(provider.Current).IsNotSameReferenceAs(first);
        await Assert.That(provider.Current.Search("beta").Single().Item.Slug).IsEqualTo("b");
        await Assert.That(provider.Current.Search("alpha")).IsEmpty();
    }
}
