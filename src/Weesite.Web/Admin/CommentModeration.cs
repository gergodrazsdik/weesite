using Microsoft.AspNetCore.Mvc;
using Weesite.Web.Visitors;

namespace Weesite.Web.Admin;

/// <summary>
/// Moderating comments from the admin (ADR 0011): <c>POST /admin/comments/approve</c>, <c>/remove</c>
/// (a published one off its post, admins only, U-49),
/// <c>/reject</c> and <c>/delete</c>, each with the post's source path and the comment's ID, and
/// for approve and reject the version stamp the page showed (<c>version</c>): approving publishes
/// only what the moderator saw, so a comment changed since is left alone (F-0209). Reject here is for
/// what waits: a new comment, or the edit beside a published one. Any editor may post them;
/// <see cref="CommentDesk"/> lets admins moderate every post's comments and authors their own
/// posts'. With an antiforgery token (a form field binds, so it's checked), and for delete the
/// confirm dialog's field (<see cref="Confirmation"/>); audited without the comment's text. Each
/// goes back to the Comments tab it came from.
/// </summary>
internal static class CommentModeration
{
    /// <summary>The tabs of the Comments page.</summary>
    // The approved ones too (U-49), newest first and a page at a time, so hundreds stay workable;
    // admins remove one there as on its post.
    public static readonly IReadOnlyList<string> Tabs = ["waiting", "approved", "rejected"];

    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapCommentModeration(IEndpointRouteBuilder app)
    {
        Map(app, "approve", (desk, user, post, id, version, ct) => desk.ApproveAsync(user, post, id, version, null, ct));
        Map(app, "reject", (desk, user, post, id, version, ct) => desk.RejectAsync(user, post, id, version, null, ct));
        Map(app, "remove", (desk, user, post, id, version, ct) => desk.RemoveAsync(user, post, id, version, null, ct));
        Map(app, "delete", (desk, user, post, id, _, ct) => desk.DeleteAsync(user, post, id, null, ct));
    }

    private static void Map(
        IEndpointRouteBuilder app,
        string action,
        Func<CommentDesk, System.Security.Claims.ClaimsPrincipal, string, string, string?, CancellationToken, Task<ModerationOutcome>> moderate)
    {
        app.MapPost("/admin/comments/" + action, async ([FromForm] string post, [FromForm] string id, [FromForm] string? version, [FromForm] string? tab, HttpContext context, CommentDesk desk) =>
        {
            var back = Tabs.Contains(tab) ? tab : "waiting";
            if (action == "delete" && !await Confirmation.IsConfirmedAsync(context))
            {
                return Confirmation.Refused($"/admin/comments?tab={back}");
            }

            var outcome = await moderate(desk, context.User, post, id, version, context.RequestAborted);
            if (outcome == ModerationOutcome.Forbidden)
            {
                return Results.Redirect("/admin/forbidden");
            }

            var done = outcome switch
            {
                ModerationOutcome.Done => action switch { "approve" => "approved", "reject" => "rejected", "remove" => "removed", _ => "deleted" },
                ModerationOutcome.NotFound => "gone",
                ModerationOutcome.Changed => "changed",
                _ => "unchanged",
            };
            return Results.Redirect($"/admin/comments?tab={back}&done={done}");
        })
        .RequireAuthorization(EditorClaims.EditorPolicy);
    }
}
