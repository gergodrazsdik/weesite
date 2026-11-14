using System.Collections.Frozen;
using System.Net;
using System.Text;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Weesite.Content.Markdown;

/// <summary>
/// Highlights code on the server with TextMateSharp (the VS Code grammars, ADR 0002) into
/// <c>&lt;span class="hl-…"&gt;</c> elements that the theme colours. The extra grammars in
/// <c>Grammars/</c> (docs/grammars.md lists them) load next to the built-in ones. Languages
/// without a grammar come out as plain, escaped text.
/// </summary>
public sealed class CodeHighlighter
{
    // Scope prefixes, most specific first; the first one a token's innermost matching scope
    // starts with gives its class.
    private static readonly (string Prefix, string Class)[] ScopeClasses =
    [
        ("comment", "hl-comment"),
        ("string", "hl-string"),
        ("constant.character.escape", "hl-string"),
        ("constant.numeric", "hl-number"),
        ("constant.language", "hl-constant"),
        ("constant.character", "hl-constant"),
        ("constant.other", "hl-constant"),
        ("keyword.operator", "hl-operator"),
        ("keyword", "hl-keyword"),
        ("storage", "hl-keyword"),
        ("entity.name.function", "hl-function"),
        ("support.function", "hl-function"),
        ("entity.name.type", "hl-type"),
        ("entity.name.class", "hl-type"),
        ("entity.other.inherited-class", "hl-type"),
        ("support.type.property-name", "hl-property"),
        ("support.type", "hl-type"),
        ("support.class", "hl-type"),
        ("entity.name.tag", "hl-tag"),
        ("entity.other.attribute-name", "hl-attribute"),
        ("variable.other.property", "hl-property"),
        ("meta.object-literal.key", "hl-property"),
    ];

    private static readonly TimeSpan LineTimeLimit = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Lines longer than this stay plain, and so does the rest of their block: highlighting time
    /// grows with a line's length, and such lines are minified or generated code (T-0122).
    /// </summary>
    public const int MaxLineLength = 1_000;

    /// <summary>Blocks longer than this, in characters, stay plain (T-0122).</summary>
    public const int MaxBlockLength = 20_000;

    // Grammars load once per process and are shared: loading them takes about half a second, and
    // they never change while the app runs.
    private static readonly Lazy<Grammars> Shared = new(() => new Grammars());

    private readonly Grammars _grammars = Shared.Value;

