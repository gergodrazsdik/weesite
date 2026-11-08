using System.Collections.Concurrent;
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

    private readonly RegistryOptions _options = new(ThemeName.DarkPlus);
    private readonly Registry _registry;
    private readonly ConcurrentDictionary<string, IGrammar?> _grammars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _languageIds;
    private readonly Lock _tokenizeLock = new();

    /// <summary>Initializes a new instance of the <see cref="CodeHighlighter"/> class.</summary>
    public CodeHighlighter()
    {
        var extra = Path.Combine(AppContext.BaseDirectory, "Grammars");
        if (Directory.Exists(extra))
        {
            _options.LoadFromLocalDir(extra, overwrite: false);
        }

        _registry = new Registry(_options);
        _languageIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in _options.GetAvailableLanguages())
        {
            _languageIds.TryAdd(language.Id, language.Id);
            foreach (var alias in language.Aliases ?? [])
            {
                _languageIds.TryAdd(alias, language.Id);
            }
        }

        // Common fence names the grammars don't list as aliases.
        foreach (var (alias, id) in new[] { ("cs", "csharp"), ("c#", "csharp"), ("yml", "yaml"), ("console", "shellscript"), ("shell", "shellscript"), ("pwsh", "powershell") })
        {
            _languageIds.TryAdd(alias, id);
        }
    }

    /// <summary>Gets a value indicating whether a fence language has a grammar.</summary>
    /// <param name="language">The fence's language, such as <c>csharp</c> or <c>sh</c>.</param>
    /// <returns>Whether the code will be highlighted.</returns>
    public bool Supports(string? language) => language is not null && _languageIds.ContainsKey(language);

    /// <summary>Renders code as HTML: highlighted spans if the language is known, escaped text otherwise.</summary>
    /// <param name="code">The code.</param>
    /// <param name="language">The fence's language, or null.</param>
    /// <returns>HTML for inside <c>&lt;code&gt;</c>.</returns>
    public string Highlight(string code, string? language)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (language is null || !_languageIds.TryGetValue(language, out var id))
        {
            return WebUtility.HtmlEncode(code);
        }

        var html = new StringBuilder(code.Length * 2);
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        IStateStack? state = null;

        // Neither loading grammars nor tokenizing is thread-safe in TextMateSharp; content renders
        // at load, so serializing is cheap.
        lock (_tokenizeLock)
        {
            var grammar = _grammars.GetOrAdd(id, LoadGrammar);
            if (grammar is null)
            {
                return WebUtility.HtmlEncode(code);
            }

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var result = grammar.TokenizeLine(line, state, LineTimeLimit);
                state = result.RuleStack;

                foreach (var token in result.Tokens)
                {
                    var start = Math.Min(token.StartIndex, line.Length);
                    var end = Math.Min(token.EndIndex, line.Length);
                    if (end <= start)
                    {
                        continue;
                    }

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

                if (i < lines.Length - 1)
                {
                    html.Append('\n');
                }
            }
        }

        return html.ToString();
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

    private IGrammar? LoadGrammar(string languageId)
    {
        var scope = _options.GetScopeByLanguageId(languageId);
        return scope is null ? null : _registry.LoadGrammar(scope);
    }
}
