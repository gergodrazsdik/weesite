using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.HttpOverrides;

namespace Weesite.Web.Analytics;

/// <summary>
/// Counts page views (brief, Analytics): after each response, a successful GET of a public page
/// (HTML, or Markdown for agents, or <c>llms.txt</c>) is queued for <see cref="PageViewWriter"/>,
/// so the request never waits for storage. Not counted: <c>/admin</c>, health checks, assets,
/// feeds, media, errors and redirects, and signed-in editors (they'd count their own work).
/// </summary>
internal sealed class PageViewRecorder(IPageViewStore store, TimeProvider time)
{
    private const string EditorCookie = "weesite.auth";

    private static readonly string[] SkippedPrefixes =
        ["/admin", "/health", "/alive", "/mcp", "/theme", "/media", "/og", "/fonts", "/icons", "/images"];

    private readonly Channel<PageView> _queue = Channel.CreateBounded<PageView>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    // The day's salt, cached; swapped as a whole, so concurrent requests see a matching pair.
    private DaySalt? _salt;

    /// <summary>Gets the queued views, for the writer.</summary>
    public ChannelReader<PageView> Queue => _queue.Reader;

    /// <summary>Whether a finished response counts as a page view.</summary>
    /// <param name="context">The request and its response.</param>
    /// <returns>True to count it.</returns>
    public static bool Counts(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || context.Response.StatusCode != StatusCodes.Status200OK
            || request.Cookies.ContainsKey(EditorCookie)
            || SkippedPrefixes.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var type = context.Response.ContentType ?? "";
        return type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase)
            || request.Path.Equals("/llms.txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Queues a view of the request's page; drops it if the queue is full.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task.</returns>
    public async Task RecordAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        var now = time.GetUtcNow();
        var userAgent = request.Headers.UserAgent.ToString();
        var (kind, client) = UserAgents.Classify(userAgent);
        var salt = await SaltAsync(DateOnly.FromDateTime(now.UtcDateTime), context.RequestAborted);
        var view = new PageView(now, request.Path.Value ?? "/", ReferrerHost(request), kind, client, Visitor(salt, ClientAddress(context), userAgent));
        _queue.Writer.TryWrite(view);
    }

    /// <summary>The visitor hash: the day's salt, the address and the User-Agent, hashed.</summary>
    /// <param name="salt">The day's salt.</param>
    /// <param name="address">The client's address.</param>
    /// <param name="userAgent">The User-Agent.</param>
    /// <returns>16 hex digits.</returns>
    public static string Visitor(byte[] salt, string address, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(salt);
        var data = Encoding.UTF8.GetBytes($"{address}\n{userAgent}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(salt, data).AsSpan(0, 8));
    }

    // The linking site's host; null for direct visits and links within the site.
    private static string? ReferrerHost(HttpRequest request) =>
        Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var referrer)
            && referrer.Scheme is "http" or "https"
            && !string.Equals(referrer.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            ? referrer.Host
            : null;

    // Behind the Container Apps ingress the connection comes from the proxy, which appends the
    // client's address to X-Forwarded-For: the last entry is the one it added (earlier ones come
    // from the client and can be anything). Used only for the hash, never stored.
    private static string ClientAddress(HttpContext context)
    {
        var forwarded = context.Request.Headers[ForwardedHeadersDefaults.XForwardedForHeaderName].ToString();
        var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return last ?? context.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    private async Task<byte[]> SaltAsync(DateOnly day, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _salt) is { } cached && cached.Day == day)
        {
            return cached.Salt;
        }

        var salt = await store.SaltAsync(day, cancellationToken);
        Volatile.Write(ref _salt, new DaySalt(day, salt));
        return salt;
    }

    private sealed record DaySalt(DateOnly Day, byte[] Salt);
}
