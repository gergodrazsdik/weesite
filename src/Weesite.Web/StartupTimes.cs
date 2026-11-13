using System.Diagnostics;

namespace Weesite.Web;

/// <summary>
/// Times the app's start (F-0107): how long after the process began the host was built, how much
/// of that the Key Vault secrets took, and when the app was ready, after the content load and the
/// search index (hosted services). One log line, so a cold start can be read from the app's log.
/// </summary>
internal static partial class StartupTimes
{
    /// <summary>Gets the time since the process began, .NET's own start included.</summary>
    public static TimeSpan SinceProcessStart
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return DateTime.Now - process.StartTime;
        }
    }

    /// <summary>Gets or sets how long loading the Key Vault secrets took.</summary>
    public static TimeSpan KeyVault { get; set; }

    /// <summary>Logs the times once the app has started.</summary>
    /// <param name="app">The app, just built.</param>
    public static void LogWhenStarted(WebApplication app)
    {
        var built = SinceProcessStart;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.Startup");
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var started = SinceProcessStart;
            LogStarted(logger, (long)started.TotalMilliseconds, (long)built.TotalMilliseconds, (long)KeyVault.TotalMilliseconds);
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started {Started} ms after the process began: the host built at {Built} ms, of which the Key Vault secrets {KeyVault} ms; content and the search index the rest")]
    private static partial void LogStarted(ILogger logger, long started, long built, long keyVault);
}
