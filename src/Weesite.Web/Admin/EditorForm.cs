using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Weesite.Content;

namespace Weesite.Web.Admin;

/// <summary>
/// The edit page's form: a <see cref="ContentDraft"/> as text fields, as the browser posts them.
/// Dates are <c>yyyy-MM-dd</c>, the publish time <c>yyyy-MM-dd HH:mm</c> in UTC (a <c>T</c> in place of the
/// space works too), tags comma-separated.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515", Justification = "A Razor component's form parameter must be public.")]
public sealed class EditorForm
{
    /// <summary>Gets or sets the item's path, or empty for a new one.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the ETag the item was opened at.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets <c>post</c> or <c>page</c>.</summary>
    public string Kind { get; set; } = "post";

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Gets or sets the slug.</summary>
    public string Slug { get; set; } = "";

    /// <summary>Gets or sets the date.</summary>
    public string? Date { get; set; }

    /// <summary>Gets or sets the update date.</summary>
    public string? Updated { get; set; }

    /// <summary>Gets or sets <c>draft</c>, <c>scheduled</c> or <c>published</c>.</summary>
    public string Status { get; set; } = "draft";

    /// <summary>Gets or sets the publish time (UTC).</summary>
    public string? PublishAt { get; set; }

    /// <summary>Gets or sets the summary.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets the tags, comma-separated.</summary>
    public string? Tags { get; set; }

    /// <summary>Gets or sets the image path.</summary>
    public string? Image { get; set; }

    /// <summary>Gets or sets the image's alternative text.</summary>
    public string? ImageAlt { get; set; }

    /// <summary>Gets or sets whether a page is in the menu.</summary>
    public bool Menu { get; set; }

    /// <summary>Gets or sets the author ID (kept from the item; not a form field).</summary>
    public string? AuthorId { get; set; }

    /// <summary>Gets or sets the redirects (kept from the item; not a form field).</summary>
    public string? RedirectFrom { get; set; }

    /// <summary>Gets or sets the language code; empty for the site's (ADR 0008).</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the Markdown body.</summary>
    public string Body { get; set; } = "";

    /// <summary>Gets a form for a new item.</summary>
    /// <param name="kind">Post or page.</param>
    /// <param name="today">Today, for a post's date.</param>
    /// <returns>An empty form.</returns>
    public static EditorForm New(ContentKind kind, DateOnly today) => new()
    {
        Kind = kind == ContentKind.Page ? "page" : "post",
        Date = kind == ContentKind.Post ? today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
    };