    // Fence names that ask for plain code on purpose: no grammar, and nothing to warn about.
    private static readonly FrozenSet<string> PlainNames = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "text", "plaintext", "plain", "txt");

    /// <summary>Gets the languages with a grammar, by name, each with the fence names that pick it.</summary>
    public IReadOnlyList<CodeLanguage> Languages => _grammars.Languages;

    /// <summary>Gets a value indicating whether a fence name asks for plain, unhighlighted code.</summary>
    /// <param name="language">The fence's language.</param>
    /// <returns>True for <c>text</c>, <c>plaintext</c>, <c>plain</c> and <c>txt</c>.</returns>
    public static bool IsPlain(string? language) => language is not null && PlainNames.Contains(language);

    /// <summary>Gets a value indicating whether a fence language has a grammar.</summary>
    /// <param name="language">The fence's language, such as <c>csharp</c> or <c>sh</c>.</param>
    /// <returns>Whether the code will be highlighted.</returns>
    public bool Supports(string? language) => language is not null && _grammars.LanguageIds.ContainsKey(language);

    /// <summary>Renders code as HTML: highlighted spans if the language is known, escaped text otherwise.</summary>
    /// <param name="code">The code.</param>
    /// <param name="language">The fence's language, or null.</param>
    /// <param name="budget">
    /// The highlighting time left for the whole document; what doesn't fit stays plain. Without
    /// one, each block gets <see cref="HighlightBudget.PerDocument"/>.
    /// </param>
    /// <returns>HTML for inside <c>&lt;code&gt;</c>.</returns>
    public string Highlight(string code, string? language, HighlightBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        budget ??= new HighlightBudget();

        if (language is null || !_grammars.LanguageIds.TryGetValue(language, out var id)
            || code.Length > MaxBlockLength || budget.Remaining <= TimeSpan.Zero)
        {
            return WebUtility.HtmlEncode(code);
        }

        // Every save renders the whole site again, and the same block always comes out the same:
        // what was highlighted in full is kept, so only new or changed blocks wait for the lock.
        if (_grammars.Cache.Find(id, code) is { } cached)
        {
            return cached;
        }

        var html = new StringBuilder(code.Length * 2);
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        IStateStack? state = null;
        var complete = true;

        // Neither loading grammars nor tokenizing is thread-safe in TextMateSharp; content renders
        // at load, so serializing is cheap.
        lock (_grammars.Lock)
        {
            var grammar = _grammars.Load(id);
            if (grammar is null)
            {
                return WebUtility.HtmlEncode(code);
            }

            var stopped = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (stopped)
                {
                    // After a line that ran out of time, the rest is plain text: the grammar's state
                    // stopped mid-line and would colour what follows wrongly (F-0119).
                    html.Append(WebUtility.HtmlEncode(line));
                    if (i < lines.Length - 1)
                    {
                        html.Append('\n');
                    }

                    continue;
                }

                // Out of time, or a line too long to colour quickly: plain from here (T-0122).
                var limit = budget.Remaining < LineTimeLimit ? budget.Remaining : LineTimeLimit;
                if (line.Length > MaxLineLength || limit <= TimeSpan.Zero)
                {
                    stopped = true;
                    complete = false;
                    i--;
                    continue;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var result = grammar.TokenizeLine(line, state, limit);
                budget.Spend(System.Diagnostics.Stopwatch.GetElapsedTime(started));
                state = result.RuleStack;
                var covered = 0;

                foreach (var token in result.Tokens)
                {
                    var start = Math.Min(token.StartIndex, line.Length);
                    var end = Math.Min(token.EndIndex, line.Length);
                    if (end <= start)
                    {
                        continue;
                    }

                    covered = Math.Max(covered, end);
                    var text = WebUtility.HtmlEncode(line[start..end]);
                    var cssClass = ClassFor(token.Scopes);
                    if (cssClass is null)
                    {
                        html.Append(text);
                    }
                    else
                    {
                        html.Append("<span class=\"").Append(cssClass).Append("\">").Append(text).Append("</span>");
                    }
                }

                // Tokens that stop short (the time limit) leave the rest of the line uncoloured, not gone.
                if (covered < line.Length)
                {
                    html.Append(WebUtility.HtmlEncode(line[covered..]));
                    stopped = true;
                    complete = false;
                }

                if (i < lines.Length - 1)
                {
                    html.Append('\n');
                }
            }
        }

        // Only a block coloured to the end is kept: one cut short by the time limit may do better
        // on a quieter server.
        var highlighted = html.ToString();
        if (complete)
        {
            _grammars.Cache.Keep(id, code, highlighted);
        }

        return highlighted;
    }

    private static string? ClassFor(List<string> scopes)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            foreach (var (prefix, cssClass) in ScopeClasses)
            {
                if (scopes[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return cssClass;
                }
            }
        }

        return null;
    }

    // The registry, the loaded grammars and the fence names; shared by every highlighter.
    private sealed class Grammars
    {
        private readonly RegistryOptions _options = new(ThemeName.DarkPlus);
        private readonly Registry _registry;
        private readonly Dictionary<string, IGrammar?> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public Grammars()
        {
            var extra = Path.Combine(AppContext.BaseDirectory, "Grammars");
            if (Directory.Exists(extra))
            {
                _options.LoadFromLocalDir(extra, overwrite: false);
            }

            _registry = new Registry(_options);
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var language in _options.GetAvailableLanguages())
            {
                ids.TryAdd(language.Id, language.Id);
                foreach (var alias in language.Aliases ?? [])
                {
                    ids.TryAdd(alias, language.Id);
                }
            }

            // Common fence names the grammars don't list as aliases.
            foreach (var (alias, id) in new[] { ("cs", "csharp"), ("c#", "csharp"), ("yml", "yaml"), ("console", "shellscript"), ("shell", "shellscript"), ("pwsh", "powershell") })
            {
                ids.TryAdd(alias, id);
            }

            LanguageIds = ids.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            // A fence name is one word (the info string's first), so aliases with spaces, such as
            // "Shell Script", never match one and aren't listed.
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var language in _options.GetAvailableLanguages())
            {
                names.TryAdd(language.Id, language.Aliases?.FirstOrDefault() ?? language.Id);
            }

            Languages = [.. ids
                .Where(pair => !pair.Key.Contains(' ', StringComparison.Ordinal))
                .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => new CodeLanguage(
                    names.GetValueOrDefault(group.Key, group.Key),
                    [.. group.Select(pair => Lower(pair.Key)).Distinct(StringComparer.Ordinal)]))
                .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)];
        }

        /// <summary>Gets the languages by name, with their fence names.</summary>
        public IReadOnlyList<CodeLanguage> Languages { get; }

        /// <summary>Gets the fence names, mapped to language IDs.</summary>
        public FrozenDictionary<string, string> LanguageIds { get; }

        /// <summary>Gets the lock around loading and tokenizing (neither is thread-safe).</summary>
        public Lock Lock { get; } = new();

        /// <summary>Gets the blocks highlighted in full, by grammar and code.</summary>
        public HighlightCache Cache { get; } = new();

        // Fence names match ignoring case; the list shows them in lower case, as people write them.
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308", Justification = "Lower case for display; matching ignores case.")]
        private static string Lower(string name) => name.ToLowerInvariant();

        /// <summary>Loads a grammar once; call it under <see cref="Lock"/>.</summary>
        /// <param name="languageId">The language ID.</param>
        /// <returns>The grammar, or null when it has none.</returns>
        public IGrammar? Load(string languageId)
        {
            if (!_loaded.TryGetValue(languageId, out var grammar))
            {
                var scope = _options.GetScopeByLanguageId(languageId);
                grammar = scope is null ? null : _registry.LoadGrammar(scope);
                _loaded[languageId] = grammar;
            }

            return grammar;
        }
    }
}

