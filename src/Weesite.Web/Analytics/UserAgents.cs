namespace Weesite.Web.Analytics;

/// <summary>
/// Labels a request's User-Agent as a browser, a bot or an AI agent (brief, Analytics). Known
/// agents and bots are matched by name; anything else that calls itself a bot, crawler or spider,
/// or a command-line HTTP client, is a bot; a browser has to say "Mozilla/". A missing User-Agent
/// is a bot.
/// </summary>
internal static class UserAgents
{
    // AI crawlers and assistants, by the names their operators publish. Checked first: several
    // also contain "bot".
    private static readonly string[] Agents =
    [
        "GPTBot", "ChatGPT-User", "OAI-SearchBot",
        "ClaudeBot", "Claude-User", "Claude-SearchBot", "anthropic-ai",
        "PerplexityBot", "Perplexity-User",
        "Google-CloudVertexBot", "Gemini-Deep-Research",
        "meta-externalagent", "meta-externalfetcher",
        "MistralAI-User", "cohere-ai", "DuckAssistBot", "Amazonbot", "Applebot-Extended",
        "Bytespider", "CCBot", "YouBot", "PetalBot",
    ];

    // Well-known crawlers, link previews and tools, named so the dashboard can show them.
    private static readonly string[] Bots =
    [
        "Googlebot", "bingbot", "DuckDuckBot", "YandexBot", "Baiduspider", "Applebot", "SeznamBot",
        "facebookexternalhit", "Twitterbot", "LinkedInBot", "Slackbot", "Discordbot", "TelegramBot",
        "WhatsApp", "Mastodon", "Bluesky", "AhrefsBot", "SemrushBot", "MJ12bot", "DotBot", "UptimeRobot",
        "HeadlessChrome", "Lighthouse", "curl", "Wget", "python-requests", "python-httpx", "aiohttp",
        "Go-http-client", "okhttp", "node-fetch", "axios", "Java", "libwww-perl", "PostmanRuntime",
    ];

    private static readonly string[] BotWords = ["bot", "crawler", "spider", "scraper", "fetch", "http client"];

    /// <summary>Labels a User-Agent.</summary>
    /// <param name="userAgent">The header's value, or null.</param>
    /// <returns>The kind, and the bot's or agent's name (null for browsers and unnamed bots).</returns>
    public static (ClientKind Kind, string? Name) Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return (ClientKind.Bot, null);
        }

        if (Find(Agents, userAgent) is { } agent)
        {
            return (ClientKind.Agent, agent);
        }

        if (Find(Bots, userAgent) is { } bot)
        {
            return (ClientKind.Bot, bot);
        }

        if (BotWords.Any(w => userAgent.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return (ClientKind.Bot, null);
        }

        return userAgent.StartsWith("Mozilla/", StringComparison.Ordinal) ? (ClientKind.Browser, null) : (ClientKind.Bot, null);
    }

    private static string? Find(string[] names, string userAgent) =>
        names.FirstOrDefault(n => userAgent.Contains(n, StringComparison.OrdinalIgnoreCase));
}
