using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace Weesite.Web.Visitors;

/// <summary>A comment as its writer's browser sees it.</summary>
/// <param name="Id">Its ID.</param>
/// <param name="Name">The writer's name.</param>
/// <param name="Text">The text: the public one once approved, the waiting one before.</param>
/// <param name="PendingText">An edit waiting for moderation, if any.</param>
/// <param name="Status"><c>pending</c>, <c>approved</c> or <c>rejected</c>.</param>
/// <param name="Created">When it was written (UTC, ISO 8601).</param>
/// <param name="Token">The token, only in the answer to a new comment.</param>
/// <param name="By"><c>author</c> or <c>editor</c> when a signed-in editor wrote it.</param>
internal sealed record OwnComment(string Id, string Name, string Text, string? PendingText, string Status, string Created, string? Token = null, string? By = null)
{
    /// <summary>Makes the browser's view of a comment.</summary>
    /// <param name="comment">The comment.</param>
    /// <param name="token">The token, for a new one.</param>
    /// <returns>The view.</returns>
    public static OwnComment Of(Comment comment, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return new(
            comment.Id,
            comment.Name,
            comment.Text,
            comment.PendingText,
            comment.StatusName,
            comment.Created.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            token,
            comment.By);
    }
}

/// <summary>Who's reading a post, for its script (T-0161).</summary>
/// <param name="Editor">Whether a signed-in editor is: they comment under their own name.</param>
/// <param name="Name">The name they comment under.</param>
/// <param name="Mark"><c>author</c> on their own post, else <c>editor</c>.</param>
/// <param name="Moderator">Whether they may approve and reject this post's comments.</param>
/// <param name="Admin">Whether they're an admin, who can also reject a published comment.</param>
internal sealed record CommentViewer(bool Editor, string? Name, string? Mark, bool Moderator, bool Admin);

/// <summary>A moderator's decision on the post (T-0161).</summary>
/// <param name="Post">The post's slug.</param>
/// <param name="Id">The comment's ID.</param>
/// <param name="Decision"><c>approve</c> or <c>reject</c>.</param>
internal sealed record CommentDecision(string? Post, string? Id, string? Decision);

/// <summary>Why a comment request was refused: a code for scripts and a sentence for people.</summary>
/// <param name="Error">The code, such as <c>too-fast</c>.</param>
/// <param name="Message">What to tell the visitor, in the site's language.</param>
internal sealed record CommentError(string Error, string Message);

/// <summary>
/// Comments for visitors (ADR 0011), under <c>/visitor</c> (the per-minute limit applies, and
/// nothing is cached on the way). All of it is for the post page's script; the form exists only
/// with JavaScript.
/// <list type="bullet">
/// <item><c>GET /visitor/comments/form?post=slug</c>: whether the post takes comments, and a fresh form token.</item>
/// <item><c>POST /visitor/comments</c> (JSON <see cref="CommentSubmission"/>): a new comment, waiting
/// for moderation; the answer has its token, given only this once.</item>
/// <item><c>GET</c>, <c>PUT</c> (JSON <see cref="CommentEdit"/>) and <c>DELETE</c>
/// <c>/visitor/comments/{id}?post=slug</c> with <c>Authorization: Comment &lt;token&gt;</c>: the
/// writer's own comment. The token goes in a header, never in an address, so no log keeps it.</item>
/// </list>
/// </summary>
internal static class CommentEndpoints
{
    /// <summary>The authorization scheme of a comment's token.</summary>
    public const string Scheme = "Comment";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Adds the comments: their store (Table Storage, or memory), the catalog of approved ones, the desk, the digest and the clean-up.</summary>
    /// <param name="services">The services.</param>
    /// <param name="inMemory">Whether to keep comments in memory.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddComments(this IServiceCollection services, bool inMemory)
    {
        if (inMemory)
        {
            services.AddSingleton<MemoryCommentStore>();
            services.AddSingleton<ICommentStore>(s => s.GetRequiredService<MemoryCommentStore>());
        }
        else
        {
            services.AddSingleton<ICommentStore, TableCommentStore>();
        }

        services.AddSingleton<CommentCatalog>();
        services.AddHostedService(s => s.GetRequiredService<CommentCatalog>());
        services.AddSingleton<CommentDesk>();
        services.AddSingleton<CommentsDigest>();
        services.AddSingleton<IDigest>(s => s.GetRequiredService<CommentsDigest>());
        services.AddHostedService<CommentCleanUp>();
        return services;
    }

    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    /// <returns>The app.</returns>
    public static IEndpointRouteBuilder MapComments(this IEndpointRouteBuilder app)
    {
        app.MapGet("/visitor/comments/form", (HttpContext context, CommentDesk desk, string? post) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return desk.IsOpen(desk.Published(post))
                ? Results.Ok(new { open = true, token = desk.FormToken() })
                : Results.Ok(new { open = false, token = (string?)null });
        });

