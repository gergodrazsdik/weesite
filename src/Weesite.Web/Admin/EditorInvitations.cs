using System.Net;
using Weesite.Content;
using Weesite.Web.Email;
using Weesite.Web.Seo;

namespace Weesite.Web.Admin;

/// <summary>
/// The invitation an editor gets when an admin adds them or changes their role (ADR 0010): sent
/// when email is set up, and always on the Editors page to copy. In English, as the admin is.
/// </summary>
internal sealed partial class EditorInvitations(IEmailSender sender, ContentCatalogProvider content, SiteUrls urls, IAuditLog audit, TimeProvider time, ILogger<EditorInvitations> logger)
{
    /// <summary>Gets a value indicating whether invitations are sent.</summary>
    public bool IsSending => sender.IsConfigured;

    /// <summary>Builds the invitation.</summary>
    /// <param name="request">The current request, for the site's address.</param>
    /// <param name="editor">The editor, as saved.</param>
    /// <param name="roleChanged">Whether their role changed, rather than their being added.</param>
    /// <returns>The mail.</returns>
    public EmailMessage For(HttpRequest request, Editor editor, bool roleChanged = false)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return Build(content.Current.Site.Name, urls.Absolute(request, "/admin"), editor, roleChanged);
    }

    /// <summary>Sends the invitation, if email is set up, and records the outcome.</summary>
    /// <param name="request">The current request, for the site's address.</param>
    /// <param name="editor">The editor, as saved.</param>
    /// <param name="roleChanged">Whether their role changed, rather than their being added.</param>
    /// <param name="cancellationToken">Stops the send.</param>
    /// <returns><c>sent</c>, <c>failed</c>, or null when email isn't set up.</returns>
    public async Task<string?> SendAsync(HttpRequest request, Editor editor, bool roleChanged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!sender.IsConfigured)
        {
            return null;
        }

        try
        {
            await sender.SendAsync(For(request, editor, roleChanged), cancellationToken);
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), editor.AddedBy, "editor-invited", editor.Email), cancellationToken);
            return "sent";
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Best effort: the editor is saved whatever happens here. The reason, not the mail.
            LogFailed(logger, ex);
            var reason = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            await audit.WriteAsync(new AuditEntry(time.GetUtcNow(), editor.AddedBy, "editor-invite-failed", $"{editor.Email} ({ex.GetType().Name}: {reason})"), CancellationToken.None);
            return "failed";
        }
    }

    /// <summary>Builds the invitation's text.</summary>
    /// <param name="siteName">The site's name.</param>
    /// <param name="signInUrl">The sign-in address.</param>
    /// <param name="editor">The editor.</param>
    /// <param name="roleChanged">Whether their role changed, rather than their being added.</param>
    /// <returns>The mail.</returns>
    internal static EmailMessage Build(string siteName, string signInUrl, Editor editor, bool roleChanged)
    {
        var role = editor.Role == EditorRole.Admin ? "an admin" : "an author";
        var subject = roleChanged ? $"Your role on {siteName} has changed" : $"You can now edit {siteName}";
        string[] paragraphs =
        [
            roleChanged
                ? $"{editor.AddedBy} has changed your role on {siteName}: you're now {role}."
                : $"{editor.AddedBy} has added you to {siteName} as {role}.",
            editor.Role == EditorRole.Admin
                ? "As an admin, you can write and publish posts, edit pages and everyone's posts, upload media, and change the site's settings, menu, theme, tags and editors."
                : "As an author, you can write and publish your own posts and upload media.",
            $"Sign in at {signInUrl} with the Google or Microsoft account of this address, {editor.Email}.",
            "If you didn't expect this, you can ignore this mail.",
        ];

        var text = string.Join("\n\n", paragraphs) + "\n";
        var html = string.Concat(paragraphs.Select((p, i) => i == 2
            ? $"<p>Sign in at <a href=\"{WebUtility.HtmlEncode(signInUrl)}\">{WebUtility.HtmlEncode(signInUrl)}</a> with the Google or Microsoft account of this address, {WebUtility.HtmlEncode(editor.Email)}.</p>"
            : $"<p>{WebUtility.HtmlEncode(p)}</p>"));
        return new EmailMessage(editor.Email, subject, text, html, siteName);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The editor invitation couldn't be sent")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
