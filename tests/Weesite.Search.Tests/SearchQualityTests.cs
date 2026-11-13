using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Weesite.Content;
using Weesite.Content.Sources;

namespace Weesite.Search.Tests;

/// <summary>
/// The meaning leg's thresholds on the sample site (T-0128, F-0056): labelled queries, each with the
/// items it's about (to be found) and the items it may also find; anything else is noise. Both
/// models embed the whole sample, about ten seconds: run `just model` first.
/// <see cref="Measure"/> prints each model's leads and similarities, to set the thresholds from.
/// </summary>
internal sealed class SearchQualityTests
{
    private static readonly Lazy<ContentCatalog> Catalog = new(LoadSample);
    private static readonly ConcurrentDictionary<string, Lazy<SearchIndex>> Indexes = new(StringComparer.Ordinal);

    // Posts in Hungarian are allowed, not required, for English words: e5 links the languages only weakly.
    private static readonly string[] Almonds = ["an-almond-farm-near-alhama-de-granada", "green-almonds-on-a-dry-hillside"];
    private static readonly string[] AlmondsHu = ["mandulafák-a-sierra-nevada-lejtőin", "mandulafak-andaluziaban", "zöld-mandula-andalúziában"];
    private static readonly string[] Aegina = ["aegina-harbour-at-sunset", "morning-on-the-aegina-waterfront", "the-bay-coastline-on-aegina", "the-harbour-chapel-of-agios-nikolaos-on-aegina"];
    private static readonly string[] GreekIslands = [.. Aegina, "vitorlasok-az-aeginai-kikotoben", "climbing-to-the-acropolis-of-lindos", "temple-columns-on-the-acropolis-of-lindos", "two-islands-off-eretria-euboea", "agios-minas-bay-near-chalkida", "old-anchor-on-the-seafront-at-karystos", "walking-the-south-coast-of-crete", "test-checklist-at-the-piraeus-ferry-port"];
    private static readonly string[] OpenTofu = ["moving-from-bicep-to-opentofu", "a-small-rego-baseline-for-infrastructure", "encrypting-opentofu-state-with-key-vault", "reviewing-opentofu-plans-in-pull-requests"];
    private static readonly string[] Alhambra = ["the-alhambra-at-dusk", "the-alcazaba-of-the-alhambra", "the-alhambra-hill-above-granada", "alhambra-from-the-paseo-de-los-tristes"];
    private static readonly string[] Granada = [.. Alhambra, "the-albaicin-from-the-generalife", "albaicin-from-san-nicolas", "green-arches-in-the-generalife-gardens-granada"];
    private static readonly string[] Quebec = ["a-winter-week-in-quebec-city", "snow-on-25e-rue-in-quebec-city", "avenue-cartier-quebec-city-at-night", "a-barber-shop-in-quebec-city", "backups-and-an-old-bank-in-quebec-city", "autumn-colours-near-bourg-royal-quebec-city", "brick-triplexes-on-rue-aberdeen", "osz-quebec-varosaban"];

