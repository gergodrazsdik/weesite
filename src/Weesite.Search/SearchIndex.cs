using System.Text;
using Microsoft.Data.Sqlite;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>A search result: a post or page, its best-matching section and a snippet.</summary>
/// <param name="Item">The post or page.</param>
/// <param name="Heading">The heading of the section that matched best, or null.</param>
/// <param name="SnippetHtml">An excerpt, HTML-escaped; keyword results mark the matched words with <c>&lt;mark&gt;</c>.</param>
/// <param name="Rank">The position in its ranking, from 1.</param>
/// <param name="IsCode">Whether the snippet is a line of code (the words matched in a code block).</param>
public sealed record SearchHit(ContentItem Item, string? Heading, string SnippetHtml, int Rank, bool IsCode = false);

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

    /// <summary>The most site words a query's last word is completed to.</summary>
    public const int MaxCompletions = 30;

    /// <summary>
    /// The fewest items with vectors for <c>minLead</c> to apply in <see cref="SearchSimilar"/>:
    /// with fewer, the median says little.
    /// </summary>
    public const int MinItemsForLead = 4;

    private const char MarkStart = '\u0001';
    private const char MarkEnd = '\u0002';

    private readonly SqliteConnection _connection;
    private readonly Dictionary<string, ContentItem> _items = new(StringComparer.Ordinal);
    private readonly List<(Chunk Chunk, float[] Vector)> _vectors = [];
    private readonly Dictionary<string, float[]> _postVectors = new(StringComparer.Ordinal);
    private readonly List<ContentItem> _posts = [];
    private readonly Dictionary<string, Dictionary<string, float[]>> _itemVectors = new(StringComparer.Ordinal);
    private readonly IEmbedder _embedder;
    private readonly Lock _lock = new();

    // Every word on the site as written (lowercase), sorted: the last word of a query is matched
    // as the start of these, not as an FTS5 prefix, which the stemmer would cut first (F-0056).
    private string[] _words = [];
    private bool _disposed;

    private static long _lastId;

    private SearchIndex(SqliteConnection connection, int chunkCount, IEmbedder embedder)
    {
        Id = Interlocked.Increment(ref _lastId);
        _connection = connection;
        ChunkCount = chunkCount;
        _embedder = embedder;
    }

    /// <summary>Gets a number that tells this index from every other built in this process (cache keys).</summary>
    public long Id { get; }

    /// <summary>Gets the number of indexed chunks.</summary>
    public int ChunkCount { get; }

    /// <summary>Gets a value indicating whether the chunks have vectors (the model was available).</summary>
    public bool HasVectors => _vectors.Count > 0;

    /// <summary>Gets how many chunks were embedded for this index rather than found stored.</summary>
    public int EmbeddedCount { get; private set; }

    /// <summary>Gets how many chunks have no vector because the embed limit was reached; they're found by keywords only.</summary>
    public int MissingCount { get; private set; }

    /// <summary>Gets each item's chunk vectors by passage hash, keyed by <see cref="VectorKeys.Item"/>, to store.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, float[]>> ItemVectors =>
        _itemVectors.ToDictionary(i => i.Key, i => (IReadOnlyDictionary<string, float[]>)i.Value, StringComparer.Ordinal);

    /// <summary>Builds an index of every post and page in a catalog.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="embedder">The embedding model; without one, only keyword search works.</param>
    /// <param name="stored">Finds a stored vector by passage hash (<see cref="VectorKeys.Passage"/>); chunks without one are embedded.</param>
    /// <param name="embedLimit">The most chunks to embed; the rest get no vector (bulk work is the re-index job's).</param>
    /// <returns>The index.</returns>
    public static SearchIndex Build(ContentCatalog catalog, IEmbedder? embedder = null, Func<string, float[]?>? stored = null, int embedLimit = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        embedder ??= new NoEmbedder();
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            // Each item's words by its language (ADR 0008): English ones stemmed (porter), the
            // others, which the English stemmer would only mangle, as words without accents.
            create.CommandText = """
                CREATE VIRTUAL TABLE chunks USING fts5(
                    title, heading, body, code, item UNINDEXED, chunk UNINDEXED,
                    tokenize = 'porter unicode61 remove_diacritics 2');
                CREATE VIRTUAL TABLE chunks_plain USING fts5(
                    title, heading, body, code, item UNINDEXED, chunk UNINDEXED,
                    tokenize = 'unicode61 remove_diacritics 2');
                """;
            create.ExecuteNonQuery();
        }

        var chunks = catalog.Posts.Concat(catalog.Pages).SelectMany(Chunker.Split).ToList();
        var index = new SearchIndex(connection, chunks.Count, embedder);
        using var transaction = connection.BeginTransaction();
        using var stemmed = connection.CreateCommand();
        stemmed.CommandText = "INSERT INTO chunks (title, heading, body, code, item, chunk) VALUES ($title, $heading, $body, $code, $item, $chunk)";
        using var plain = connection.CreateCommand();
        plain.CommandText = "INSERT INTO chunks_plain (title, heading, body, code, item, chunk) VALUES ($title, $heading, $body, $code, $item, $chunk)";
        foreach (var c in chunks)
        {
            var insert = IsEnglish(c.Item.Language) ? stemmed : plain;
            insert.Parameters.Clear();
            var title = insert.Parameters.Add("$title", SqliteType.Text);
            var heading = insert.Parameters.Add("$heading", SqliteType.Text);
            var body = insert.Parameters.Add("$body", SqliteType.Text);
            var code = insert.Parameters.Add("$code", SqliteType.Text);
            var item = insert.Parameters.Add("$item", SqliteType.Text);
            var chunk = insert.Parameters.Add("$chunk", SqliteType.Integer);
            var key = Key(c.Item);
            index._items.TryAdd(key, c.Item);

            // The title counts once per item, on its first chunk. Names such as .NET get their
            // shared token (Aliases), which snippets and headings drop again.
            title.Value = c.Index == 0 ? Aliases.Annotate(c.Item.Title) : "";
            heading.Value = Aliases.Annotate(c.Heading ?? "");
            body.Value = Aliases.Annotate(c.Text);
            code.Value = Aliases.Annotate(c.Code);
            item.Value = key;
            chunk.Value = c.Index;
            insert.ExecuteNonQuery();
            if (embedder.Dimensions > 0)
            {
                var passage = PassageText(c);
                var hash = VectorKeys.Passage(passage);
                float[] vector;
                if (stored?.Invoke(hash) is { } found && found.Length == embedder.Dimensions)
                {
                    vector = found;
                }
                else if (index.EmbeddedCount < embedLimit)
                {
                    vector = embedder.EmbedPassage(passage);
                    index.EmbeddedCount++;
                }
                else
                {
                    index.MissingCount++;
                    continue;
                }

                index._vectors.Add((c, vector));
                var itemKey = VectorKeys.Item(c.Item);
                if (!index._itemVectors.TryGetValue(itemKey, out var itemVectors))
                {
                    itemVectors = new Dictionary<string, float[]>(StringComparer.Ordinal);
                    index._itemVectors[itemKey] = itemVectors;
                }

                itemVectors[hash] = vector;
            }
        }

        transaction.Commit();
        index._words = [.. chunks
            .SelectMany(c => Words(string.Join(' ', c.Index == 0 ? c.Item.Title : "", c.Heading, c.Text, c.Code)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
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
    /// <param name="minLead">
    /// How far an item's similarity must be above the median of every item's, for this query
    /// (F-0056): the model rates unrelated text 0.5 to 0.66, so a floor alone lets noise through,
    /// while what a query is about stands out from the rest. Applies from
    /// <see cref="MinItemsForLead"/> items up; 0 turns it off.
    /// </param>
    /// <returns>The results, most similar first.</returns>
    public IReadOnlyList<SearchHit> SearchSimilar(string? query, int limit = 20, float minSimilarity = -1, float minLead = 0)
    {
        if (!HasVectors || ToMatchExpression(query) is null || limit <= 0)
        {
            return [];
        }

        var best = BestByItem(query!);
        var floor = minSimilarity;
        if (minLead > 0 && best.Count >= MinItemsForLead)
        {
            var scores = best.Values.Select(b => b.Score).Order().ToArray();
            var median = scores.Length % 2 == 1 ? scores[scores.Length / 2] : (scores[(scores.Length / 2) - 1] + scores[scores.Length / 2]) / 2;
            floor = Math.Max(floor, median + minLead);
        }

        return best.Values
            .Where(b => b.Score >= floor)
            .OrderByDescending(b => b.Score)
            .Take(limit)
            .Select((b, i) => new SearchHit(b.Chunk.Item, b.Chunk.Heading, SnippetHtml(Excerpt(b.Chunk.Text)), i + 1))
            .ToList();
    }

    /// <summary>
    /// Gets each item's best section similarity for a query, as the meaning leg sees it, before the
    /// floor and the lead (the search quality tests measure them with it).
    /// </summary>
    /// <param name="query">The visitor's words.</param>
    /// <returns>The similarities by item; empty without vectors.</returns>
    internal IReadOnlyList<(ContentItem Item, float Score)> Similarities(string query) =>
        HasVectors ? [.. BestByItem(query).Values.Select(b => (b.Chunk.Item, b.Score))] : [];

    private Dictionary<string, (Chunk Chunk, float Score)> BestByItem(string query)
    {
        var text = query.Length > MaxQueryLength ? query[..MaxQueryLength] : query;
        var vector = _embedder.EmbedQuery(Aliases.ForMeaning(text));
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

        return best;
    }

    /// <summary>Searches; each post or page appears once, at its best-ranked section.</summary>
    /// <param name="query">The visitor's words.</param>
    /// <param name="limit">The most results to return.</param>
    /// <returns>The results, best first; empty for a query without words.</returns>
    public IReadOnlyList<SearchHit> Search(string? query, int limit = 20)
    {
        var match = MatchExpression(query);
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
            // Both tables (ADR 0008); their bm25 scores are close enough to rank together, and a
            // site's items are mostly in one of them.
            command.CommandText = """
                SELECT item, heading, text, code FROM (
                    SELECT item, heading, snippet(chunks, 2, char(1), char(2), '…', 24) AS text,
                        snippet(chunks, 3, char(1), char(2), '…', 16) AS code,
                        bm25(chunks, 10.0, 4.0, 1.0, 1.0) AS score
                    FROM chunks WHERE chunks MATCH $match
                    UNION ALL
                    SELECT item, heading, snippet(chunks_plain, 2, char(1), char(2), '…', 24),
                        snippet(chunks_plain, 3, char(1), char(2), '…', 16),
                        bm25(chunks_plain, 10.0, 4.0, 1.0, 1.0)
                    FROM chunks_plain WHERE chunks_plain MATCH $match)
                ORDER BY score
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

                var heading = Aliases.Strip(reader.GetString(1));
                var text = Aliases.Strip(reader.GetString(2));
                var codeSnippet = Aliases.Strip(reader.GetString(3));

                // A match in the text wins; one only in code shows the matching line as code.
                var hit = !text.Contains(MarkStart, StringComparison.Ordinal) && codeSnippet.Contains(MarkStart, StringComparison.Ordinal)
                    ? new SearchHit(item, heading.Length > 0 ? heading : null, SnippetHtml(CodeLine(codeSnippet)), hits.Count + 1, IsCode: true)
                    : new SearchHit(item, heading.Length > 0 ? heading : null, SnippetHtml(text), hits.Count + 1);
                hits.Add(hit);
            }
        }

        return hits;
    }

    /// <summary>Whether an item's words go to the stemmed (English) keyword table rather than the plain one.</summary>
    /// <param name="language">The item's language code.</param>
    /// <returns>True for English (<c>en</c>, <c>en-GB</c>).</returns>
    internal static bool IsEnglish(string? language) =>
        language is { Length: >= 2 } code && code.StartsWith("en", StringComparison.OrdinalIgnoreCase) && (code.Length == 2 || code[2] == '-');

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
    /// (so no FTS5 syntax gets through), all required, the last one as a prefix. Names such as
    /// .NET become their shared token first (<see cref="Aliases"/>).
    /// </summary>
    /// <param name="query">The visitor's words.</param>
    /// <returns>The expression, or null when there are no words.</returns>
    internal static string? ToMatchExpression(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var text = Aliases.ForKeywords(query.Length > MaxQueryLength ? query[..MaxQueryLength] : query);
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

    /// <summary>
    /// The expression the keyword search runs: every word quoted and required, and the last one,
    /// when it isn't a word on the site, also completed to the site's words that start with it, so
    /// "cascade lay" finds "cascade layers". FTS5's own prefix query would stem "lay" to "lai" first and miss (F-0056).
    /// </summary>
    /// <param name="query">The visitor's words.</param>
    /// <returns>The expression, or null when there are no words.</returns>
    internal string? MatchExpression(string? query)
    {
        if (ToMatchExpression(query) is not { } plain)
        {
            return null;
        }

        var terms = plain.Split(' ');
        var last = terms[^1][1..^2];
        var completions = Completions(Words(last).FirstOrDefault() ?? "");
        terms[^1] = completions.Count == 0
            ? $"\"{last}\""
            : $"(\"{last}\" OR {string.Join(" OR ", completions.Select(w => $"\"{w}\""))})";

        // FTS5 takes an implied AND between phrases only, not next to a bracketed group.
        return string.Join(" AND ", terms);
    }

    // The shortest site words that start with a prefix of two letters or more, at most
    // MaxCompletions; none when the prefix is a whole word on the site.
    private List<string> Completions(string prefix)
    {
        // A word the site has is matched as it is: "key" shouldn't find "keyword".
        var start = Array.BinarySearch(_words, prefix, StringComparer.Ordinal);
        if (prefix.Length < 2 || start >= 0)
        {
            return [];
        }

        start = ~start;
        var found = new List<string>();
        for (var i = start; i < _words.Length && _words[i].StartsWith(prefix, StringComparison.Ordinal); i++)
        {
            if (_words[i].Length > prefix.Length)
            {
                found.Add(_words[i]);
            }
        }

        return [.. found.OrderBy(w => w.Length).ThenBy(w => w, StringComparer.Ordinal).Take(MaxCompletions)];
    }

    // The words of a text as the query splitter sees them: letters and digits, lowercase.
    private static IEnumerable<string> Words(string text)
    {
        var current = new StringBuilder();
        foreach (var ch in text.Append(' '))
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }
    }

    private static string Key(ContentItem item) => $"{item.Kind}:{item.Slug}";

    // A chunk's text for embedding: the title and heading give the section its context.
    private static string PassageText(Chunk chunk) =>
        string.Join(". ", new[] { chunk.Item.Title, chunk.Heading, chunk.Text, chunk.Code }.Where(s => !string.IsNullOrWhiteSpace(s)));

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

    // The line of a code snippet with the first match, trimmed, at most CodeLineLength characters
    // around the match.
    private static string CodeLine(string snippet)
    {
        const int CodeLineLength = 100;
        var line = snippet.Split('\n').First(l => l.Contains(MarkStart, StringComparison.Ordinal)).Trim();
        if (line.Length <= CodeLineLength)
        {
            return line;
        }

        var start = Math.Clamp(line.IndexOf(MarkStart, StringComparison.Ordinal) - (CodeLineLength / 3), 0, line.Length - CodeLineLength);
        var end = line.IndexOf(MarkEnd, start, StringComparison.Ordinal);
        var length = Math.Max(CodeLineLength, end + 1 - start);
        length = Math.Min(length, line.Length - start);
        return string.Concat(start > 0 ? "…" : "", line.AsSpan(start, length), start + length < line.Length ? "…" : "");
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
