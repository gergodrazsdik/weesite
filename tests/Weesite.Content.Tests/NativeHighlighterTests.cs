using Weesite.Content.Markdown;

namespace Weesite.Content.Tests;

/// <summary>The native highlighter's interface from .NET (ADR 0012, T-0189): the Rust library in native/highlight.</summary>
internal sealed class NativeHighlighterTests
{
    private static readonly TimeSpan Enough = TimeSpan.FromSeconds(5);

    [Test]
    public async Task The_library_is_next_to_the_app_and_loads()
    {
        await Assert.That(NativeHighlighter.IsAvailable).IsTrue();
    }

    [Test]
    public async Task Code_comes_back_coloured_with_weesites_classes_and_escaped()
    {
        var result = NativeHighlighter.Highlight("// a <note>\r\nvar name = \"Ada\";", "csharp", Enough, 1_000);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Html).Contains("<span class=\"hl-comment\">// a &lt;note&gt;</span>");
        await Assert.That(result.Html).Contains("<span class=\"hl-keyword\">var</span>");
        await Assert.That(result.Html).Contains("hl-string");
        await Assert.That(result.Html).DoesNotContain("\r");
        await Assert.That(result.Stop).IsEqualTo(HighlightStop.None);
    }

    [Test]
    [Arguments("cs")]
    [Arguments("c#")]
    [Arguments("CSharp")]
    public async Task Fence_names_resolve_ignoring_case(string language)
    {
        await Assert.That(NativeHighlighter.Highlight("class A {}", language, Enough, 1_000)).IsNotNull();
    }

    [Test]
    public async Task A_language_without_a_syntax_gives_nothing()
    {
        await Assert.That(NativeHighlighter.Highlight("anything", "no-such-language", Enough, 1_000)).IsNull();
    }

    [Test]
    public async Task Without_time_or_with_a_line_too_long_the_rest_stays_plain()
    {
        var noTime = NativeHighlighter.Highlight("var a = 1;", "cs", TimeSpan.Zero, 1_000)!;
        var tooLong = NativeHighlighter.Highlight("var a = 1;\n" + new string('x', 50) + "\nvar b = 2;", "cs", Enough, 20)!;

        await Assert.That(noTime.Stop).IsEqualTo(HighlightStop.OutOfTime);
        await Assert.That(noTime.Html).IsEqualTo("var a = 1;");
        await Assert.That(tooLong.Stop).IsEqualTo(HighlightStop.TooLong);
        await Assert.That(tooLong.Html).EndsWith(new string('x', 50) + "\nvar b = 2;");
    }

    [Test]
    public async Task The_languages_come_with_their_fence_names()
    {
        var languages = NativeHighlighter.Languages();

        var csharp = languages.Single(l => l.Name == "C#");
        await Assert.That(csharp.FenceNames).Contains("cs");
        await Assert.That(csharp.FenceNames).Contains("csharp");
        await Assert.That(languages.Count).IsGreaterThan(100);
    }

    [Test]
    public async Task Many_threads_at_once_get_the_same_answer()
    {
        const string Code = "for (var i = 0; i < 10; i++) { Console.WriteLine(i); }";
        var expected = NativeHighlighter.Highlight(Code, "cs", Enough, 1_000)!.Html;

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => NativeHighlighter.Highlight(Code, "cs", Enough, 1_000)!.Html)));

        await Assert.That(results.Distinct().ToList()).IsEquivalentTo([expected]);
    }
}