    /// <summary>Gets the labelled queries.</summary>
    public static IReadOnlyList<LabelledQuery> Queries { get; } =
    [
        // The site's technical posts.
        new("cheap cloud hosting", ["azure-container-apps-on-a-hobby-budget"], ["chiseled-net-images-in-practice", "backups-and-an-old-bank-in-quebec-city", .. OpenTofu]),
        new("why use a justfile", ["why-every-repo-here-has-a-justfile"], ["toml-yaml-and-the-configs-in-between", "small-deploys-like-a-long-lake-bridge"]),
        new("infrastructure as code on Azure", ["moving-from-bicep-to-opentofu"], [.. OpenTofu, "azure-container-apps-on-a-hobby-budget"]),
        new("policy checks for infrastructure code", ["a-small-rego-baseline-for-infrastructure"], [.. OpenTofu]),
        new("keeping secrets out of the state file", ["encrypting-opentofu-state-with-key-vault"], [.. OpenTofu]),
        new("smaller container images for .NET", ["chiseled-net-images-in-practice"], ["azure-container-apps-on-a-hobby-budget", "testing-blazor-ssr-with-tunit-and-playwright"]),
        new("end to end browser tests", ["testing-blazor-ssr-with-tunit-and-playwright"], ["test-checklist-at-the-piraeus-ferry-port", "a-sunset-walk-after-a-day-of-debugging", "small-deploys-like-a-long-lake-bridge", "forms-that-work-without-javascript"]),
        new("dark mode colour schemes", ["theming-with-light-dark-and-cascade-layers"], ["typography", "rue-saint-saens-at-night-in-black-and-white"]),
        new("forms without scripts", ["forms-that-work-without-javascript"], ["notes-on-static-rendering", "beach-path-on-ile-de-re-and-accessible-pages", "testing-blazor-ssr-with-tunit-and-playwright"]),
        new("server side rendering", ["notes-on-static-rendering"], ["forms-that-work-without-javascript", "testing-blazor-ssr-with-tunit-and-playwright", "hybrid-search-without-a-search-service"]),
        new("search with embeddings", ["hybrid-search-without-a-search-service"], []),
        new("choosing a config file format", ["toml-yaml-and-the-configs-in-between"], ["why-every-repo-here-has-a-justfile"]),
        new("functional programming", ["a-weekend-with-haskell-type-classes"], []),
        new("making better coffee", ["notes-on-dialing-in-espresso"], ["one-espresso-one-task"]),
        new("headphone jack", ["small-things-the-audio-plug"], []),
        new("reviewing deployment plans", ["reviewing-opentofu-plans-in-pull-requests"], [.. OpenTofu, "small-deploys-like-a-long-lake-bridge"]),
        new("who writes this site", ["about"], ["hello-world", "terms"]),
        new("what personal data is collected", ["privacy"], ["terms"]),

        // The travel and photo posts.
        new("Roman ruins", ["the-amphitheatre-at-pompeii", "roman-amphitheatre-at-saintes"], ["amphoras-in-a-storeroom-at-herculaneum", "charred-window-shutters-at-herculaneum", "plain-text-files-and-the-jars-at-pompeii", "early-morning-at-the-parthenon-athens", "climbing-to-the-acropolis-of-lindos", "temple-columns-on-the-acropolis-of-lindos", "cliff-shelters-at-la-roque-saint-christophe"]),
        new("Greek islands", ["aegina-harbour-at-sunset"], [.. GreekIslands, "agios-nikolaos-from-lycabettus", "early-morning-at-the-parthenon-athens"]),
        new("almond trees", [.. Almonds], [.. AlmondsHu, "tree-of-heaven-in-the-sierra-nevada", "montpellier-maple-in-el-torcal", "paella-otthon-andaluz-modra"]),
        new("snow in winter", ["snow-on-25e-rue-in-quebec-city", "a-winter-week-in-quebec-city"], [.. Quebec, "forest-walks-in-every-season"]),
        new("birds", ["a-mallard-by-the-water-in-montreal", "grey-crowned-crane-at-the-sao-paulo-zoo"], ["pillangó-margarita-szigetén"]),
        new("boats in a harbour", ["aegina-harbour-at-sunset", "dinghies-at-conleau-gulf-of-morbihan"], [.. Aegina, "vitorlasok-az-aeginai-kikotoben", "a-fishing-boat-left-on-the-sand", "a-beached-boat-at-chacachacare-lagoon", "old-anchor-on-the-seafront-at-karystos", "a-helicopter-over-botafogo-bay", "test-checklist-at-the-piraeus-ferry-port", "lighthouses-and-long-exposures", "agios-minas-bay-near-chalkida", "red-anchors-at-chacachacare", "walking-the-south-coast-of-crete"]),
        new("city streets at night", ["avenue-cartier-quebec-city-at-night", "rue-saint-saens-at-night-in-black-and-white"], [.. Quebec, "crossing-avenida-faria-lima", "lighthouses-and-long-exposures", "the-alhambra-at-dusk", "séta-a-bécsi-grabenen", "writing-on-a-phone-in-munich"]),
        new("a ruined abbey", ["photographing-the-ruined-abbey-on-ile-de-re"], ["kerékpárral-a-romos-apátsághoz", "the-red-brick-greyfriars-abbey-in-odense", "mandulafak-andaluziaban", "abandoned-house-in-the-scrub-margarita-island"]),
        new("gardens in Spain", ["a-garden-alley-in-the-alcazar-of-seville", "gardens-of-the-alcazar-in-cordoba"], [.. Granada, "an-agave-in-flower", "mályvarózsák-egy-francia-faluban", "allee-des-philosophes-at-chantilly", .. Almonds, .. AlmondsHu]),
        new("the Alhambra in Granada", [.. Alhambra], [.. Granada, "the-alcazaba-walls-in-almeria", "an-almond-farm-near-alhama-de-granada"]),
        new("watching the sunset", ["aegina-harbour-at-sunset", "sunset-at-las-guevaras-margarita-island", "sunset-over-the-forest-in-anzoategui"], ["a-sunset-walk-after-a-day-of-debugging", "the-alhambra-at-dusk", "avenue-cartier-quebec-city-at-night", "two-islands-off-eretria-euboea", "walking-the-south-coast-of-crete"]),
        new("cooking paella", [], ["paella-otthon-andaluz-modra", .. Almonds, .. AlmondsHu]),
        new("autumn leaves", ["autumn-colours-near-bourg-royal-quebec-city"], [.. Quebec, "forest-walks-in-every-season", "forest-path-at-the-lacs-laberge"]),

        // In Hungarian: a multilingual model only.
        new("mandulafák virágzása", ["mandulafák-a-sierra-nevada-lejtőin"], [.. Almonds, .. AlmondsHu, "tree-of-heaven-in-the-sierra-nevada", "mályvarózsák-egy-francia-faluban"], Multilingual: true),
        new("vitorlások a kikötőben", ["vitorlasok-az-aeginai-kikotoben"], [.. Aegina, "vitorlasok-az-aeginai-kikotoben", "dinghies-at-conleau-gulf-of-morbihan", "old-anchor-on-the-seafront-at-karystos", "a-fishing-boat-left-on-the-sand", "a-beached-boat-at-chacachacare-lagoon", "test-checklist-at-the-piraeus-ferry-port", "agios-minas-bay-near-chalkida"], Multilingual: true),
        new("ősz Québecben", ["osz-quebec-varosaban"], [.. Quebec], Multilingual: true),
        new("napóra egy bécsi palotán", ["napora-a-hofburgban"], ["séta-a-bécsi-grabenen"], Multilingual: true),

        // Unrelated: nothing should be found (F-0056's "kubernetes" and "key" among them).
        new("kubernetes", [], ["chiseled-net-images-in-practice", "azure-container-apps-on-a-hobby-budget"]),
        new("key", [], ["encrypting-opentofu-state-with-key-vault"]),
        new("football results", [], []),
        new("chocolate cake recipe", [], ["paella-otthon-andaluz-modra", "notes-on-dialing-in-espresso"]),
        new("car insurance prices", [], []),
        new("stock market news", [], []),
        new("learning to play the violin", [], []),
    ];

