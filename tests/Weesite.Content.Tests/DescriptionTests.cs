namespace Weesite.Content.Tests;

/// <summary>An item's description for search engines and link previews (T-0119).</summary>
internal sealed class DescriptionTests
{
    // As the loader leaves it: the plain text is set when the catalog renders the item.
    private static ContentItem Item(string? summary, string plainText) =>
        new(ContentKind.Post, "a", "A", "en", plainText, "posts/a.md") { Summary = summary, PlainText = plainText };

    [Test]
    public async Task The_summary_wins()
    {
        var item = Item("The summary.", "The text.");

        await Assert.That(item.Description).IsEqualTo("The summary.");
    }

    [Test]
    public async Task Without_a_summary_a_short_text_is_the_description()
    {
        var item = Item(null, "A short post\nabout  one thing.");

        await Assert.That(item.Description).IsEqualTo("A short post about one thing.");
    }

    [Test]
    public async Task A_long_text_is_cut_at_a_word_within_160_characters()
    {
        var words = string.Join(' ', Enumerable.Range(1, 60).Select(i => $"word{i}"));
        var item = Item(null, words);

        var description = item.Description!;

        await Assert.That(description.Length).IsLessThanOrEqualTo(160);
        await Assert.That(description).EndsWith("…");
        await Assert.That(words.StartsWith(description[..^1], StringComparison.Ordinal)).IsTrue();
        await Assert.That(description[..^1]).DoesNotEndWith(" ");
        await Assert.That(words[description.Length - 1]).IsEqualTo(' ');
    }

    [Test]
    public async Task An_item_with_neither_has_none()
    {
        var item = Item(null, " ");

        await Assert.That(item.Description).IsNull();
    }
}
