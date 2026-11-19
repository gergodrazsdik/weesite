using Microsoft.AspNetCore.Mvc;
using Weesite.Web.Visitors;

namespace Weesite.Web.Admin;

/// <summary>
/// Moderating comments from the admin (ADR 0011): <c>POST /admin/comments/approve</c>,
/// <c>/reject</c> and <c>/delete</c>, each with the post's source path and the comment's ID. Any
/// editor may post them; <see cref="CommentDesk"/> lets admins moderate every post's comments and
/// authors their own posts'. With an antiforgery token (a form field binds, so it's checked);
/// audited without the comment's text. Each goes back to the Comments tab it came from.
/// </summary>
internal static class CommentModeration
{
    /// <summary>The tabs of the Comments page.</summary>
    // No published tab (T-0161): with hundreds of comments it can't be worked through; published
    // ones are moderated on their post.
    public static readonly IReadOnlyList<string> Tabs = ["waiting", "rejected"];

    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapCommentModeration(IEndpointRouteBuilder app)
    {
        Map(app, "approve", (desk, user, post, id, ct) => desk.ApproveAsync(user, post, id, null, ct));
        Map(app, "reject", (desk, user, post, id, ct) => desk.RejectAsync(user, post, id, null, ct));
        Map(app, "delete", (desk, user, post, id, ct) => desk.DeleteAsync(user, post, id, null, ct));
    }

    private static void Map(
        IEndpointRouteBuilder app,
        string action,
        Func<CommentDesk, System.Security.Claims.ClaimsPrincipal, string, string, CancellationToken, Task<ModerationOutcome>> moderate)
    {
        app.MapPost("/admin/comments/" + action, async ([FromForm] string post, [FromForm] string id, [FromForm] string? tab, HttpContext context, CommentDesk desk) =>
        {
            var outcome = await moderate(desk, context.User, post, id, context.RequestAborted);
            if (outcome == ModerationOutcome.Forbidden)
            {
                return Results.Redirect("/admin/forbidden");
            }

            var back = Tabs.Contains(tab) ? tab : "waiting";
            var done = outcome switch
            {
                ModerationOutcome.Done => action switch { "approve" => "approved", "reject" => "rejected", _ => "deleted" },
                ModerationOutcome.NotFound => "gone",
                _ => "unchanged",
            };
            return Results.Redirect($"/admin/comments?tab={back}&done={done}");
        })
        .RequireAuthorization(EditorClaims.EditorPolicy);
    }
}