    /// <summary>Gets the models to measure.</summary>
    /// <returns>Each model's name.</returns>
    public static IEnumerable<string> Models() => EmbeddingModel.All.Select(m => m.Name);

    /// <summary>
    /// The meaning leg at each model's default thresholds keeps finding most of what the queries
    /// are about, with noise in few queries: the bands overlap on 100 posts, so these are the
    /// measured results with a little room (2026-11-13, F-0103 and F-0112), a bar against getting worse.
    /// </summary>
    /// <param name="modelName">The model.</param>
    /// <param name="minFound">The least share of the labelled items found.</param>
    /// <param name="maxNoisyQueries">The most queries that find something unrelated.</param>
    /// <returns>A task.</returns>
    [Test]
    [Arguments("bge-small-en-v1.5", 0.75, 5)]
    [Arguments("multilingual-e5-small", 0.55, 4)]
    public async Task The_meaning_leg_finds_most_of_what_queries_are_about_with_little_noise(string modelName, double minFound, int maxNoisyQueries)
    {
        var model = EmbeddingModel.Find(modelName)!;
        var index = Index(model);
        int must = 0, found = 0;
        var noisy = new List<string>();
        var missed = new List<string>();
        foreach (var query in Queries.Where(q => IsMultilingual(model) || !q.Multilingual))
        {
            var hits = index.SearchSimilar(query.Text, 50, model.MinSimilarity, model.MinLead).Select(h => h.Item.Slug).ToHashSet(StringComparer.Ordinal);
            must += query.Relevant.Length;
            found += query.Relevant.Count(hits.Contains);
            missed.AddRange(query.Relevant.Where(s => !hits.Contains(s)).Select(s => $"\"{query.Text}\": {s}"));
            var noise = hits.Where(s => !query.Allows(s)).ToList();
            if (noise.Count > 0)
            {
                noisy.Add($"\"{query.Text}\": {string.Join(", ", noise)}");
            }
        }

        await Assert.That((double)found / must).IsGreaterThanOrEqualTo(minFound).Because($"missed: {string.Join("; ", missed)}");
        await Assert.That(noisy.Count).IsLessThanOrEqualTo(maxNoisyQueries).Because($"noise: {string.Join("; ", noisy)}");
    }

