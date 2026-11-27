using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Weesite.Content.Markdown;

/// <summary>Why the native highlighter stopped colouring a block before its end.</summary>
internal enum HighlightStop
{
    /// <summary>It didn't: every line is coloured.</summary>
    None = 0,

    /// <summary>A line was longer than the limit; it and the rest stay plain.</summary>
    TooLong = 1,

    /// <summary>The time the block was given ran out; the rest stays plain.</summary>
    OutOfTime = 2,

    /// <summary>The syntax failed on a line; it and the rest stay plain.</summary>
    Failed = 3,
}

/// <summary>A block the native highlighter coloured.</summary>
/// <param name="Html">The HTML, for inside <c>&lt;code&gt;</c>.</param>
/// <param name="Spent">How long it took.</param>
/// <param name="Stop">Why it stopped before the end, if it did.</param>
internal sealed record NativeHighlight(string Html, TimeSpan Spent, HighlightStop Stop);

/// <summary>
/// The native code highlighter (ADR 0012): syntect with bat's Sublime syntaxes, in the Rust library
/// built from <c>native/highlight</c> and copied next to the app. Its syntaxes load once per process,
/// on the first call, and stay; it's safe to call from several threads at once. When the library
/// isn't there (a build without Rust), <see cref="IsAvailable"/> is false, and the other members
/// throw.
/// </summary>
internal static partial class NativeHighlighter
{
    private const string Library = "weesite_highlight";

    private static readonly string FileName =
        OperatingSystem.IsWindows() ? "weesite_highlight.dll"
        : OperatingSystem.IsMacOS() ? "libweesite_highlight.dylib"
        : "libweesite_highlight.so";

    // Loaded from this assembly's own folder by its full path, and only from there: no other file
    // of that name anywhere on the search path can stand in for it. The resolver hands the same
    // library to the P/Invokes below.
    private static readonly Lazy<nint> Handle = new(() =>
    {
        var assembly = typeof(NativeHighlighter).Assembly;
        var folder = Path.GetDirectoryName(assembly.Location);
        var path = Path.Combine(string.IsNullOrEmpty(folder) ? AppContext.BaseDirectory : folder, FileName);
        if (!NativeLibrary.TryLoad(path, out var handle))
        {
            return 0;
        }

        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == Library ? handle : 0);
        return handle;
    });

    /// <summary>Gets a value indicating whether the library is there and loads.</summary>
    public static bool IsAvailable => Handle.Value != 0;

    /// <summary>Colours a block of code.</summary>
    /// <param name="code">The code; lines end in <c>\n</c> or <c>\r\n</c>.</param>
    /// <param name="language">The fence's language, such as <c>csharp</c> or <c>sh</c>.</param>
    /// <param name="budget">The time it may take, checked between lines.</param>
    /// <param name="maxLineCharacters">The longest line it colours; a longer one, and the rest, stay plain.</param>
    /// <returns>The block, or null when no syntax answers to the language.</returns>
    /// <exception cref="InvalidOperationException">The library failed.</exception>
    public static NativeHighlight? Highlight(string code, string language, TimeSpan budget, int maxLineCharacters)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(language);
        EnsureLoaded();
        var languageBytes = Encoding.UTF8.GetBytes(language);
        var codeBytes = Encoding.UTF8.GetBytes(code);
        var micros = budget <= TimeSpan.Zero ? 0UL : (ulong)(budget.Ticks / TimeSpan.TicksPerMicrosecond);
        var status = NativeMethods.Highlight(languageBytes, (nuint)languageBytes.Length, codeBytes, (nuint)codeBytes.Length, micros, (uint)Math.Max(0, maxLineCharacters), out var result);
        switch (status)
        {
            case 0:
                try
                {
                    var html = Marshal.PtrToStringUTF8(result.Html, checked((int)result.HtmlLength));
                    return new NativeHighlight(html, TimeSpan.FromMicroseconds(result.SpentMicros), (HighlightStop)result.Stop);
                }
                finally
                {
                    NativeMethods.Free(result.Html, result.HtmlLength);
                }

            case 1:
                return null;
            default:
                throw new InvalidOperationException($"The code highlighter failed ({status}).");
        }
    }

    /// <summary>Gets the languages it has a syntax for, by name, each with the fence names that pick it.</summary>
    /// <returns>The languages, sorted by name.</returns>
    /// <exception cref="InvalidOperationException">The library failed.</exception>
    public static IReadOnlyList<CodeLanguage> Languages()
    {
        EnsureLoaded();
        var status = NativeMethods.Languages(out var json, out var length);
        if (status != 0)
        {
            throw new InvalidOperationException($"The code highlighter failed to list its languages ({status}).");
        }

        try
        {
            var text = Marshal.PtrToStringUTF8(json, checked((int)length));
            return [.. JsonSerializer.Deserialize<List<Language>>(text, JsonOptions)!.Select(l => new CodeLanguage(l.Name, l.Fences))];
        }
        finally
        {
            NativeMethods.Free(json, length);
        }
    }

    private static void EnsureLoaded()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException($"The code highlighter's library ({FileName}) isn't next to the app.");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record Language(string Name, string[] Fences);

    // The C interface (native/highlight/src/lib.rs).
    private static partial class NativeMethods
    {
        [LibraryImport(Library, EntryPoint = "weesite_highlight")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Highlight(byte[] language, nuint languageLength, byte[] code, nuint codeLength, ulong budgetMicros, uint maxLineCharacters, out Result result);

        [LibraryImport(Library, EntryPoint = "weesite_highlight_languages")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int Languages(out nint json, out nuint length);

        [LibraryImport(Library, EntryPoint = "weesite_highlight_free")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial void Free(nint text, nuint length);
    }

    // HighlightResult in the library: the same fields, in the same order.
    [StructLayout(LayoutKind.Sequential)]
    private struct Result
    {
        public nint Html;
        public nuint HtmlLength;
        public ulong SpentMicros;
        public uint Stop;
    }
}
