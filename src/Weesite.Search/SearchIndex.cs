using System.Text;
using Microsoft.Data.Sqlite;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>A search result: a post or page, its best-matching section and a snippet.</summary>
/// <param name="Item">The post or page.</param>
/// <param name="Heading">The heading of the section that matched best, or null.</param>
/// <param name="SnippetHtml">An excerpt, HTML-escaped; keyword results mark the matched words with <c>&lt;mark&gt;</c>.</param>
/// <param name="Rank">The position in its ranking, from 1.</param>
public sealed record SearchHit(ContentItem Item, string? Heading, string SnippetHtml, int Rank);

/// <summary>
/// The search index, built once per content catalog (brief, Hybrid search). Keywords: SQLite
/// FTS5 in memory, one row per chunk, BM25 with title and headings weighted above body text, and
/// snippets; queries are serialized on the one connection and take a millisecond or two. Meaning:
/// one vector per chunk in plain arrays, compared by brute-force cosine similarity.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    /// <summary>The longest query accepted; the rest is ignored.</summary>
    public const int MaxQueryLength = 200;

    /// <summary>The most words of a query that are used.</summary>
    public const int MaxTerms = 8;

    private const char MarkStart = '\u0001';
    private const char MarkEnd = '\u0002';

    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, ContentItem> _items = new(StringComparer.Ordinal);
    private readonly List<(Chunk Chunk, float[] Vector)> _vectors = [];
    private readonly Dictionary<string, float[]> _postVectors = new(StringComparer.Ordinal);
    private readonly List<ContentItem> _posts = [];
    private readonly IEmbedder _embedder;
    private readonly Lock _lock = new();
    private bool _disposed;

    private SearchIndex(SqliteConnection connection, int chunkCount, IEmbedder embedder)
    {
        _connection = connection;
        ChunkCount = chunkCount;
        _embedder = embedder;
    }

    /// <summary>Gets the number of indexed chunks.</summary>
    public int ChunkCount { get; }

    /// <summary>Gets a value indicating whether the chunks have vectors (the model was available).</summary>
    public bool HasVectors => _vectors.Count > 0;

    /// <summary>Builds an index of every post and page in a catalog.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="embedder">The embedding model; without one, only keyword search works.</param>
    /// <returns>The index.</returns>
    public static SearchIndex Build(ContentCatalog catalog, IEmbedder? embedder = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        embedder ??= new NoEmbedder();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            // English stemming for now; Hungarian gets its own table with unicode61 (brief).
            create.CommandText = """
                CREATE VIRTUAL TABLE chunks USING fts5(
                    title, heading, body, item UNINDEXED, chunk UNINDEXED,
                    tokenize = 'porter unicode61 remove_diacritics 2');
                """;
            create.ExecuteNonQuery();
        }

        var chunks = catalog.Posts.Concat(catalog.Pages).SelectMany(Chunker.Split).ToList();
        var index = new SearchIndex(connection, chunks.Count, embedder);
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO chunks (title, heading, body, item, chunk) VALUES ($title, $heading, $body, $item, $chunk)";
        var title = insert.Parameters.Add("$title", SqliteType.Text);
        var heading = insert.Parameters.Add("$heading", SqliteType.Text);
        var body = insert.Parameters.Add("$body", SqliteType.Text);
        var item = insert.Parameters.Add("$item", SqliteType.Text);
        var chunk = insert.Parameters.Add("$chunk", SqliteType.Integer);
        foreach (var c in chunks)
        {
            var key = Key(c.Item);
            index._items.TryAdd(key, c.Item);

            // The title counts once per item, on its first chunk.
            title.Value = c.Index == 0 ? c.Item.Title : "";
            heading.Value = c.Heading ?? "";
            body.Value = c.Text;
            item.Value = key;
            chunk.Value = c.Index;
            insert.ExecuteNonQuery();
            if (embedder.Dimensions > 0)
            {
                index._vectors.Add((c, embedder.EmbedPassage(PassageText(c))));
            }
        }

        transaction.Commit();
        index._posts.AddRange(catalog.Posts);
        foreach (var group in index._vectors.Where(v => v.Chunk.Item.Kind == ContentKind.Post).GroupBy(v => Key(v.Chunk.Item)))
        {
            var mean = new float[embedder.Dimensions];
            foreach (var (_, vector) in group)
            {
                System.Numerics.Tensors.TensorPrimitives.Add(mean, vector, mean);
            }

            Vectors.Normalize(mean);
            index._postVectors[group.Key] = mean;
        }

        return index;
    }

    /// <summary>
    /// Finds the posts most like a post: by meaning (the mean of each post's chunk vectors) when
    /// the index has vectors, otherwise by the number of shared tags. Never the post itself.
    /// </summary>
    /// <param name="post">The post.</param>
    /// <param name="count">The most posts to return.</param>
    /// <param name="minSimilarity">The lowest similarity a related post needs (meaning only).</param>
    /// <returns>The related posts, closest first.</returns>
    public IReadOnlyList<ContentItem> Related(ContentItem post, int count = 3, float minSimilarity = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(post);
        var key = Key(post);
        var others = _posts.Where(p => Key(p) != key);
        if (_postVectors.TryGetValue(key, out var vector))
        {
            return others
                .Select(p => (Post: p, Score: _postVectors.TryGetValue(Key(p), out var v) ? Vectors.Similarity(vector, v) : -1))
                .Where(x => x.Score >= minSimilarity)
                .OrderByDescending(x => x.Score)
                .Take(count)
                .Select(x => x.Post)
                .ToList();
        }

        return others
            .Select(p => (Post: p, Shared: p.Tags.Count(post.Tags.Contains)))
            .Where(x => x.Shared > 0)
            .OrderByDescending(x => x.Shared)
            .Take(count)
            .Select(x => x.Post)
            .ToList();
    }

    /// <summary>
    /// Searches by meaning: embeds the query and ranks posts and pages by their most similar
    /// section. Empty when the index has no vectors or the query no words.
    /// </summary>
    /// <param name="query">The visitor's words.</param>
    /// <param name="limit">The most results to return.</param>
    /// <param name="minSimilarity">The lowest cosine similarity a section needs to count.</param>
    /// <returns>The results, most similar first.</returns>
    public IReadOnlyList<SearchHit> SearchSimilar(string? query, int limit = 20, float minSimilarity = -1)
    {
        if (!HasVectors || ToMatchExpression(query) is null || limit <= 0)
        {
            return [];
        }

        var text = query!.Length > MaxQueryLength ? query[..MaxQueryLength] : query;
        var vector = _embedder.EmbedQuery(text);
        var best = new Dictionary<string, (Chunk Chunk, float Score)>(StringComparer.Ordinal);
        foreach (var (chunk, chunkVector) in _vectors)
        {
            var score = Vectors.Similarity(vector, chunkVector);
            var key = Key(chunk.Item);
            if (!best.TryGetValue(key, out var current) || score > current.Score)
            {
                best[key] = (chunk, score);
            }
        }

        return best.Values
            .Where(b => b.Score >= minSimilarity)
            .OrderByDescending(b => b.Score)
            .Take(limit)
            .Select((b, i) => new SearchHit(b.Chunk.Item, b.Chunk.Heading, SnippetHtml(Excerpt(b.Chunk.Text)), i + 1))
            .ToList();
    }

    /// <summary>Searches; each post or page appears once, at its best-ranked section.</summary>
    /// <param name="query">The visitor's words.</param>
    /// <param name="limit">The most results to return.</param>
    /// <returns>The results, best first; empty for a query without words.</returns>
    public IReadOnlyList<SearchHit> Search(string? query, int limit = 20)
    {
        var match = ToMatchExpression(query);
        if (match is null || limit <= 0)
        {
            return [];
        }

        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT item, heading, snippet(chunks, 2, char(1), char(2), '…', 24)
                FROM chunks WHERE chunks MATCH $match
                ORDER BY bm25(chunks, 10.0, 4.0, 1.0)
                LIMIT 500
                """;
            command.Parameters.AddWithValue("$match", match);
            using var reader = command.ExecuteReader();
            while (reader.Read() && hits.Count < limit)
            {
                var key = reader.GetString(0);
                if (!seen.Add(key) || !_items.TryGetValue(key, out var item))
                {
                    continue;
                }

                var heading = reader.GetString(1);
                hits.Add(new SearchHit(item, heading.Length > 0 ? heading : null, SnippetHtml(reader.GetString(2)), hits.Count + 1));
            }
        }

        return hits;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _disposed = true;
                _connection.Dispose();
            }
        }
    }

    /// <summary>
    /// Turns free text into an FTS5 expression: up to <see cref="MaxTerms"/> words, each quoted
    /// (so no FTS5 syntax gets through), all required, the last one as a prefix.
    /// </summary>
    /// <param name="query">The visitor's words.</param>
    /// <returns>The expression, or null when there are no words.</returns>
    internal static string? ToMatchExpression(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var text = query.Length > MaxQueryLength ? query[..MaxQueryLength] : query;
        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text.Append(' '))
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }

        if (terms.Count == 0)
        {
            return null;
        }

        var used = terms.Take(MaxTerms).ToList();
        return string.Join(' ', used.Select((t, i) => i == used.Count - 1 ? $"\"{t}\"*" : $"\"{t}\""));
    }

    private static string Key(ContentItem item) => $"{item.Kind}:{item.Slug}";

    // A chunk's text for embedding: the title and heading give the section its context.
    private static string PassageText(Chunk chunk) =>
        string.Join(". ", new[] { chunk.Item.Title, chunk.Heading, chunk.Text }.Where(s => !string.IsNullOrWhiteSpace(s)));

    // The first ~200 characters, cut at a word.
    private static string Excerpt(string text)
    {
        if (text.Length <= 200)
        {
            return text;
        }

        var cut = text.AsSpan(0, 200).LastIndexOf(' ');
        return string.Concat(text.AsSpan(0, cut > 100 ? cut : 200), "…");
    }

    // HTML-escapes the snippet, then turns the match markers into <mark> elements.
    private static string SnippetHtml(string snippet)
    {
        var html = new StringBuilder(snippet.Length + 32);
        foreach (var ch in snippet)
        {
            html.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                MarkStart => "<mark>",
                MarkEnd => "</mark>",
                '\n' or '\r' or '\t' => " ",
                _ => ch.ToString(),
            });
        }

        return html.ToString();
    }
}
