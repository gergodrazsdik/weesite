using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Azure.Core;

namespace Weesite.Web;

/// <summary>
/// Times the app's start (F-0107): how long after the process began the host was built, and when
/// the app was ready, after the content load and the search index (hosted services). Also what
/// ran beside it (ADR 0012): each scope's first token and the highlighter's warm-up. The Key Vault
/// secrets load in the background and log their own time (T-0192). Logged once the app has
/// started, so a cold start can be read from the app's log.
/// </summary>
internal static partial class StartupTimes
{
    private static readonly ConcurrentDictionary<string, string> Tokens = new(StringComparer.Ordinal);

    /// <summary>Gets the time since the process began, .NET's own start included.</summary>
    public static TimeSpan SinceProcessStart
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return DateTime.Now - process.StartTime;
        }
    }

    /// <summary>Gets what each first token took, as the log line says it (tests).</summary>
    internal static IReadOnlyList<string> TokenTimes => [.. Tokens.Values.Order(StringComparer.Ordinal)];

    /// <summary>Gets or sets the highlighter's warm-up, started at the beginning of <c>Program</c>.</summary>
    public static Task<TimeSpan>? HighlighterWarmUp { get; set; }

    /// <summary>
    /// Asks for each scope's first token at once, in the background, so the clients that need them
    /// find them in the credential's cache, and records how long each took. A failure is only
    /// recorded: the client asks again itself, and says why if it fails too.
    /// </summary>
    /// <param name="credential">The credential the clients share.</param>
    /// <param name="scopes">The scopes, such as <c>https://vault.azure.net/.default</c>.</param>
    public static void AskForTokens(TokenCredential credential, IEnumerable<string> scopes)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(scopes);
        var askedAt = (long)SinceProcessStart.TotalMilliseconds;
        foreach (var scope in scopes)
        {
            var host = Uri.TryCreate(scope, UriKind.Absolute, out var uri) ? uri.Host : scope;
            _ = Task.Run(async () =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await credential.GetTokenAsync(new TokenRequestContext([scope]), CancellationToken.None);
                    Tokens[host] = string.Create(CultureInfo.InvariantCulture, $"{host} {(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds} ms (asked at {askedAt} ms)");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Tokens[host] = string.Create(CultureInfo.InvariantCulture, $"{host} failed after {(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds} ms ({ex.GetType().Name})");
                }
            });
        }
    }

    /// <summary>Logs the times once the app has started.</summary>
    /// <param name="app">The app, just built.</param>
    public static void LogWhenStarted(WebApplication app)
    {
        var built = SinceProcessStart;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.Startup");
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var started = SinceProcessStart;
            LogStarted(logger, (long)started.TotalMilliseconds, (long)built.TotalMilliseconds);
            var warmUp = HighlighterWarmUp is { IsCompletedSuccessfully: true } done
                ? string.Create(CultureInfo.InvariantCulture, $"{(long)done.Result.TotalMilliseconds} ms")
                : "not finished";
            var tokens = Tokens.IsEmpty ? "none asked for, or none back yet" : string.Join(", ", Tokens.Values.Order(StringComparer.Ordinal));
            LogBeside(logger, tokens, warmUp);
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started {Started} ms after the process began: the host built at {Built} ms; content and the search index the rest")]
    private static partial void LogStarted(ILogger logger, long started, long built);

    [LoggerMessage(Level = LogLevel.Information, Message = "Beside the start: first tokens {Tokens}; highlighter warm-up {WarmUp}")]
    private static partial void LogBeside(ILogger logger, string tokens, string warmUp);
}
