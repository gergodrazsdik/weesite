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
    public async Task Items_in_other_languages_are_found_without_accents_and_by_their_word_starts()
    {
        var english = Post("containers", "Running containers", "Containers run anywhere.");
        var hungarian = Post("kontenerek", "Konténerek futtatása", "A Kubernetesről és a konténerekről.") with { Language = "hu" };
        using var index = Index(english, hungarian);

        await Assert.That(index.Search("konténerek").Select(h => h.Item.Slug)).IsEquivalentTo(["kontenerek"]);
        await Assert.That(index.Search("kontenerek futtatasa").Select(h => h.Item.Slug)).IsEquivalentTo(["kontenerek"]);
        await Assert.That(index.Search("kubernetes").Select(h => h.Item.Slug)).IsEquivalentTo(["kontenerek"]);
        await Assert.That(index.Search("running").Select(h => h.Item.Slug)).IsEquivalentTo(["containers"]);
        await Assert.That(index.Search("runs anywhere")[0].SnippetHtml).Contains("<mark>run</mark>");
        await Assert.That(SearchIndex.IsEnglish("en-GB")).IsTrue();
        await Assert.That(SearchIndex.IsEnglish("hu")).IsFalse();
        await Assert.That(SearchIndex.IsEnglish("eng")).IsFalse();
    }

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
    public async Task Footnotes_strikethrough_and_task_lists_come_out_as_text()
    {
        // F-0091 L17: the site's dialect, so snippets don't show "[^1]" or "~~".
        var item = Post("b", "B", "Text with a note[^1] and ~~old~~ new.\n\n- [x] done\n- [ ] to do\n\n[^1]: The note.\n");

        var text = Chunker.Split(item).Single().Text;

        await Assert.That(text).DoesNotContain("[^1]");
        await Assert.That(text).DoesNotContain("~~");
        await Assert.That(text).DoesNotContain("[x]");
        await Assert.That(text).Contains("old new");
        await Assert.That(text).Contains("The note.");
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
    [Arguments("cascade lay", "layers")]
    [Arguments("cascade layer", "layers")]
    [Arguments("renderi", "rendering")]
    [Arguments("theming cascad", "layers")]
    public async Task The_last_word_is_completed_to_the_sites_words_before_stemming(string query, string expected)
    {
        // F-0056: FTS5 stems a prefix query first ("lay" to "lai", "renderi" stays), so it missed.
        using var index = Index(
            Post("layers", "Theming with light-dark() and cascade layers", "Tokens in layers."),
            Post("rendering", "Static rendering", "Every page is rendered on the server."));

        var hits = index.Search(query);

        await Assert.That(hits.Select(h => h.Item.Slug)).IsEquivalentTo([expected]);
    }

    [Test]
    public async Task A_whole_word_isnt_completed_and_the_expression_is_explicit()
    {
        using var index = Index(
            Post("vault", "Key Vault", "The key stays in the vault."),
            Post("search", "Keywords", "Search by keywords."));

        await Assert.That(index.Search("key").Select(h => h.Item.Slug)).IsEquivalentTo(["vault"]);
        await Assert.That(index.MatchExpression("vault ke")).IsEqualTo("\"vault\" AND (\"ke\" OR \"key\" OR \"keywords\")");
        await Assert.That(index.MatchExpression("vault k")).IsEqualTo("\"vault\" AND \"k\"");
        await Assert.That(index.MatchExpression("  ")).IsNull();
    }

    [Test]
    public async Task Match_expression_quotes_every_word_and_caps_their_number()
    {
        await Assert.That(SearchIndex.ToMatchExpression("Hello, wörld!")).IsEqualTo("\"Hello\" \"wörld\"*");
        await Assert.That(SearchIndex.ToMatchExpression("a b c d e f g h i j")).IsEqualTo("\"a\" \"b\" \"c\" \"d\" \"e\" \"f\" \"g\" \"h\"*");
        await Assert.That(SearchIndex.ToMatchExpression(new string('x', 500))).IsEqualTo($"\"{new string('x', SearchIndex.MaxQueryLength)}\"*");
    }

    [Test]
    [Arguments(".NET")]
    [Arguments("dotnet")]
    [Arguments(".net")]
    public async Task Dotnet_and_its_written_form_find_the_same_posts(string query)
    {
        // T-0079: ".NET" was the word "net" to FTS5, so it found nets and missed "dotnet".
        using var index = Index(
            Post("images", "Chiseled .NET images", "Small images for .NET apps."),
            Post("cli", "The CLI", "Run dotnet build first."),
            Post("aspnet", "Forms", "ASP.NET Core handles the post."),
            Post("net", "Fishing", "A net catches fish."));

        var slugs = index.Search(query).Select(h => h.Item.Slug).ToList();

        await Assert.That(slugs).IsEquivalentTo(["images", "cli", "aspnet"]);
    }

    [Test]
    public async Task Csharp_is_found_by_either_spelling_and_not_by_the_letter_c()
    {
        using var index = Index(
            Post("records", "Records", "Records in C# are immutable by default."),
            Post("pointers", "Pointers", "Pointers in C need care."),
            Post("aspnet", "Endpoints", "ASP.NET endpoints."));

        await Assert.That(index.Search("C#").Select(h => h.Item.Slug)).IsEquivalentTo(["records"]);
        await Assert.That(index.Search("csharp").Select(h => h.Item.Slug)).IsEquivalentTo(["records"]);
        await Assert.That(index.Search("asp.net").Select(h => h.Item.Slug)).IsEquivalentTo(["aspnet"]);
    }

    [Test]
    public async Task Snippets_and_headings_show_the_written_form_marked()
    {
        using var index = Index(Post("upgrade", "Upgrading", "Intro.\n\n## Moving to .NET 11\n\nThe .NET 11 SDK & C# 15 arrive together."));

        var hit = index.Search("dotnet").Single();

        await Assert.That(hit.Heading).IsEqualTo("Moving to .NET 11");
        await Assert.That(hit.SnippetHtml).IsEqualTo("The <mark>.NET</mark> 11 SDK &amp; C# 15 arrive together.");
    }

    [Test]
    [Arguments("…\u0001dotnet\u0002\u0004 images", "…\u0001.NET\u0002 images")]
    [Arguments("Run \u0005.NET\u0003dotnet\u0004 apps", "Run .NET apps")]
    [Arguments("Run \u0005.\u0001NET\u0002\u0003dotnet\u0004 apps", "Run .\u0001NET\u0002 apps")]
    [Arguments("Small images for \u0005.NET…", "Small images for .NET…")]
    [Arguments("Small images for \u0005.NET\u0003dot…", "Small images for .NET…")]
    [Arguments("aspnet dotnet\u0004 Core", "ASP.NET Core")]
    public async Task Annotations_are_removed_even_where_a_snippet_cuts_them(string snippet, string expected) =>
        await Assert.That(Aliases.Strip(snippet)).IsEqualTo(expected);

    [Test]
    public async Task Meaning_search_embeds_the_written_form()
    {
        var embedder = new RecordingEmbedder();
        using var index = SearchIndex.Build(ContentCatalog.Build(SiteSettings.Empty, [new ParseResult(Post("a", "A", "Text."), null)]), embedder);

        _ = index.SearchSimilar("dotnet csharp records");

        await Assert.That(embedder.Queries).IsEquivalentTo([".NET C# records"]);
    }

    private sealed class RecordingEmbedder : IEmbedder
    {
        public List<string> Queries { get; } = [];

        public string ModelId => "recording";

        public int Dimensions => 2;

        public float[] EmbedPassage(string text) => [1f, 0f];

        public float[] EmbedQuery(string text)
        {
            Queries.Add(text);
            return [1f, 0f];
        }
    }

    [Test]
    public async Task Provider_rebuilds_when_the_catalog_changes()
    {
        var content = new ContentCatalogProvider();
        content.Set(ContentCatalog.Build(SiteSettings.Empty, [new ParseResult(Post("a", "Alpha", "First."), null)]));
        using var provider = new SearchIndexProvider(content, new NoEmbedder());
        var first = provider.Current;

        content.Set(ContentCatalog.Build(SiteSettings.Empty, [new ParseResult(Post("b", "Beta", "Second."), null)]));
        await provider.RefreshAsync();

        await Assert.That(provider.Current).IsNotSameReferenceAs(first);
        await Assert.That(provider.Current.Search("beta").Single().Item.Slug).IsEqualTo("b");
        await Assert.That(provider.Current.Search("alpha")).IsEmpty();
    }
}
