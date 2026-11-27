using Weesite.Web.Agents;

namespace Weesite.Web.Tests;

/// <summary>edit_item's replacements (T-0197): exact, in order, all or none.</summary>
internal sealed class ItemEditsTests
{
    private const string File = "---\ntitle: A walk\n---\n\nThe harbour at dusk.\nThe harbour at dawn.\nBoats.\n";

    [Test]
    public async Task Edits_apply_in_order_each_to_the_text_the_ones_before_left()
    {
        var result = ItemEdits.Apply(File, [Edit("at dusk", "in the evening"), Edit("in the evening.\nThe", "in the evening.\nAnd the"), Edit("Boats.\n", "")]);

        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Text).IsEqualTo("---\ntitle: A walk\n---\n\nThe harbour in the evening.\nAnd the harbour at dawn.\n");
    }

    [Test]
    public async Task An_edit_whose_text_isnt_there_stops_them_all()
    {
        var result = ItemEdits.Apply(File, [Edit("Boats.", "Ships."), Edit("the harbour", "the port")]);

        await Assert.That(result.Text).IsNull();
        await Assert.That(result.Error).IsEqualTo("Edit 2: its old_text isn't in the file as the edits before it left it. Copy it exactly from get_item, spaces and line breaks included.");
    }

    [Test]
    public async Task An_edit_whose_text_appears_several_times_names_their_lines()
    {
        var result = ItemEdits.Apply(File, [Edit("The harbour", "A harbour")]);

        await Assert.That(result.Text).IsNull();
        await Assert.That(result.Error).IsEqualTo("Edit 1: its old_text appears 2 times (lines 5, 6): include more of the text around it so it matches once.");
    }

    [Test]
    public async Task Overlapping_matches_count_too()
    {
        var result = ItemEdits.Apply("aaa", [Edit("aa", "b")]);

        await Assert.That(result.Error).Contains("appears 2 times (lines 1, 1)");
    }

    [Test]
    public async Task Windows_line_breaks_match_the_files()
    {
        var result = ItemEdits.Apply(File, [Edit("at dusk.\r\nThe harbour at dawn.", "at dusk.\r\nThe harbour at dawn, too.")]);

        await Assert.That(result.Text).IsEqualTo(File.Replace("at dawn.", "at dawn, too.", StringComparison.Ordinal));
        await Assert.That(result.Text).DoesNotContain("\r");
    }

    [Test]
    public async Task An_empty_old_text_is_refused()
    {
        var empty = ItemEdits.Apply(File, [Edit("Boats.", "Ships."), Edit("", "More.")]);
        var missing = ItemEdits.Apply(File, [new ItemEdit { NewText = "More." }]);

        await Assert.That(empty.Error).StartsWith("Edit 2: its old_text is empty.");
        await Assert.That(missing.Error).StartsWith("Edit 1: its old_text is empty.");
    }

    [Test]
    public async Task One_to_fifty_edits_are_taken()
    {
        var none = ItemEdits.Apply(File, []);
        var nothing = ItemEdits.Apply(File, null);
        var fifty = ItemEdits.Apply(string.Concat(Enumerable.Range(0, 50).Select(i => $"<{i}>")), [.. Enumerable.Range(0, 50).Select(i => Edit($"<{i}>", ""))]);
        var tooMany = ItemEdits.Apply(File, [.. Enumerable.Range(0, 51).Select(_ => Edit("Boats.", "Boats."))]);

        await Assert.That(none.Error).StartsWith("Give at least one edit");
        await Assert.That(nothing.Error).StartsWith("Give at least one edit");
        await Assert.That(fifty.Text).IsEqualTo("");
        await Assert.That(tooMany.Error).IsEqualTo("Give at most 50 edits in one call; this has 51. Split them over several calls, or send the whole file with update_item.");
    }

    [Test]
    public async Task A_missing_new_text_removes_the_old_one()
    {
        var result = ItemEdits.Apply(File, [new ItemEdit { OldText = "Boats.\n" }]);

        await Assert.That(result.Text).IsEqualTo(File.Replace("Boats.\n", "", StringComparison.Ordinal));
    }

    private static ItemEdit Edit(string oldText, string newText) => new() { OldText = oldText, NewText = newText };
}
