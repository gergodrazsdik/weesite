namespace Weesite.Web.Admin;

/// <summary>A line of a diff: unchanged, removed (only in the old text) or added (only in the new).</summary>
/// <param name="Kind">What happened to the line.</param>
/// <param name="Text">The line.</param>
internal sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>What happened to a line.</summary>
internal enum DiffKind
{
    /// <summary>In both texts.</summary>
    Same,

    /// <summary>Only in the old text.</summary>
    Removed,

    /// <summary>Only in the new text.</summary>
    Added,
}

/// <summary>A line diff of two texts (longest common subsequence), for comparing a post's versions.</summary>
internal static class LineDiff
{
    /// <summary>The most lines either text may have; longer texts aren't compared.</summary>
    public const int MaxLines = 2000;

    /// <summary>Compares two texts line by line.</summary>
    /// <param name="before">The old text.</param>
    /// <param name="after">The new text.</param>
    /// <returns>The lines in order, or null when a text is longer than <see cref="MaxLines"/>.</returns>
    public static IReadOnlyList<DiffLine>? Compare(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var a = Lines(before);
        var b = Lines(after);
        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            return null;
        }

        // lengths[i][j]: the longest common subsequence of a[i..] and b[j..].
        var lengths = new int[a.Length + 1][];
        for (var i = 0; i <= a.Length; i++)
        {
            lengths[i] = new int[b.Length + 1];
        }

        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lengths[i][j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? lengths[i + 1][j + 1] + 1
                    : Math.Max(lengths[i + 1][j], lengths[i][j + 1]);
            }
        }

        var lines = new List<DiffLine>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (string.Equals(a[x], b[y], StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffKind.Same, a[x++]));
                y++;
            }
            else if (lengths[x + 1][y] >= lengths[x][y + 1])
            {
                lines.Add(new DiffLine(DiffKind.Removed, a[x++]));
            }
            else
            {
                lines.Add(new DiffLine(DiffKind.Added, b[y++]));
            }
        }

        lines.AddRange(a[x..].Select(l => new DiffLine(DiffKind.Removed, l)));
        lines.AddRange(b[y..].Select(l => new DiffLine(DiffKind.Added, l)));
        return lines;
    }

    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
}
