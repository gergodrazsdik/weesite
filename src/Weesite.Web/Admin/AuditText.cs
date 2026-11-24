using System.Text.RegularExpressions;
using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>
/// The audit log in words for the Audit log page (U-53): an action's code as a phrase, the editor's
/// name for their address, and a target's file path as the post or page it is. The log itself keeps
/// the codes, addresses and paths, which the CSV download has as they are.
/// </summary>
internal static partial class AuditText
{
    private const string AppPrefix = "mcp-";

    private static readonly Dictionary<string, string> Actions = new(StringComparer.Ordinal)
    {
        ["sign-in"] = "Signed in",
        ["sign-in-refused"] = "Sign-in refused",
        ["sign-out"] = "Signed out",
        ["sign-out-everywhere"] = "Signed out everywhere",
        ["item-created"] = "Created",
        ["item-saved"] = "Saved",
        ["item-restored"] = "Restored an earlier version",
        ["status-published"] = "Published",
        ["status-draft"] = "Made a draft",
        ["status-scheduled"] = "Scheduled",
        ["status-archived"] = "Archived",
        ["content-deleted"] = "Deleted for good",
        ["media-uploaded"] = "Uploaded a file",
        ["media-replaced"] = "Replaced a file",
        ["media-deleted"] = "Deleted a file",
        ["media-archived"] = "Archived a file",
        ["media-unarchived"] = "Brought back a file",
        ["media-described"] = "Described an image",
        ["upload-address"] = "Got an upload address",
        ["settings-changed"] = "Changed the site settings",
        ["menu-changed"] = "Changed the menu",
        ["theme-changed"] = "Changed the colour theme",
        ["tags-changed"] = "Changed the tags",
        ["profile-saved"] = "Saved a profile",
        ["editor-added"] = "Added an editor",
        ["editor-role-changed"] = "Changed an editor's role",
        ["editor-removed"] = "Removed an editor",
        ["editor-invited"] = "Sent an invitation",
        ["editor-invite-failed"] = "Invitation not sent",
        ["comment-approved"] = "Approved a comment",
        ["comment-rejected"] = "Rejected a comment",
        ["comment-removed"] = "Removed a comment",
        ["comment-deleted"] = "Deleted a comment",
        ["message-deleted"] = "Deleted a message",
        ["messages-deleted-read"] = "Deleted the read messages",
        ["backup-downloaded"] = "Downloaded a backup",
        ["backup-imported"] = "Imported a backup",
        ["site-reset"] = "Reset the site",
        ["audit-exported"] = "Downloaded the audit log",
        ["reindex-requested"] = "Asked for a new search index",
        ["oauth-grant"] = "Connected an app",
        ["oauth-denied"] = "Refused an app",
        ["oauth-revoke"] = "Revoked an app",
        ["oauth-grant-ended"] = "An app's access ended",
        ["oauth-code-reuse"] = "Revoked an app: a sign-in code was used twice",
        ["oauth-refresh-reuse"] = "Revoked an app: an old token was used again",
    };

    /// <summary>Gets an action in words: <c>media-uploaded</c> is "Uploaded a file", and one an app did says so.</summary>
    /// <param name="action">The action's code.</param>
    /// <returns>The words; for a code this doesn't know, the code with spaces, capitalized.</returns>
    public static string Action(string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var byApp = action.StartsWith(AppPrefix, StringComparison.Ordinal);
        var code = byApp ? action[AppPrefix.Length..] : action;
        var words = Actions.TryGetValue(code, out var known) ? known
            : code.Length == 0 ? action
            : char.ToUpperInvariant(code[0]) + code[1..].Replace('-', ' ');
        return byApp ? words + " (through an app)" : words;
    }

    /// <summary>Gets the name an editor's address stands for: their author profile's, or none.</summary>
    /// <param name="site">The site settings, with the profiles.</param>
    /// <param name="actor">Who, as the log has it: an address, or such as <c>(not an editor)</c>.</param>
    /// <returns>The name, or null when there's no profile with one.</returns>
    public static string? NameOf(SiteSettings site, string actor)
    {
        ArgumentNullException.ThrowIfNull(site);
        return actor is { Length: > 0 } && actor.Contains('@', StringComparison.Ordinal)
            && site.FindAuthor(EditorRights.AuthorId(actor))?.Name is { Length: > 0 } name
                ? name
                : null;
    }

    /// <summary>A target as the page shows it.</summary>
    /// <param name="Text">The words: a post's or page's title, or the target as it was written.</param>
    /// <param name="Path">The source path, when the words are a title.</param>
    /// <param name="Edit">The editor's address of the post or page, when it's still there.</param>
    /// <param name="App">The app it was done through, if any.</param>
    internal sealed record Target(string Text, string? Path, string? Edit, string? App);

    /// <summary>Reads a target: a post's or page's path becomes its title, a comment says whose post,
    /// a profile whose, and an app's name comes apart.</summary>
    /// <param name="catalog">The content.</param>
    /// <param name="target">The target as the log has it.</param>
    /// <returns>The target to show; null for none.</returns>
    public static Target? Read(ContentCatalog catalog, string? target)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        string? app = null;
        var text = target;
        if (ViaApp().Match(text) is { Success: true } via)
        {
            app = via.Groups["app"].Value;
            text = text[..via.Index];
        }

        // A comment: the post's path, then # and the comment's ID.
        var comment = false;
        if (text.IndexOf('#', StringComparison.Ordinal) is > 0 and var hash && IsSourcePath(text[..hash]))
        {
            text = text[..hash];
            comment = true;
        }

        if (IsSourcePath(text))
        {
            var item = catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished).FirstOrDefault(i => i.SourcePath == text);
            if (item is not null)
            {
                return new Target(comment ? $"A comment on “{item.Title}”" : $"“{item.Title}”", text, "/admin/edit?path=" + Uri.EscapeDataString(text), app);
            }

            return new Target(comment ? "A comment on " + text : text, null, null, app);
        }

        if (Profile().Match(text) is { Success: true } profile
            && catalog.Site.FindAuthor(profile.Groups["id"].Value)?.Name is { Length: > 0 } name)
        {
            return new Target($"{name}'s profile", null, null, app);
        }

        return new Target(text, null, null, app);
    }

    private static bool IsSourcePath(string text) =>
        (text.StartsWith("posts/", StringComparison.Ordinal) || text.StartsWith("pages/", StringComparison.Ordinal))
        && text.EndsWith(".md", StringComparison.Ordinal)
        && !text.Contains(' ', StringComparison.Ordinal);

    [GeneratedRegex(@" \(via (?<app>[^()]+)\)$")]
    private static partial Regex ViaApp();

    [GeneratedRegex(@"^authors\.json \((?<id>[^()]+)\)$")]
    private static partial Regex Profile();
}