    /// <summary>Gets a form filled from a draft.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="path">Its path.</param>
    /// <param name="etag">Its ETag.</param>
    /// <returns>The form.</returns>
    public static EditorForm From(ContentDraft draft, string? path, string? etag)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return FromDraft(draft, path, etag);
    }

    private static EditorForm FromDraft(ContentDraft draft, string? path, string? etag) => new()
    {
        Path = path,
        ETag = etag,
        Kind = draft.Kind == ContentKind.Page ? "page" : "post",
        Title = draft.Title,
        Slug = draft.Slug,
        Date = draft.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Updated = draft.Updated?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Status = draft.Status switch { ContentStatus.Published => "published", ContentStatus.Scheduled => "scheduled", ContentStatus.Archived => "archived", _ => "draft" },
        PublishAt = draft.PublishAt?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        Summary = draft.Summary,
        Tags = string.Join(", ", draft.Tags),
        Image = draft.Image,
        ImageAlt = draft.ImageAlt,
        Menu = draft.ShowInMenu,
        AuthorId = draft.AuthorId,
        Language = draft.Language,
        RedirectFrom = string.Join(",", draft.RedirectFrom),
        Body = draft.Body,
    };

    /// <summary>Turns the form into a draft; malformed dates become a message instead.</summary>
    /// <param name="language">The site's language, for an empty Language field.</param>
    /// <param name="error">What's malformed, if anything.</param>
    /// <returns>The draft, or null with <paramref name="error"/> set.</returns>
    public ContentDraft? ToDraft(string language, out string? error)
    {
        string? problem = null;
        DateOnly? Day(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return day;
            }

            problem ??= $"The {field} isn't a date such as 2026-11-09.";
            return null;
        }

        var date = Day(Date, "date");
        var updated = Day(Updated, "update date");
        DateTimeOffset? publishAt = null;
        if (!string.IsNullOrWhiteSpace(PublishAt))
        {
            if (DateTime.TryParseExact(PublishAt.Trim(), ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                publishAt = new DateTimeOffset(at, TimeSpan.Zero);
            }
            else
            {
                problem ??= "The publish time isn't a date and time such as 2026-11-10 09:00.";
            }
        }

        if (!string.IsNullOrWhiteSpace(Language) && !SiteCulture.IsKnown(Language))
        {
            problem ??= "The language must be a code such as en, en-GB or hu.";
        }

        error = problem;
        if (error is not null)
        {
            return null;
        }

        return new ContentDraft
        {
            Kind = Kind == "page" ? ContentKind.Page : ContentKind.Post,
            Title = (Title ?? "").Trim(),
            Slug = (Slug ?? "").Trim(),
            Language = string.IsNullOrWhiteSpace(Language) ? language : Language.Trim(),
            Summary = string.IsNullOrWhiteSpace(Summary) ? null : Summary.Trim(),
            Date = date,
            Updated = updated,
            AuthorId = string.IsNullOrWhiteSpace(AuthorId) ? null : AuthorId,
            Tags = [.. (Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            Status = Status switch { "published" => ContentStatus.Published, "scheduled" => ContentStatus.Scheduled, "archived" => ContentStatus.Archived, _ => ContentStatus.Draft },
            PublishAt = Status == "scheduled" ? publishAt : null,
            Image = string.IsNullOrWhiteSpace(Image) ? null : Image.Trim(),
            ImageAlt = string.IsNullOrWhiteSpace(ImageAlt) ? null : ImageAlt.Trim(),
            ShowInMenu = Kind == "page" && Menu,
            RedirectFrom = [.. (RedirectFrom ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            Body = Body ?? "",
        };
    }
}

/// <summary>Who may edit what (brief: admins everything, authors their own posts).</summary>
internal static class EditorRights
{
    /// <summary>Gets the author ID of an editor: opaque, so their address never lands in content.</summary>
    /// <param name="email">The editor's address.</param>
    /// <returns>Such as <c>editor-1a2b3c4d5e6f</c>.</returns>
    public static string AuthorId(string email) =>
        "editor-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ConfiguredAdmins.Normalize(email))))[..12];

    /// <summary>Gets the signed-in editor's author ID.</summary>
    /// <param name="user">The editor.</param>
    /// <returns>The ID, or null when signed out.</returns>
    public static string? AuthorId(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.Email)?.Value is { } email ? AuthorId(email) : null;

    /// <summary>Gets whether an editor may edit an item.</summary>
    /// <param name="user">The editor.</param>
    /// <param name="kind">The item's kind.</param>
    /// <param name="authorId">The item's author, or null for a new item.</param>
    /// <param name="isNew">Whether the item is new.</param>
    /// <returns>True for admins; for authors, new posts and their own.</returns>
    public static bool CanEdit(ClaimsPrincipal user, ContentKind kind, string? authorId, bool isNew) =>
        user.IsInRole(nameof(EditorRole.Admin))
        || (user.IsInRole(nameof(EditorRole.Author)) && kind == ContentKind.Post
            && (isNew || string.Equals(authorId, AuthorId(user), StringComparison.Ordinal)));
}

/// <summary>
/// The editor field an error is about (F-0081), from the message the form, the save or the
/// parser gave, so the page can mark the field invalid and link to it.
/// </summary>
internal static class EditorErrors
{
    /// <summary>Gets the id of the field an error message is about.</summary>
    /// <param name="message">The message.</param>
    /// <returns>A field id such as <c>slug</c>, or null when the message isn't about one field.</returns>
    public static string? FieldOf(string? message) => message switch
    {
        null => null,
        _ when message.StartsWith("title", StringComparison.OrdinalIgnoreCase) => "title",
        _ when message.Contains("slug", StringComparison.OrdinalIgnoreCase) => "slug",
        _ when message.StartsWith("The update date", StringComparison.Ordinal) => "updated",
        _ when message.StartsWith("The date", StringComparison.Ordinal) || message == "a post needs a date" => "date",
        _ when message.Contains("publish", StringComparison.OrdinalIgnoreCase) => "publish-at",
        _ when message.StartsWith("The language", StringComparison.Ordinal) => "language",
        _ when message.StartsWith("tag ", StringComparison.Ordinal) => "tags",
        _ when message.Contains("alt text", StringComparison.Ordinal) => "body",
        _ => null,
    };
}
