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
internal sealed record OwnComment(string Id, string Name, string Text, string? PendingText, string Status, string Created, string? Token = null)
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
            token);
    }
}

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

            var result = await desk.EditAsync(post, id, TokenOf(context), edit, context.RequestAborted);
            return result.Outcome == CommentOutcome.Done ? Results.Ok(OwnComment.Of(result.Comment!)) : Refused(result, t);
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
