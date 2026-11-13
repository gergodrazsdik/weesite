namespace Weesite.Content.Tests;

/// <summary>Slugs and tags in any script (F-0091 L1).</summary>
internal sealed class SlugTests
{
    [Test]
    [Arguments("hello-world", true)]
    [Arguments("ékezet", true)]
    [Arguments("első-bejegyzés", true)]
    [Arguments("日本語のタイトル", true)]
    [Arguments("привет-мир", true)]
    [Arguments("v2-notes", true)]
    [Arguments("Ékezet", false)]
    [Arguments("ékezet", false)]
    [Arguments("two words", false)]
    [Arguments("double--hyphen", false)]
    [Arguments("-leading", false)]
    [Arguments("dot.ted", false)]
    [Arguments("", false)]
    public async Task Lowercase_letters_of_any_script_digits_and_single_hyphens_in_composed_form(string slug, bool valid) =>
        await Assert.That(Slugs.IsValid(slug)).IsEqualTo(valid);
}
