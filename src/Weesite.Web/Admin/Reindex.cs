using System.Security.Claims;
using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Antiforgery;
using Weesite.Content;
using Weesite.Search;
using Weesite.ServiceDefaults;

namespace Weesite.Web.Admin;

/// <summary>Asks for a re-index with a message on the <c>reindex</c> queue, which starts the re-index job (T-0069).</summary>
internal sealed class QueueReindexRequests(QueueServiceClient queues, TimeProvider time) : IReindexRequests
{
    /// <summary>The queue the re-index job listens to.</summary>
    public const string QueueName = "reindex";

    /// <inheritdoc />
    public async Task<bool> RequestAsync(string requestedBy, CancellationToken cancellationToken)
    {
        var queue = queues.GetQueueClient(QueueName);
        await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

        // A request still waiting re-embeds everything anyway; one being run is taken off the queue
        // until it's done, so a press after a run starts asks again (for what changed since).
        var waiting = await queue.PeekMessagesAsync(1, cancellationToken);
        if (waiting.Value.Length > 0)
        {
            return false;
        }

        await SendAsync(requestedBy, cancellationToken);
        return true;
    }

    /// <summary>
    /// Sends a request without looking at the queue first: for the seed workflow, whose identity
    /// may only add messages (infra/app seed.tf; the queue is infra's). A second request waiting
    /// costs a run that finds nothing new.
    /// </summary>
    /// <param name="requestedBy">Who asks.</param>
    /// <param name="cancellationToken">Stops the request.</param>
    /// <returns>A task.</returns>
    public async Task SendAsync(string requestedBy, CancellationToken cancellationToken) =>
        await queues.GetQueueClient(QueueName).SendMessageAsync(JsonSerializer.Serialize(new { requestedBy, requestedAt = time.GetUtcNow() }), cancellationToken);
}

/// <summary>
/// Re-indexes inside the web app, in the background, one run at a time: content from files
/// (tests) and anywhere without a queue.
/// </summary>
internal sealed partial class InProcessReindexRequests(Reindexer reindexer, ContentCatalogProvider content, ILogger<InProcessReindexRequests> logger) : IReindexRequests, IDisposable
{
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Gets the last run (tests wait for it).</summary>
    public Task Last { get; private set; } = Task.CompletedTask;

    private int _waiting;

    /// <inheritdoc />
    public Task<bool> RequestAsync(string requestedBy, CancellationToken cancellationToken)
    {
        // One run waiting at most: a second press before it starts adds nothing (F-0091 L30).
        if (Interlocked.CompareExchange(ref _waiting, 1, 0) != 0)
        {
            return Task.FromResult(false);
        }

        Last = Task.Run(async () =>
        {
            await _running.WaitAsync();
            Interlocked.Exchange(ref _waiting, 0);
            try
            {
                await reindexer.RunAsync(content.Current, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A background run has no caller to tell; the admin can try again.
                LogFailed(ex);
            }
            finally
            {
                _running.Release();
            }
        }, CancellationToken.None);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public void Dispose() => _running.Dispose();

    [LoggerMessage(Level = LogLevel.Error, Message = "Re-indexing in the web app failed")]
    private partial void LogFailed(Exception exception);
}

/// <summary>The admin's "Re-embed all" (brief, editor features).</summary>
internal static class ReindexEndpoint
{
    /// <summary>Maps <c>POST /admin/search/reindex</c>: admins only, with an antiforgery token.</summary>
    /// <param name="app">The app.</param>
    public static void MapReindex(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/search/reindex", async (HttpContext context, IAntiforgery antiforgery, IReindexRequests reindex, IAuditLog audit, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            var cancel = context.RequestAborted;
            var actor = context.User.FindFirst(ClaimTypes.Email)?.Value ?? "(unknown)";
            if (!await reindex.RequestAsync(actor, cancel))
            {
                return Results.Redirect("/admin/settings?reindex=waiting#search-index-heading");
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), actor, "reindex-requested", "all posts and pages"), cancel);
            return Results.Redirect("/admin/settings?reindex=1#search-index-heading");
        })
        .RequireAuthorization(EditorClaims.AdminPolicy)
        .DisableAntiforgery();
    }
}

/// <summary>
/// The re-index job (brief, Bulk re-indexing): the web app's image started with <c>reindex</c>.
/// Takes the waiting requests off the queue, loads the content from Blob, embeds every chunk,
/// stores the vectors and bumps the version marker, then deletes the requests. A run that fails
/// leaves them, so they come back and the job runs again; one that has failed
/// <see cref="MaxAttempts"/> times is dropped.
/// </summary>
internal static partial class ReindexJob
{
    /// <summary>How often a request is tried before it's dropped.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Runs the job.</summary>
    /// <param name="args">The command line after <c>reindex</c>.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddServiceDefaults();
        builder.AddAzureBlobServiceClient("blobs");
        builder.AddAzureQueueServiceClient("queues");
        builder.Services.AddContentFromBlobs();
        builder.Services.AddSingleton<IVectorStore, BlobVectorStore>();
        builder.Services.AddSearch();

        // The host isn't started: none of the web app's background services run here.
        using var host = builder.Build();
        var services = host.Services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Weesite.ReindexJob");
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var cancel = lifetime.ApplicationStopping;

        var queue = services.GetRequiredService<QueueServiceClient>().GetQueueClient(QueueReindexRequests.QueueName);
        await queue.CreateIfNotExistsAsync(cancellationToken: cancel);

        // Every waiting request at once: one run covers them all. They stay hidden while it runs.
        var messages = (await queue.ReceiveMessagesAsync(maxMessages: 32, visibilityTimeout: TimeSpan.FromHours(1), cancellationToken: cancel)).Value;
        foreach (var dropped in messages.Where(m => m.DequeueCount > MaxAttempts))
        {
            LogDropped(logger, dropped.MessageId, dropped.DequeueCount);
            await queue.DeleteMessageAsync(dropped.MessageId, dropped.PopReceipt, cancel);
        }

        var pending = messages.Where(m => m.DequeueCount <= MaxAttempts).ToList();
        LogStarting(logger, pending.Count);

        await services.GetRequiredService<ContentLoader>().LoadAsync(cancel);
        var result = await services.GetRequiredService<Reindexer>().RunAsync(services.GetRequiredService<ContentCatalogProvider>().Current, cancel);
        if (result is null)
        {
            return 1;
        }

        foreach (var message in pending)
        {
            await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancel);
        }

        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-index job starting for {Requests} queued requests")]
    private static partial void LogStarting(ILogger logger, int requests);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropping re-index request {MessageId} after {Attempts} attempts")]
    private static partial void LogDropped(ILogger logger, string messageId, long attempts);
}