        app.MapPost("/visitor/comments", async (HttpContext context, CommentDesk desk, IStringLocalizer<PublicResource> t) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await ReadAsync<CommentSubmission>(context) is not { } sent)
            {
                return BadJson(t);
            }

            var result = await desk.SubmitAsync(context, sent, context.RequestAborted);
            return result.Outcome is CommentOutcome.Done or CommentOutcome.Pretended
                ? Results.Ok(OwnComment.Of(result.Comment!, result.Token))
                : Refused(result, t);
        });

        app.MapGet("/visitor/comments/{id}", async (HttpContext context, CommentDesk desk, IStringLocalizer<PublicResource> t, string id, string? post) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var result = await desk.OwnAsync(post, id, TokenOf(context), context.RequestAborted);
            return result.Comment is { } comment ? Results.Ok(OwnComment.Of(comment)) : Refused(result, t);
        });

        app.MapPut("/visitor/comments/{id}", async (HttpContext context, CommentDesk desk, IStringLocalizer<PublicResource> t, string id, string? post) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await ReadAsync<CommentEdit>(context) is not { } edit)
            {
                return BadJson(t);
            }

            var result = await desk.EditAsync(post, id, TokenOf(context), edit, context.RequestAborted, context.User);
            return result.Outcome == CommentOutcome.Done ? Results.Ok(OwnComment.Of(result.Comment!)) : Refused(result, t);
        });

        // Who's here, for the post's script (T-0161): a signed-in editor comments under their own
        // name, and a moderator of the post approves and rejects there. Never cached.
        app.MapGet("/visitor/comments/me", (HttpContext context, CommentDesk desk, string? post) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var item = desk.Published(post);
            var name = desk.Signed(context.User);
            return Results.Ok(new CommentViewer(
                name is not null,
                name,
                name is null || item is null ? null : string.Equals(item.AuthorId, Weesite.Web.Admin.EditorRights.AuthorId(context.User), StringComparison.Ordinal) ? "author" : "editor",
                name is not null && item is not null && desk.CanModerate(context.User, item.SourcePath),
                name is not null && context.User.IsInRole(nameof(Weesite.Web.Admin.EditorRole.Admin))));
        });

        app.MapGet("/visitor/comments/moderation", async (HttpContext context, CommentDesk desk, string? post) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (desk.Published(post) is not { } item || desk.Signed(context.User) is null || !desk.CanModerate(context.User, item.SourcePath))
            {
                return Results.Json(new CommentError("forbidden", "Sign in as an editor who may moderate this post."), statusCode: StatusCodes.Status403Forbidden);
            }

            var waiting = await desk.WaitingOnAsync(context.User, item, context.RequestAborted);
            return Results.Ok(waiting.Select(c => OwnComment.Of(c)).ToList());
        });

        // Approving or rejecting on the post (T-0161): the editor's cookie and JSON, which another
        // site's page can't send without asking first (no CORS here), so no cross-site post gets in.
        app.MapPost("/visitor/comments/moderation", async (HttpContext context, CommentDesk desk, IStringLocalizer<PublicResource> t) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await ReadAsync<CommentDecision>(context) is not { } decision)
            {
                return BadJson(t);
            }

            if (desk.Published(decision.Post) is not { } item || desk.Signed(context.User) is null)
            {
                return Results.Json(new CommentError("forbidden", "Sign in as an editor who may moderate this post."), statusCode: StatusCodes.Status403Forbidden);
            }

            var outcome = decision.Decision switch
            {
                "approve" => await desk.ApproveAsync(context.User, item.SourcePath, decision.Id ?? "", "on the post", context.RequestAborted),
                "reject" => await desk.RejectAsync(context.User, item.SourcePath, decision.Id ?? "", "on the post", context.RequestAborted),
                _ => (ModerationOutcome?)null,
            };
            return outcome switch
            {
                ModerationOutcome.Done => Results.NoContent(),
                ModerationOutcome.Forbidden => Results.Json(new CommentError("forbidden", "You may moderate comments on your own posts only."), statusCode: StatusCodes.Status403Forbidden),
                ModerationOutcome.NotFound => Results.Json(new CommentError("not-found", t["CommentErrorGone"]), statusCode: StatusCodes.Status404NotFound),
                ModerationOutcome.NothingWaiting => Results.Json(new CommentError("nothing-waiting", "Nothing waits on that comment."), statusCode: StatusCodes.Status409Conflict),
                _ => BadJson(t),
            };
        });

        app.MapDelete("/visitor/comments/{id}", async (HttpContext context, CommentDesk desk, IStringLocalizer<PublicResource> t, string id, string? post) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var outcome = await desk.DeleteOwnAsync(post, id, TokenOf(context), context.RequestAborted);
            return outcome == CommentOutcome.Done ? Results.NoContent() : Refused(new CommentResult(outcome), t);
        });

        return app;
    }

    /// <summary>Reads a comment's token from <c>Authorization: Comment &lt;token&gt;</c>.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The token, or null.</returns>
    public static string? TokenOf(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase) ? header[(Scheme.Length + 1)..].Trim() : null;
    }

    private static async Task<T?> ReadAsync<T>(HttpContext context)
        where T : class
    {
        if (!context.Request.HasJsonContentType())
        {
            return null;
        }

        try
        {
            return await context.Request.ReadFromJsonAsync<T>(Json, context.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Every error answer has a body, so the status code pages leave it alone.
    private static IResult BadJson(IStringLocalizer<PublicResource> t) =>
        Results.Json(new CommentError("bad-request", t["CommentErrorFailed"]), statusCode: StatusCodes.Status400BadRequest);

    private static IResult Refused(CommentResult result, IStringLocalizer<PublicResource> t)
    {
        var (code, key, status) = result.Outcome switch
        {
            CommentOutcome.Closed => ("closed", "CommentErrorClosed", StatusCodes.Status403Forbidden),
            CommentOutcome.TooFast => ("too-fast", "CommentErrorTooFast", StatusCodes.Status400BadRequest),
            CommentOutcome.Expired => ("expired", "CommentErrorExpired", StatusCodes.Status400BadRequest),
            CommentOutcome.Invalid => ("invalid", "CommentErrorInvalid", StatusCodes.Status400BadRequest),
            CommentOutcome.NoProof => ("no-proof", "CommentErrorNoProof", StatusCodes.Status400BadRequest),
            CommentOutcome.TooMany => ("too-many", "CommentErrorTooMany", StatusCodes.Status429TooManyRequests),
            CommentOutcome.BadName => ("bad-name", result.Problem switch
            {
                TextProblem.Empty => "CommentErrorNameMissing",
                TextProblem.TooLong => "CommentErrorNameTooLong",
                _ => "CommentErrorNamePlain",
            }, StatusCodes.Status400BadRequest),
            CommentOutcome.BadText => ("bad-text", result.Problem switch
            {
                TextProblem.Empty => "CommentErrorTextMissing",
                TextProblem.TooLong => "CommentErrorTextTooLong",
                TextProblem.TooManyLinks => "CommentErrorTextLinks",
                _ => "CommentErrorTextMarkup",
            }, StatusCodes.Status400BadRequest),
            CommentOutcome.WrongToken => ("wrong-token", "CommentErrorGone", StatusCodes.Status403Forbidden),
            CommentOutcome.NotEditable => ("not-editable", "CommentErrorNotEditable", StatusCodes.Status409Conflict),
            _ => ("not-found", "CommentErrorGone", StatusCodes.Status404NotFound),
        };
        return result.Outcome is CommentOutcome.BadName or CommentOutcome.BadText && result.Problem == TextProblem.TooLong
            ? Results.Json(new CommentError(code, t[key, result.Outcome == CommentOutcome.BadName ? CommentDesk.NameMax : CommentDesk.TextMax]), statusCode: status)
            : Results.Json(new CommentError(code, t[key]), statusCode: status);
    }
}
