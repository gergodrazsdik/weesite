namespace Weesite.Web.Visitors;

/// <summary>A reaction to a post (ADR 0011): a fixed set, each with an emoji and a translated label.</summary>
internal enum ReactionKind
{
    /// <summary>👍</summary>
    Like,

    /// <summary>❤️</summary>
    Love,

    /// <summary>🙏</summary>
    Thanks,

    /// <summary>💡</summary>
    Insightful,

    /// <summary>😄</summary>
    Funny,
}

/// <summary>The reactions' names, emoji and resource keys.</summary>
internal static class Reactions
{
    /// <summary>Gets every reaction, in the order the buttons show them.</summary>
    public static IReadOnlyList<ReactionKind> All { get; } = [ReactionKind.Like, ReactionKind.Love, ReactionKind.Thanks, ReactionKind.Insightful, ReactionKind.Funny];

    /// <summary>The name in URLs, JSON and storage, such as <c>like</c>.</summary>
    /// <param name="kind">The reaction.</param>
    /// <returns>The name.</returns>
    public static string Name(ReactionKind kind) => kind switch
    {
        ReactionKind.Like => "like",
        ReactionKind.Love => "love",
        ReactionKind.Thanks => "thanks",
        ReactionKind.Insightful => "insightful",
        ReactionKind.Funny => "funny",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The emoji on the button.</summary>
    /// <param name="kind">The reaction.</param>
    /// <returns>The emoji.</returns>
    public static string Emoji(ReactionKind kind) => kind switch
    {
        ReactionKind.Like => "👍",
        ReactionKind.Love => "❤️",
        ReactionKind.Thanks => "🙏",
        ReactionKind.Insightful => "💡",
        ReactionKind.Funny => "😄",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The label's resource key, the same in the public and the admin resources.</summary>
    /// <param name="kind">The reaction.</param>
    /// <returns>Such as <c>ReactionLike</c>.</returns>
    public static string LabelKey(ReactionKind kind) => "Reaction" + kind;

    /// <summary>Reads a name.</summary>
    /// <param name="name">Such as <c>like</c>.</param>
    /// <returns>The reaction, or null for an unknown name.</returns>
    public static ReactionKind? Parse(string? name) =>
        All.Select(k => (ReactionKind?)k).FirstOrDefault(k => string.Equals(Name(k!.Value), name, StringComparison.Ordinal));
}

/// <summary>A post's reaction counts.</summary>
/// <param name="Like">👍</param>
/// <param name="Love">❤️</param>
/// <param name="Thanks">🙏</param>
/// <param name="Insightful">💡</param>
/// <param name="Funny">😄</param>
internal sealed record ReactionCounts(int Like, int Love, int Thanks, int Insightful, int Funny)
{
    /// <summary>Gets the counts of a post without reactions.</summary>
    public static ReactionCounts None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Gets every count added up.</summary>
    public int Total => Like + Love + Thanks + Insightful + Funny;

    /// <summary>Gets one reaction's count.</summary>
    /// <param name="kind">The reaction.</param>
    /// <returns>Its count.</returns>
    public int Of(ReactionKind kind) => kind switch
    {
        ReactionKind.Like => Like,
        ReactionKind.Love => Love,
        ReactionKind.Thanks => Thanks,
        ReactionKind.Insightful => Insightful,
        ReactionKind.Funny => Funny,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Changes one count, never below zero.</summary>
    /// <param name="kind">The reaction.</param>
    /// <param name="delta">+1 or -1.</param>
    /// <returns>The new counts.</returns>
    public ReactionCounts Add(ReactionKind kind, int delta)
    {
        var value = Math.Max(0, Of(kind) + delta);
        return kind switch
        {
            ReactionKind.Like => this with { Like = value },
            ReactionKind.Love => this with { Love = value },
            ReactionKind.Thanks => this with { Thanks = value },
            ReactionKind.Insightful => this with { Insightful = value },
            ReactionKind.Funny => this with { Funny = value },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>The counts by name, as the endpoints answer them.</summary>
    /// <returns>Such as <c>{ "like": 3, "love": 0, … }</c>.</returns>
    public IReadOnlyDictionary<string, int> ByName() => Reactions.All.ToDictionary(Reactions.Name, Of, StringComparer.Ordinal);
}

/// <summary>A reaction given or taken back, for the analytics: no hash, nothing about who.</summary>
/// <param name="At">When.</param>
/// <param name="Post">The post's source path, such as <c>posts/hello-world.md</c>.</param>
/// <param name="Kind">The reaction.</param>
/// <param name="Delta">+1 for given, -1 for taken back.</param>
internal sealed record ReactionEvent(DateTimeOffset At, string Post, ReactionKind Kind, int Delta);