    /// <summary>
    /// Prints, for each model and query, the lowest lead over the median of what it must find and
    /// the highest lead of noise; and over all queries, the band between them for each threshold.
    /// </summary>
    /// <param name="modelName">The model.</param>
    /// <returns>A task.</returns>
    [Test]
    [Explicit]
    [MethodDataSource(nameof(Models))]
    public async Task Measure(string modelName)
    {
        var model = EmbeddingModel.Find(modelName)!;
        var index = Index(model);
        var report = new StringBuilder().Append(CultureInfo.InvariantCulture, $"{model.Name}: floor {model.MinSimilarity}, lead {model.MinLead}\n");
        float lowestLead = float.MaxValue, highestNoiseLead = float.MinValue, lowestScore = float.MaxValue, highestNoiseScore = float.MinValue;
        var measured = new List<(LabelledQuery Query, IReadOnlyList<(ContentItem Item, float Score)> Scores, float Median)>();
        foreach (var query in Queries.Where(q => IsMultilingual(model) || !q.Multilingual))
        {
            var scores = index.Similarities(query.Text);
            var sorted = scores.Select(s => s.Score).Order().ToArray();
            var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
            measured.Add((query, scores, median));
            var must = query.Relevant;
            var wanted = scores.Where(s => must.Contains(s.Item.Slug)).ToList();
            var noise = scores.Where(s => !query.Allows(s.Item.Slug)).OrderByDescending(s => s.Score).ToList();
            var worst = wanted.Count > 0 ? wanted.MinBy(s => s.Score) : default;
            var loudest = noise.Count > 0 ? noise[0] : default;
            report.Append(CultureInfo.InvariantCulture, $"  {query.Text,-40} median {median:0.000}");
            if (wanted.Count > 0)
            {
                report.Append(CultureInfo.InvariantCulture, $"  must {worst.Score:0.000} (+{worst.Score - median:0.000}) {worst.Item.Slug}");
                lowestLead = Math.Min(lowestLead, worst.Score - median);
                lowestScore = Math.Min(lowestScore, worst.Score);
            }

            if (noise.Count > 0)
            {
                report.Append(CultureInfo.InvariantCulture, $"  noise {loudest.Score:0.000} (+{loudest.Score - median:0.000}) {loudest.Item.Slug}");
                highestNoiseLead = Math.Max(highestNoiseLead, loudest.Score - median);
                highestNoiseScore = Math.Max(highestNoiseScore, loudest.Score);
            }

            report.Append('\n');
        }

        report.Append(CultureInfo.InvariantCulture, $"  lead: must from {lowestLead:0.000}, noise up to {highestNoiseLead:0.000}; similarity: must from {lowestScore:0.000}, noise up to {highestNoiseScore:0.000}\n");

        // What each lead would find, with the model's floor and without one: of the items the
        // queries are about, how many; of the rest, how many (noise) and in how many queries.
        var mustTotal = measured.Sum(m => m.Query.Relevant.Length);
        foreach (var floor in new[] { model.MinSimilarity, -1f })
        {
            report.Append(CultureInfo.InvariantCulture, $"  floor {floor}:\n");
            for (var step = 8; step <= 50; step++)
            {
                var lead = step / 200f;
                int found = 0, noise = 0, noisyQueries = 0;
                foreach (var (query, scores, median) in measured)
                {
                    var hits = scores.Where(s => s.Score >= Math.Max(floor, median + lead)).Select(s => s.Item.Slug).ToList();
                    found += hits.Count(query.Relevant.Contains);
                    var loud = hits.Count(h => !query.Allows(h));
                    noise += loud;
                    noisyQueries += loud > 0 ? 1 : 0;
                }

                report.Append(CultureInfo.InvariantCulture, $"    lead {lead:0.000}: found {found}/{mustTotal}, noise {noise} in {noisyQueries} queries\n");
            }
        }
        Console.WriteLine(report);
        Directory.CreateDirectory(Path.Combine(FindRepoRoot(), "artifacts", "search-quality"));
        await File.WriteAllTextAsync(Path.Combine(FindRepoRoot(), "artifacts", "search-quality", $"{model.Name}.txt"), report.ToString());
    }

