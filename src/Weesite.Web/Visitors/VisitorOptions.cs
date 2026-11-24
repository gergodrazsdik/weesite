namespace Weesite.Web.Visitors;

/// <summary>
/// The limits on what visitors send (ADR 0011), from <c>Visitor:*</c>: <c>ContactPerHour</c> (5),
/// <c>CommentsPerHour</c> (5), <c>CommentChangesPerHour</c> (20), <c>ReactionsPerHour</c> (30), <c>ReactionsPerHourPerAddress</c> (120),
/// <c>MinimumSeconds</c> (3, how soon a
/// form may be sent after it was shown), <c>FormHours</c> (24, how long a shown form may be sent)
/// and <c>ProofMaxNumber</c> (100,000, how hard the proof of work is: the browser tries up to this
/// many hashes, half as many on average).
/// </summary>
/// <param name="ContactPerHour">Contact messages per hour per address.</param>
/// <param name="CommentsPerHour">Comments per hour per address.</param>
/// <param name="CommentChangesPerHour">Edits and deletions of their own comments per hour per address.</param>
/// <param name="ReactionsPerHour">Reactions per hour per address and browser.</param>
/// <param name="ReactionsPerHourPerAddress">Reactions per hour per address, whatever the browser
/// says it is: a script that changes its User-Agent stops here (F-0188).</param>
/// <param name="MinimumTime">How soon a form may be sent after it was shown.</param>
/// <param name="FormLifetime">How long a shown form may be sent.</param>
/// <param name="ProofMaxNumber">The largest number a challenge's answer can be.</param>
internal sealed record VisitorOptions(
    int ContactPerHour,
    int CommentsPerHour,
    int CommentChangesPerHour,
    int ReactionsPerHour,
    int ReactionsPerHourPerAddress,
    TimeSpan MinimumTime,
    TimeSpan FormLifetime,
    int ProofMaxNumber)
{
    /// <summary>Reads the options.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The options.</returns>
    public static VisitorOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new VisitorOptions(
            configuration.GetValue("Visitor:ContactPerHour", 5),
            configuration.GetValue("Visitor:CommentsPerHour", 5),
            configuration.GetValue("Visitor:CommentChangesPerHour", 20),
            configuration.GetValue("Visitor:ReactionsPerHour", 30),
            configuration.GetValue("Visitor:ReactionsPerHourPerAddress", 120),
            TimeSpan.FromSeconds(configuration.GetValue("Visitor:MinimumSeconds", 3.0)),
            TimeSpan.FromHours(configuration.GetValue("Visitor:FormHours", 24.0)),
            Math.Clamp(configuration.GetValue("Visitor:ProofMaxNumber", 100_000), 1, 10_000_000));
    }
}
