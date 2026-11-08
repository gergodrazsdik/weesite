using System.Text;
using Microsoft.Data.Sqlite;
using Weesite.Content;

namespace Weesite.Search;

/// <summary>A search result: a post or page, its best-matching section and a snippet.</summary>
/// <param name="Item">The post or page.</param>
/// <param name="Heading">The heading of the section that matched best, or null.</param>
/// <param name="SnippetHtml">An excerpt with the matched words in <c>&lt;mark&gt;</c>, HTML-escaped otherwise.</param>
/// <param name="Rank">The position in the keyword ranking, from 1.</param>
public sealed record SearchHit(ContentItem Item, string? Heading, string SnippetHtml, int Rank);

/// <summary>
/// The keyword index: SQLite FTS5 in memory, one row per chunk, BM25 ranking with title and
/// headings weighted above body text, and snippets (brief, Hybrid search). Built once per content
/// catalog; queries are serialized on the one connection, which takes a millisecond or two.
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
    private readonly Lock _lock = new();
    private bool _disposed;

    private SearchIndex(SqliteConnection connection, int chunkCount)
    {
        _connection = connection;
        ChunkCount = chunkCount;
    }

    /// <summary>Gets the number of indexed chunks.</summary>
    public int ChunkCount { get; }

    /// <summary>Builds an index of every post and page in a catalog.</summary>
    /// <param name="catalog">The content.</param>
    /// <returns>The index.</returns>
    public static SearchIndex Build(ContentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
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
        var index = new SearchIndex(connection, chunks.Count);
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
        }

        transaction.Commit();
        return index;
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