/// <summary>A language the highlighter knows.</summary>
/// <param name="Name">Its name, such as <c>C#</c>.</param>
/// <param name="FenceNames">The names that pick it after a code fence, its ID first, such as <c>csharp</c>, <c>cs</c>.</param>
public sealed record CodeLanguage(string Name, IReadOnlyList<string> FenceNames);

/// <summary>
/// The time a document's code may spend being highlighted, all blocks together (T-0122): code
/// that doesn't fit stays plain. Only the highlighting counts, not waiting for the lock it runs
/// under, so a busy moment doesn't leave a page plain.
/// </summary>
public sealed class HighlightBudget
{
    /// <summary>The time a document gets: ordinary code takes a few milliseconds a block.</summary>
    public static readonly TimeSpan PerDocument = TimeSpan.FromSeconds(2);

    /// <summary>Initializes a new instance of the <see cref="HighlightBudget"/> class.</summary>
    /// <param name="total">The time; <see cref="PerDocument"/> by default.</param>
    public HighlightBudget(TimeSpan? total = null) => Remaining = total ?? PerDocument;

    /// <summary>Gets the time left.</summary>
    public TimeSpan Remaining { get; private set; }

    /// <summary>Takes time off the budget.</summary>
    /// <param name="time">The time spent.</param>
    public void Spend(TimeSpan time) => Remaining -= time;
}

/// <summary>
/// Highlighted blocks, by grammar and code, up to <see cref="MaxCharacters"/> in all; when full it
/// starts again, which costs one more render of each block, not correctness.
/// </summary>
internal sealed class HighlightCache
{
    /// <summary>The most characters of code and HTML kept (about 16 MB).</summary>
    public const int MaxCharacters = 8_000_000;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Grammar, string Code), string> _blocks = new();
    private long _characters;

    /// <summary>Finds a block highlighted before.</summary>
    /// <param name="grammar">The grammar's scope name.</param>
    /// <param name="code">The code.</param>
    /// <returns>Its HTML, or null.</returns>
    public string? Find(string grammar, string code) => _blocks.GetValueOrDefault((grammar, code));

    /// <summary>Keeps a block highlighted in full.</summary>
    /// <param name="grammar">The grammar's scope name.</param>
    /// <param name="code">The code.</param>
    /// <param name="html">Its HTML.</param>
    public void Keep(string grammar, string code, string html)
    {
        if (Interlocked.Add(ref _characters, code.Length + html.Length) > MaxCharacters)
        {
            _blocks.Clear();
            Interlocked.Exchange(ref _characters, code.Length + html.Length);
        }

        _blocks.TryAdd((grammar, code), html);
    }
}