    // Every model but the English default reads other languages (ADR 0008).
    private static bool IsMultilingual(EmbeddingModel model) => model != EmbeddingModel.BgeSmallEn;

    private static SearchIndex Index(EmbeddingModel model) =>
        Indexes.GetOrAdd(model.Name, _ => new Lazy<SearchIndex>(() =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Models", model.Name);
            if (!OnnxEmbedder.IsAvailable(directory, model))
            {
                throw new InvalidOperationException($"No embedding model in {directory}: run `just model`.");
            }

            return SearchIndex.Build(Catalog.Value, new OnnxEmbedder(directory, model));
        })).Value;

    private static ContentCatalog LoadSample()
    {
        var source = new FileContentSource(Path.Combine(FindRepoRoot(), "seed"));
        var site = SiteSettings.Parse(source.ReadSettingsAsync(CancellationToken.None).GetAwaiter().GetResult()!);
        var files = source.ReadContentAsync(CancellationToken.None).GetAwaiter().GetResult();
        return ContentCatalog.Build(site, files.Select(f => ContentParser.Parse(f.Path, f.Text, site.Language)));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Weesite.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root wasn't found.");
    }

    /// <summary>A query with what it's about and what it may also find.</summary>
    /// <param name="Text">The query.</param>
    /// <param name="Relevant">What it's about: these must be found.</param>
    /// <param name="Allowed">What it may also find.</param>
    /// <param name="Multilingual">Whether only a multilingual model is asked (a query in another language).</param>
    internal sealed record LabelledQuery(string Text, string[] Relevant, string[] Allowed, bool Multilingual = false)
    {
        /// <summary>Gets whether a found item is fine for this query.</summary>
        /// <param name="slug">The item's slug.</param>
        /// <returns>True when it's relevant or allowed.</returns>
        public bool Allows(string slug) => Relevant.Contains(slug) || Allowed.Contains(slug);
    }
}
