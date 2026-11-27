using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Weesite.Web.Agents;

/// <summary>One replacement of <c>edit_item</c> (T-0197): exact text, as get_item returns the file, and what replaces it.</summary>
internal sealed class ItemEdit
{
    /// <summary>Gets or sets the text to replace, which must appear in the file exactly once.</summary>
    [JsonPropertyName("old_text")]
    [Description("The text to replace, copied exactly from the file get_item returns (front matter included), spaces and line breaks too. It must appear exactly once: include enough of the text around it.")]
    public string? OldText { get; set; }

    /// <summary>Gets or sets the text that replaces it; empty removes it.</summary>
    [JsonPropertyName("new_text")]
    [Description("The text that replaces it; empty to remove it.")]
    public string? NewText { get; set; }
}

/// <summary>A file after <c>edit_item</c>'s replacements, or why they don't apply.</summary>
/// <param name="Text">The new file, when every edit applied.</param>
/// <param name="Error">What was wrong, naming the edit, when one didn't.</param>
internal sealed record ItemEditResult(string? Text, string? Error);

/// <summary>
/// Applies <c>edit_item</c>'s replacements to a whole file (T-0197), in the str_replace style: in
/// order, each to the text the ones before it left, all or none. Each old text must appear exactly
/// once, so an edit can't land in the wrong place, and line breaks are compared as LF.
/// </summary>
internal static class ItemEdits
{
    /// <summary>The most edits one call takes.</summary>
    public const int MaxEdits = 50;

    // The most lines an error lists where an old text appears.
    private const int ListedLines = 10;

    /// <summary>Applies the edits to the file.</summary>
    /// <param name="file">The file as get_item returns it.</param>
    /// <param name="edits">The edits, in order.</param>
    /// <returns>The new file, or the first edit that doesn't apply and why.</returns>
    public static ItemEditResult Apply(string file, IReadOnlyList<ItemEdit?>? edits)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (edits is not { Count: > 0 })
        {
            return new(null, "Give at least one edit: an old_text from the file and the new_text that replaces it.");
        }

        if (edits.Count > MaxEdits)
        {
            return new(null, $"Give at most {MaxEdits} edits in one call; this has {edits.Count}. Split them over several calls, or send the whole file with update_item.");
        }

        var text = Lf(file);
        for (var i = 0; i < edits.Count; i++)
        {
            var number = i + 1;
            var old = Lf(edits[i]?.OldText ?? "");
            if (old.Length == 0)
            {
                return new(null, $"Edit {number}: its old_text is empty. Give the text to replace, copied from get_item; to add text, put the text next to where it goes in both old_text and new_text.");
            }

            var at = Occurrences(text, old);
            switch (at.Count)
            {
                case 0:
                    return new(null, $"Edit {number}: its old_text isn't in the file{(i > 0 ? " as the edits before it left it" : "")}. Copy it exactly from get_item, spaces and line breaks included.");
                case > 1:
                    var lines = string.Join(", ", at.Take(ListedLines).Select(p => LineOf(text, p).ToString(CultureInfo.InvariantCulture)));
                    var more = at.Count > ListedLines ? ", …" : "";
                    return new(null, $"Edit {number}: its old_text appears {at.Count} times (lines {lines}{more}): include more of the text around it so it matches once.");
            }

            text = string.Concat(text.AsSpan(0, at[0]), Lf(edits[i]!.NewText ?? ""), text.AsSpan(at[0] + old.Length));
        }

        return new(text, null);
    }

    // Where the old text starts, overlapping matches included: "aa" is twice in "aaa", so an edit
    // never has to guess which one was meant.
    private static List<int> Occurrences(string text, string old)
    {
        var found = new List<int>();
        for (var at = text.IndexOf(old, StringComparison.Ordinal); at >= 0; at = at + 1 < text.Length ? text.IndexOf(old, at + 1, StringComparison.Ordinal) : -1)
        {
            found.Add(at);
        }

        return found;
    }

    // The 1-based line a position is on.
    private static int LineOf(string text, int position) => text.AsSpan(0, position).Count('\n') + 1;

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
