using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Weesite.Search;

/// <summary>The results of a search.</summary>
/// <param name="Hits">The posts and pages, best first.</param>
/// <param name="UsedMeaning">Whether the vector leg ran (false without a model, or when busy).</param>
public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, bool UsedMeaning)
{
    /// <summary>Gets no results.</summary>
    public static SearchResults Empty { get; } = new([], false);
}

/// <summary>
/// Hybrid search (brief, Hybrid search): the keyword ranking and the meaning ranking merged with
/// reciprocal rank fusion, each item scoring <c>Σ 1 / (60 + rank)</c> over the rankings it's in.
/// Meaning hits below a similarity floor, or not far enough above the median similarity of the
/// site's items for that query, are dropped, so a query unlike anything on the site finds nothing
/// rather than everything (F-0056). A cost guard keeps search cheap on a small replica:
/// queries are capped (<see cref="SearchIndex.MaxQueryLength"/>, <see cref="SearchIndex.MaxTerms"/>),
/// results are cached, and at most two queries embed at once; when both slots stay busy for a
/// second, the query falls back to keywords.
/// </summary>
public sealed class HybridSearch : IDisposable
{
    /// <summary>The RRF constant: dampens the difference between the top ranks.</summary>
    public const int RrfK = 60;

    /// <summary>The default similarity floor for meaning hits, measured on the sample site (T-0042).</summary>
    public const float DefaultMinSimilarity = 0.6f;

    /// <summary>
    /// The default lead over the median similarity a meaning hit needs (F-0056), measured on the
    /// sample site with 30 queries: what a query was about led by 0.118 and more, unrelated text
    /// by 0.112 at most. Narrow, so it's a setting (<c>Search:MinLead</c>).
    /// </summary>
    public const float DefaultMinLead = 0.115f;

    private const int CandidateCount = 50;
    private const int MaxConcurrentEmbeddings = 2;

    private readonly SearchIndexProvider _indexes;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 500 });
    private readonly SemaphoreSlim _embeddingSlots = new(MaxConcurrentEmbeddings, MaxConcurrentEmbeddings);
    private readonly TimeSpan _slotWait;

    /// <summary>Initializes a new instance of the <see cref="HybridSearch"/> class.</summary>
    /// <param name="indexes">The current index.</param>
    /// <param name="configuration">The configuration (<c>Search:MinSimilarity</c>).</param>
    public HybridSearch(SearchIndexProvider indexes, IConfiguration configuration)
        : this(indexes, configuration, TimeSpan.FromSeconds(1))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HybridSearch"/> class.</summary>
    /// <param name="indexes">The current index.</param>
    /// <param name="configuration">The configuration (<c>Search:MinSimilarity</c>).</param>
    /// <param name="slotWait">How long a query waits for an embedding slot before using keywords only.</param>
    internal HybridSearch(SearchIndexProvider indexes, IConfiguration configuration, TimeSpan slotWait)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _indexes = indexes;
        _slotWait = slotWait;
        MinSimilarity = float.TryParse(configuration["Search:MinSimilarity"], NumberStyles.Float, CultureInfo.InvariantCulture, out var floor)
            ? floor
            : DefaultMinSimilarity;
        MinLead = float.TryParse(configuration["Search:MinLead"], NumberStyles.Float, CultureInfo.InvariantCulture, out var lead)
            ? lead
            : DefaultMinLead;
    }

    /// <summary>Gets the lead over the median similarity a meaning hit needs (0: none).</summary>
    public float MinLead { get; }

    /// <summary>Gets the similarity floor for meaning hits.</summary>
    public float MinSimilarity { get; }

    /// <summary>Gets the embedding slots, for tests of the busy fallback.</summary>
    internal SemaphoreSlim EmbeddingSlots => _embeddingSlots;

    /// <summary>Searches by keywords and meaning, merged.</summary>
    /// <param name="query">The visitor's words.</param>
    /// <param name="limit">The most results to return.</param>
    /// <returns>The results.</returns>
    public SearchResults Search(string? query, int limit = 20)
    {
        var expression = SearchIndex.ToMatchExpression(query);
        if (expression is null || limit <= 0)
        {
            return SearchResults.Empty;
        }

        var index = _indexes.Current;
        var key = $"{RuntimeHelpers.GetHashCode(index)}|{limit}|{expression.ToUpperInvariant()}";
        if (_cache.TryGetValue(key, out SearchResults? cached) && cached is not null)
        {
            return cached;
        }

        var keyword = index.Search(query, CandidateCount);
        IReadOnlyList<SearchHit> meaning = [];
        var usedMeaning = false;
        if (index.HasVectors && _embeddingSlots.Wait(_slotWait))
        {
            try
            {
                meaning = index.SearchSimilar(query, CandidateCount, MinSimilarity, MinLead);
                usedMeaning = true;
            }
            finally
            {
                _embeddingSlots.Release();
            }
        }

        var results = new SearchResults(Fuse(keyword, meaning, limit), usedMeaning);

        // Only complete results are cached: a busy fallback shouldn't stick.
        if (usedMeaning || !index.HasVectors)
        {
            _cache.Set(key, results, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cache.Dispose();
        _embeddingSlots.Dispose();
    }

    /// <summary>Merges rankings with reciprocal rank fusion; keyword snippets (with marks) win.</summary>
    /// <param name="keyword">The keyword ranking.</param>
    /// <param name="meaning">The meaning ranking.</param>
    /// <param name="limit">The most results to return.</param>
    /// <returns>The merged ranking, ranks renumbered from 1.</returns>
    internal static IReadOnlyList<SearchHit> Fuse(IReadOnlyList<SearchHit> keyword, IReadOnlyList<SearchHit> meaning, int limit)
    {
        var merged = new Dictionary<(Weesite.Content.ContentKind, string), (double Score, SearchHit Hit, int Order)>();
        var order = 0;
        foreach (var hit in keyword.Concat(meaning))
        {
            var key = (hit.Item.Kind, hit.Item.Slug);
            var score = 1.0 / (RrfK + hit.Rank);
            merged[key] = merged.TryGetValue(key, out var existing)
                ? (existing.Score + score, existing.Hit, existing.Order)
                : (score, hit, order++);
        }

        return merged.Values
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Order)
            .Take(limit)
            .Select((m, i) => m.Hit with { Rank = i + 1 })
            .ToList();
    }
}
