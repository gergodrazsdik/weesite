using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Weesite.Web.Admin;
using Weesite.Web.Agents.OAuth;

namespace Weesite.Web.Agents;

/// <summary>What a confirmation to publish is for: one grant, one item at one version, one change.</summary>
/// <param name="GrantId">The grant of the app that asked.</param>
/// <param name="Path">The item.</param>
/// <param name="Version">Its version when the editor was asked.</param>
/// <param name="Status"><c>published</c> or <c>scheduled</c>.</param>
/// <param name="PublishAt">For scheduled: when, in UTC, round-trip format.</param>
/// <param name="ExpiresAt">When the question stops being answerable.</param>
internal sealed record PublishQuestion(string GrantId, string Path, string Version, string Status, string? PublishAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Asks the editor in their app before <c>set_status</c> publishes or schedules, when they chose so
/// on the consent page (T-0198). The server is stateless, so it asks through MCP's multi round-trip
/// requests (MRTR): the first call answers with an elicitation for the client to show and a request
/// state, and the client calls again with the editor's answer and that state. The state is a Data
/// Protection payload bound to the grant, the item, its version and the change, for ten minutes: it
/// can't be made up, moved to another item or change, or used again once the item has changed. An
/// app's own client could answer for the editor, so this is a convenience; the consent's levels are
/// what limit an app.
/// </summary>
internal sealed class PublishConfirmations(IDataProtectionProvider protection, IAuditLog audit, TimeProvider time)
{
    /// <summary>How long the editor has to answer.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The input request's key.</summary>
    public const string Key = "confirm_publish";

    /// <summary>The refusal when the app can't ask.</summary>
    public const string CannotAsk =
        "You asked to be asked in the app before anything is published, and this app can't ask you: it doesn't support MCP elicitation. Publish or schedule it in /admin, or connect the app again without that option.";

    private readonly IDataProtector _protector = protection.CreateProtector("Weesite.PublishConfirmations.v1");

    /// <summary>
    /// Asks the editor, or reads their answer: returns when they confirmed, throws
    /// <see cref="InputRequiredException"/> to ask, and <see cref="McpException"/> when they declined
    /// or the answer isn't valid for this change.
    /// </summary>
    /// <param name="server">The request's server, for what the client supports.</param>
    /// <param name="request">The tool call, with the request state and the answer on a retry.</param>
    /// <param name="user">The editor, signed in with the app's token.</param>
    /// <param name="path">The item.</param>
    /// <param name="version">The version it's saved at, which is the stored one.</param>
    /// <param name="title">Its title, for the question.</param>
    /// <param name="status"><c>published</c> or <c>scheduled</c>.</param>
    /// <param name="publishAt">For scheduled: when.</param>
    /// <returns>A task that completes when the editor confirmed.</returns>
    public async Task ConfirmAsync(McpServer server, RequestContext<CallToolRequestParams> request, ClaimsPrincipal user, string path, string version, string title, string status, DateTimeOffset? publishAt)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        var grantId = user.FindFirstValue(EditorBearerHandler.GrantClaim) ?? throw new McpException("Not signed in.");
        var at = publishAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var target = $"{path} ({status}, via {user.FindFirstValue(EditorBearerHandler.ClientClaim) ?? "an app"})";

        // The retry, with the editor's answer.
        if (request.Params?.RequestState is { } state)
        {
            var question = Read(state)
                ?? throw new McpException("This confirmation isn't valid, or the 10 minutes to answer it are over: call set_status again to be asked again. Nothing changed.");
            if (question.GrantId != grantId || question.Path != path || question.Status != status || question.PublishAt != at)
            {
                throw new McpException("This confirmation is for another item or another change: call set_status again to be asked about this one. Nothing changed.");
            }

            // Every save makes a new version, so an answer is used once.
            if (question.Version != version)
            {
                throw new McpException("The item changed since you were asked, or this confirmation was used already: call set_status again to be asked about the current version. Nothing changed.");
            }

            var answer = request.Params.InputResponses?.TryGetValue(Key, out var response) == true
                ? response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)
                : null;
            if (answer?.IsAccepted == true)
            {
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(user), "mcp-publish-confirmed", target), CancellationToken.None);
                return;
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(user), "mcp-publish-declined", target), CancellationToken.None);
            throw new McpException($"You declined in the app, so '{title}' wasn't {(status == "scheduled" ? "scheduled" : "published")}. Nothing changed.");
        }

        // The first call: ask, if the client can.
        if (!server.IsMrtrSupported || server.ClientCapabilities?.Elicitation?.Form is null)
        {
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(user), "mcp-publish-not-asked", target), CancellationToken.None);
            throw new McpException(CannotAsk);
        }

        var message = publishAt is { } when
            ? $"Schedule '{title}' for {when.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC?"
            : $"Publish '{title}' now?";
        throw new InputRequiredException(
            new Dictionary<string, InputRequest>
            {
                [Key] = InputRequest.ForElicitation(new ElicitRequestParams { Message = message, RequestedSchema = new() }),
            },
            Protect(new PublishQuestion(grantId, path, version, status, at, time.GetUtcNow() + Lifetime)));
    }

    /// <summary>Protects a question as the request state.</summary>
    /// <param name="question">The question.</param>
    /// <returns>The state.</returns>
    internal string Protect(PublishQuestion question) => _protector.Protect(JsonSerializer.Serialize(question));

    /// <summary>Reads a request state: null when it isn't one of ours, or has expired.</summary>
    /// <param name="state">The state the client sent back.</param>
    /// <returns>The question, or null.</returns>
    internal PublishQuestion? Read(string state)
    {
        try
        {
            var question = JsonSerializer.Deserialize<PublishQuestion>(_protector.Unprotect(state));
            return question is not null && question.ExpiresAt > time.GetUtcNow() ? question : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }
}
