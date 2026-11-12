namespace Weesite.Web.Agents.OAuth;

/// <summary>What one clean-up deleted.</summary>
/// <param name="Codes">Codes that expired unused.</param>
/// <param name="Grants">Grants whose refresh token expired.</param>
/// <param name="Clients">Registered clients nobody uses.</param>
internal sealed record OAuthCleanUpResult(int Codes, int Grants, int Clients);

/// <summary>
/// Deletes what the authorization server no longer needs, a minute after startup and then once a
/// day (F-0060). Anyone can register a client, so a registration that never got a grant goes after
/// a day, and one whose grants were all revoked or expired goes a month after the last one ended
/// (maintainer, 2026-11-12); the app then registers again, or is added again. Codes that expired
/// unused and grants whose refresh token expired go too. Every replica runs it; each step is safe
/// to repeat.
/// </summary>
internal sealed partial class OAuthCleanUp(IOAuthStore store, TimeProvider time, ILogger<OAuthCleanUp> logger) : BackgroundService
{
    /// <summary>How long a registration may wait for its first grant.</summary>
    public static readonly TimeSpan NeverGrantedFor = TimeSpan.FromDays(1);

    /// <summary>How long a registration is kept after its last grant ended.</summary>
    public static readonly TimeSpan IdleFor = TimeSpan.FromDays(30);

    private static readonly TimeSpan FirstAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Every = TimeSpan.FromHours(24);

    /// <summary>Deletes expired codes and grants, and the registrations nobody uses.</summary>
    /// <param name="cancellationToken">Cancels the clean-up.</param>
    /// <returns>What it deleted.</returns>
    public async Task<OAuthCleanUpResult> CleanUpAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var codes = await store.DeleteExpiredCodesAsync(now, cancellationToken);

        var grants = await store.ListGrantsAsync(null, cancellationToken);
        var expired = grants.Where(g => g.RefreshExpiresAt <= now).ToList();
        foreach (var grant in expired)
        {
            await store.DeleteGrantAsync(grant.Id, cancellationToken);
        }

        var live = grants.Where(g => g.RefreshExpiresAt > now).Select(g => g.ClientId).ToHashSet(StringComparer.Ordinal);
        var lastEnded = expired.GroupBy(g => g.ClientId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(x => x.RefreshExpiresAt), StringComparer.Ordinal);
        var clients = 0;
        foreach (var client in await store.ListClientsAsync(cancellationToken))
        {
            if (live.Contains(client.Id))
            {
                // Also marks clients registered before the clean-up existed, which aren't marked.
                if (!client.Granted || client.IdleSince is not null)
                {
                    await store.MarkClientGrantedAsync(client.Id, cancellationToken);
                }

                continue;
            }

            // A revoked grant leaves nothing behind, so the idle time starts when the clean-up
            // first finds the client without one, or when its last grant expired.
            var idleSince = client.Granted
                ? client.IdleSince ?? lastEnded.GetValueOrDefault(client.Id, now)
                : client.RegisteredAt;
            if (now - idleSince >= (client.Granted ? IdleFor : NeverGrantedFor))
            {
                await store.DeleteClientAsync(client.Id, cancellationToken);
                clients++;
            }
            else if (client.Granted && client.IdleSince is null)
            {
                await store.MarkClientIdleAsync(client.Id, idleSince, cancellationToken);
            }
        }

        return new OAuthCleanUpResult(codes, expired.Count, clients);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstAfter, time, stoppingToken);
            using var timer = new PeriodicTimer(Every, time);
            do
            {
                try
                {
                    var result = await CleanUpAsync(stoppingToken);
                    LogCleanedUp(logger, result.Codes, result.Grants, result.Clients);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogCleanUpFailed(logger, e);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OAuth clean-up: deleted {Codes} expired codes, {Grants} expired grants and {Clients} unused clients")]
    private static partial void LogCleanedUp(ILogger logger, int codes, int grants, int clients);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OAuth clean-up failed; it runs again tomorrow")]
    private static partial void LogCleanUpFailed(ILogger logger, Exception exception);
}
