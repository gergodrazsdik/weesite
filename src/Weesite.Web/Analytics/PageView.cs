namespace Weesite.Web.Analytics;

/// <summary>Who asked for a page: a person's browser, a crawler or tool, or an AI agent.</summary>
internal enum ClientKind
{
    /// <summary>A web browser, so most likely a person.</summary>
    Browser,

    /// <summary>A search engine crawler, link preview, monitor or command-line tool.</summary>
    Bot,

    /// <summary>An AI crawler or assistant fetching the page.</summary>
    Agent,
}

/// <summary>
/// One page view (brief, Analytics): no IP address and no cookie. The visitor is a hash of the
/// address and browser with a salt that changes every day, so a visitor can be counted within a
/// day but not followed from one day to the next.
/// </summary>
/// <param name="At">When.</param>
/// <param name="Path">The page's path, without the query.</param>
/// <param name="ReferrerHost">The linking site's host, or null for direct visits and links within the site.</param>
/// <param name="Kind">Browser, bot or agent.</param>
/// <param name="Client">The bot's or agent's name (such as <c>Googlebot</c>), or null for browsers.</param>
/// <param name="Visitor">The day's visitor hash.</param>
internal sealed record PageView(DateTimeOffset At, string Path, string? ReferrerHost, ClientKind Kind, string? Client, string Visitor);
