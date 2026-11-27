using System.Collections.Frozen;
using System.Net;

namespace Weesite.Content.Markdown;

/// <summary>
/// Highlights code on the server into <c>&lt;span class="hl-…"&gt;</c> elements that the theme
/// colours (ADR 0002), with the native highlighter: syntect and bat's Sublime syntaxes in a Rust
/// library (ADR 0012, <see cref="NativeHighlighter"/>). Languages without a syntax, and every
/// block when the library isn't there, come out as plain, escaped text.
/// </summary>
/// <param name="lineTimeLimit">
/// The time a block cut short may have taken itself and still be kept as it came out: it would
/// take that again at every load (T-0177). A second by default; tests set less.
/// </param>
public sealed class CodeHighlighter(TimeSpan? lineTimeLimit = null)
{
    /// <summary>
    /// Lines longer than this stay plain, and so does the rest of their block: highlighting time
    /// grows with a line's length, and such lines are minified or generated code (T-0122).
    /// </summary>
    public const int MaxLineLength = 1_000;

    /// <summary>Blocks longer than this, in characters, stay plain (T-0122).</summary>
    public const int MaxBlockLength = 20_000;

    private static readonly TimeSpan DefaultLineTimeLimit = TimeSpan.FromSeconds(1);

    // The library's languages and the blocks highlighted, shared by every highlighter: its
    // syntaxes load once per process and never change while the app runs.
    private static readonly Lazy<Shared> State = new(() => new Shared());

    // Fence names that ask for plain code on purpose: no syntax, and nothing to warn about.
    private static readonly FrozenSet<string> PlainNames = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "text", "plaintext", "plain", "txt");

    private readonly TimeSpan _lineTimeLimit = lineTimeLimit ?? DefaultLineTimeLimit;
    private readonly Shared _shared = State.Value;

    /// <summary>Gets a value indicating whether the native highlighter is there; without it, code renders plain.</summary>
    public static bool IsAvailable => NativeHighlighter.IsAvailable;

    /// <summary>Gets the languages with a syntax, by name, each with the fence names that pick it.</summary>
    public IReadOnlyList<CodeLanguage> Languages => _shared.Languages;

    /// <summary>
    /// Loads the library and its syntaxes and highlights one line of shell in the background, so
    /// the first render doesn't wait for them: the app starts it while Key Vault waits for its
    /// token (ADR 0012). Each other language compiles its patterns at its first block.
    /// </summary>
    /// <returns>How long it took.</returns>
    public static Task<TimeSpan> WarmUpAsync() => Task.Run(() =>
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _ = new CodeHighlighter().Highlight("echo \"$HOME\" | tr a-z A-Z", "bash");
        return System.Diagnostics.Stopwatch.GetElapsedTime(started);
    });

    /// <summary>Gets a value indicating whether a fence name asks for plain, unhighlighted code.</summary>
    /// <param name="language">The fence's language.</param>
    /// <returns>True for <c>text</c>, <c>plaintext</c>, <c>plain</c> and <c>txt</c>.</returns>
    public static bool IsPlain(string? language) => language is not null && PlainNames.Contains(language);

    /// <summary>Gets a value indicating whether a fence language has a syntax.</summary>
    /// <param name="language">The fence's language, such as <c>csharp</c> or <c>sh</c>.</param>
    /// <returns>Whether the code will be highlighted.</returns>
    public bool Supports(string? language) => language is not null && _shared.Fences.ContainsKey(language);

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
        var shared = _shared;

        if (language is null || !shared.Fences.TryGetValue(language, out var name)
            || code.Length > MaxBlockLength || budget.Remaining <= TimeSpan.Zero)
        {
            return WebUtility.HtmlEncode(code);
        }

        // The same block comes out the same: what was highlighted is kept (see below).
        if (shared.Cache.Find(name, code) is { } cached)
        {
            return cached;
        }

        NativeHighlight? result;
        try
        {
            result = NativeHighlighter.Highlight(code, language, budget.Remaining, MaxLineLength);
        }
        catch (InvalidOperationException)
        {
            // The library failed on this block (it caught a panic): plain, as for no syntax.
            return WebUtility.HtmlEncode(code);
        }

        if (result is null)
        {
            return WebUtility.HtmlEncode(code);
        }

        budget.Spend(result.Spent);

        // A block coloured to the end is kept, and so is one that stopped at a line too long or a
        // line its syntax fails on, or ran out of time after taking a line's limit itself (T-0177):
        // it would only take that time again at every load, and come out the same. One cut short
        // because the document's other blocks used the time isn't kept: it gets its own turn in
        // another render.
        if (result.Stop is not HighlightStop.OutOfTime || result.Spent >= _lineTimeLimit)
        {
            shared.Cache.Keep(name, code, result.Html);
        }

        return result.Html;
    }

    // The languages, the fence names that pick them, and the blocks highlighted.
    private sealed class Shared
    {
        public Shared()
        {
            Languages = NativeHighlighter.IsAvailable ? NativeHighlighter.Languages() : [];
            var fences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var language in Languages)
            {
                foreach (var fence in language.FenceNames)
                {
                    fences.TryAdd(fence, language.Name);
                }
            }

            Fences = fences.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Gets the languages by name, with their fence names.</summary>
        public IReadOnlyList<CodeLanguage> Languages { get; }

        /// <summary>Gets the fence names, mapped to the names of their languages.</summary>
        public FrozenDictionary<string, string> Fences { get; }

        /// <summary>Gets the blocks highlighted before, by language and code.</summary>
        public HighlightCache Cache { get; } = new();
    }
}

/// <summary>A language the highlighter knows.</summary>
/// <param name="Name">Its name, such as <c>C#</c>.</param>
/// <param name="FenceNames">The names that pick it after a code fence, the one the editor inserts first, such as <c>csharp</c>, <c>cs</c>.</param>
public sealed record CodeLanguage(string Name, IReadOnlyList<string> FenceNames);

/// <summary>
/// The time a document's code may spend being highlighted, all blocks together (T-0122): code
/// that doesn't fit stays plain. The highlighter checks it between lines.
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
/// Highlighted blocks, by language and code, up to <see cref="MaxCharacters"/> in all; when full it
/// starts again, which costs one more render of each block, not correctness.
/// </summary>
internal sealed class HighlightCache
{
    /// <summary>The most characters of code and HTML kept (about 16 MB).</summary>
    public const int MaxCharacters = 8_000_000;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Language, string Code), string> _blocks = new();
    private long _characters;

    /// <summary>Finds a block highlighted before.</summary>
    /// <param name="language">The language's name.</param>
    /// <param name="code">The code.</param>
    /// <returns>Its HTML, or null.</returns>
    public string? Find(string language, string code) => _blocks.GetValueOrDefault((language, code));

    /// <summary>Keeps a highlighted block.</summary>
    /// <param name="language">The language's name.</param>
    /// <param name="code">The code.</param>
    /// <param name="html">Its HTML.</param>
    public void Keep(string language, string code, string html)
    {
        if (Interlocked.Add(ref _characters, code.Length + html.Length) > MaxCharacters)
        {
            _blocks.Clear();
            Interlocked.Exchange(ref _characters, code.Length + html.Length);
        }

        _blocks.TryAdd((language, code), html);
    }
}
