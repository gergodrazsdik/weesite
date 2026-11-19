using Microsoft.AspNetCore.Http.HttpResults;

namespace Weesite.Web.Visitors;

/// <summary>Wires up what visitors send: the shared layers against spam (ADR 0011).</summary>
internal static class VisitorExtensions
{
    /// <summary>
    /// Adds the address hashes, the counters (Table Storage, or memory with content from files),
    /// the proof of work, the form guard, and the daily clean-up of old counters.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The configuration, for <see cref="VisitorOptions"/>.</param>
    /// <param name="inMemory">Whether to keep the counters in memory (content from files).</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddVisitorInput(this IServiceCollection services, IConfiguration configuration, bool inMemory)
    {
        if (inMemory)
        {
            services.AddSingleton<MemoryVisitorStore>();
            services.AddSingleton<IVisitorStore>(s => s.GetRequiredService<MemoryVisitorStore>());
        }
        else
        {
            services.AddSingleton<IVisitorStore, TableVisitorStore>();
        }

        services.AddSingleton(VisitorOptions.Read(configuration));
        services.AddSingleton<ClientHashes>();
        services.AddSingleton<ProofOfWork>();
        services.AddSingleton<FormGuard>();
        services.AddHostedService<VisitorCleanUp>();
        return services;
    }

    /// <summary>
    /// Maps <c>GET /visitor/challenge?for=contact|comment|reaction</c>: a fresh proof-of-work
    /// challenge, never cached.
    /// </summary>
    /// <param name="app">The app.</param>
    /// <returns>The app.</returns>
    public static IEndpointRouteBuilder MapVisitorChallenge(this IEndpointRouteBuilder app)
    {
        app.MapGet("/visitor/challenge", Results<Ok<ProofChallenge>, BadRequest<string>> (HttpContext context, ProofOfWork proof, string? @for) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return @for is { } purpose && ProofOfWork.Purposes.Contains(purpose)
                ? TypedResults.Ok(proof.Create(purpose))
                : TypedResults.BadRequest("Ask for a challenge for contact, comment or reaction.");
        });
        return app;
    }
}

/// <summary>Deletes the counters and used challenges of days before yesterday, once a day.</summary>
/// <param name="store">The counters.</param>
/// <param name="time">The clock.</param>
/// <param name="logger">The logger.</param>
internal sealed partial class VisitorCleanUp(IVisitorStore store, TimeProvider time, ILogger<VisitorCleanUp> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    /// <summary>Deletes what's older than yesterday.</summary>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>How many rows went.</returns>
    public Task<int> CleanUpAsync(CancellationToken cancellationToken) =>
        store.CleanUpAsync(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1), cancellationToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Not at startup: the app has more urgent things to do then.
            await Task.Delay(TimeSpan.FromMinutes(5), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deleted = await CleanUpAsync(stoppingToken);
                    LogCleanedUp(logger, deleted);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogCleanUpFailed(logger, e);
                }

                await Task.Delay(Every, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Visitor limits: deleted {Count} old counters and challenges")]
    private static partial void LogCleanedUp(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Visitor limits: the clean-up failed; it runs again tomorrow")]
    private static partial void LogCleanUpFailed(ILogger logger, Exception exception);
}
