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
        MapBulk(app);
    }

    /// <summary>The most comments one bulk action takes: a page of the Comments list.</summary>
    public const int MaxBulk = 50;

    /// <summary>A comment's value in the bulk form: its ID, the version stamp shown, and its post's source path last.</summary>
    /// <param name="comment">The comment.</param>
    /// <returns>The value.</returns>
    public static string BulkValue(Comment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return $"{comment.Id}|{comment.Stamp}|{comment.Post}";
    }

    // POST /admin/comments/bulk (T-0184): approve or reject the comments selected on the Waiting
    // tab, each as its own button would, at the version the page showed: a comment changed since,
    // or one this editor may not moderate, is left alone and counted, never decided unseen.
    private static void MapBulk(IEndpointRouteBuilder app) =>
        app.MapPost("/admin/comments/bulk", async ([FromForm(Name = "do")] string? choice, [FromForm] string? tab, HttpContext context, CommentDesk desk) =>
        {
            var back = Tabs.Contains(tab) ? tab : "waiting";
            // "do", not "action": a button named action would hide the form's own action from scripts.
            if (choice is not ("approve" or "reject"))
            {
                return Results.BadRequest();
            }

            var selected = (await context.Request.ReadFormAsync(context.RequestAborted))["selected"]
                .Select(v => (v ?? "").Split('|', 3))
                .Where(p => p.Length == 3 && Comment.IsId(p[0]) && p[2].Length > 0)
                .DistinctBy(p => (p[2], p[0]))
                .Take(MaxBulk)
                .ToList();
            if (selected.Count == 0)
            {
                return Results.Redirect($"/admin/comments?tab={back}&done=bulk-none");
            }

            var done = 0;
            foreach (var parts in selected)
            {
                var (id, stamp, post) = (parts[0], parts[1].Length == 0 ? null : parts[1], parts[2]);
                var outcome = choice == "approve"
                    ? await desk.ApproveAsync(context.User, post, id, stamp, null, CancellationToken.None)
                    : await desk.RejectAsync(context.User, post, id, stamp, null, CancellationToken.None);
                done += outcome == ModerationOutcome.Done ? 1 : 0;
            }

            var verb = choice == "approve" ? "approved" : "rejected";
            return Results.Redirect(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/admin/comments?tab={back}&done=bulk-{verb}&n={done}&left={selected.Count - done}"));
        })
        .RequireAuthorization(EditorClaims.EditorPolicy);

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
