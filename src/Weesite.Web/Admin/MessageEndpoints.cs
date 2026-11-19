using Microsoft.AspNetCore.Mvc;
using Weesite.Web.Visitors;

namespace Weesite.Web.Admin;

/// <summary>
/// Deleting contact messages (ADR 0011): <c>POST /admin/messages/delete</c> for one,
/// <c>POST /admin/messages/delete-read</c> for every read one. Admins only, with an antiforgery
/// token (a form field binds, so it's checked); audited without the message's content.
/// </summary>
internal static class MessageEndpoints
{
    /// <summary>Maps the endpoints.</summary>
    /// <param name="app">The app.</param>
    public static void MapMessages(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/messages/delete", async ([FromForm] string id, HttpContext context, IMessageStore store, IAuditLog audit, TimeProvider time) =>
        {
            if (!await store.DeleteAsync(id, context.RequestAborted))
            {
                return Results.Redirect("/admin/messages?gone=1");
            }

            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(context.User), "message-deleted", id), CancellationToken.None);
            return Results.Redirect("/admin/messages?deleted=1");
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);

        app.MapPost("/admin/messages/delete-read", async ([FromForm] string? scope, HttpContext context, IMessageStore store, IAuditLog audit, TimeProvider time) =>
        {
            var count = await store.DeleteReadAsync(context.RequestAborted);
            if (count > 0)
            {
                await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), EditorClaims.Email(context.User), "messages-deleted-read", count.ToString(System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None);
            }

            return Results.Redirect("/admin/messages?deletedRead=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        })
        .RequireAuthorization(EditorClaims.AdminPolicy);
    }
}
