using Weesite.Content.Sources;

namespace Weesite.Content;

/// <summary>What happened to a save: <see cref="ContentSaved"/>, <see cref="ContentConflict"/> or <see cref="ContentInvalid"/>.</summary>
public abstract record SaveOutcome;

/// <summary>The item was saved.</summary>
/// <param name="Path">Its path in the content store.</param>
/// <param name="ETag">Its new ETag, for the next save.</param>
public sealed record ContentSaved(string Path, string ETag) : SaveOutcome;

/// <summary>Someone saved the item since it was opened; nothing was written.</summary>
public sealed record ContentConflict : SaveOutcome;

/// <summary>The draft isn't valid; nothing was written.</summary>
/// <param name="Message">What's wrong.</param>
public sealed record ContentInvalid(string Message) : SaveOutcome;

/// <summary>An item opened for editing.</summary>
/// <param name="Path">Its path in the content store.</param>
/// <param name="Draft">Its fields.</param>
/// <param name="ETag">The version opened, for the conflict check on save.</param>
public sealed record OpenedItem(string Path, ContentDraft Draft, string ETag);

/// <summary>
/// Opens and saves posts and pages for the editor (ADR 0005). A save is checked by the same parser
/// the public site uses, keeps slugs unique, turns a changed slug of a public item into a redirect,
/// writes only if nobody saved meanwhile (ETag), bumps the version marker for other replicas and
/// reloads this one at once. A file keeps the path it was created with, so its revisions stay one
/// history when its slug changes.
/// </summary>
public sealed class ContentEditor(
    IContentStore store,
    ContentCatalogProvider content,
    ContentLoader loader,
    ContentReloader reloader,
    TimeProvider time,
    Markdown.MarkdownRenderer renderer)
{
    /// <summary>Opens an item.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The item, or null when there's none or it doesn't parse.</returns>
    public async Task<OpenedItem?> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var file = await store.ReadAsync(path, cancellationToken);
        if (file is null || ContentParser.Parse(path, file.Text, content.Current.Site.Language).Item is not { } item)
        {
            return null;
        }

        return new OpenedItem(path, ContentDraft.From(item), file.ETag);
    }

    /// <summary>Saves a draft.</summary>
    /// <param name="path">The item's path, or null for a new item.</param>
    /// <param name="draft">The fields.</param>
    /// <param name="etag">The ETag it was opened at; ignored for a new item.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <returns>What happened.</returns>
    public async Task<SaveOutcome> SaveAsync(string? path, ContentDraft draft, string? etag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var catalog = content.Current;
        var now = time.GetUtcNow();
        draft = draft with
        {
            Slug = draft.Slug.Trim(),
            Tags = [.. draft.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)],
        };

        if (path is not null)
        {
            var current = await store.ReadAsync(path, cancellationToken);
            if (current is null)
            {
                return new ContentInvalid("The item no longer exists.");
            }

            // A public item's old slug keeps working as a redirect.
            if (ContentParser.Parse(path, current.Text, catalog.Site.Language).Item is { } before
                && before.Slug != draft.Slug && before.IsPublicAt(now))
            {
                draft = draft with { RedirectFrom = [.. draft.RedirectFrom, before.Slug] };
            }
        }

        draft = draft with { RedirectFrom = [.. draft.RedirectFrom.Where(s => s != draft.Slug).Distinct(StringComparer.Ordinal)] };
        if (draft.Slug.Length > Slugs.MaxLength)
        {
            return new ContentInvalid($"The slug can be at most {Slugs.MaxLength} characters long; this one has {draft.Slug.Length}.");
        }

        var reserved = draft.Kind == ContentKind.Page ? Slugs.Reserved : Slugs.ReservedForPosts;
        if (reserved.Contains(draft.Slug))
        {
            return new ContentInvalid($"The slug '{draft.Slug}' is reserved for the site.");
        }

        var target = path ?? await NewPathAsync(draft, cancellationToken);
        var text = draft.ToMarkdown();
        if (ContentParser.Parse(target, text, catalog.Site.Language).Error is { } error)
        {
            return new ContentInvalid(error[(error.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
        }

        // Every image needs a description (brief, Images and attachments).
        var missingAlt = renderer.Render(draft.Body, catalog.Media).Problems.Where(p => p.EndsWith("has no alt text", StringComparison.Ordinal)).ToList();
        if (missingAlt.Count > 0)
        {
            return new ContentInvalid($"Every image needs a description (alt text) in its brackets: ![what it shows](/media/...). Missing: {string.Join("; ", missingAlt)}.");
        }

        var taken = catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished)
            .FirstOrDefault(i => i.Kind == draft.Kind && i.Slug == draft.Slug && i.SourcePath != target);
        if (taken is not null)
        {
            return new ContentInvalid($"The slug '{draft.Slug}' is already used by \"{taken.Title}\".");
        }

        var saved = await store.WriteAsync(target, text, path is null ? null : etag, cancellationToken);
        if (saved is null)
        {
            return new ContentConflict();
        }

        var version = await store.BumpVersionAsync(cancellationToken);
        await loader.LoadAsync(cancellationToken);
        reloader.Acknowledge(version);
        return new ContentSaved(target, saved);
    }

    /// <summary>
    /// Deletes an item for good (T-0110): its file goes, so it leaves the site and the editor,
    /// and its versions are no longer listed (storage keeps them for its retention period).
    /// </summary>
    /// <param name="path">Its path.</param>
    /// <param name="etag">The ETag it was opened at; a newer save isn't deleted.</param>
    /// <param name="cancellationToken">Stops the delete.</param>
    /// <returns>True when it was deleted; false when it changed or was gone.</returns>
    public async Task<bool> DeleteAsync(string path, string etag, CancellationToken cancellationToken)
    {
        if (!await store.DeleteAsync(path, etag, cancellationToken))
        {
            return false;
        }

        var version = await store.BumpVersionAsync(cancellationToken);
        await loader.LoadAsync(cancellationToken);
        reloader.Acknowledge(version);
        return true;
    }

    /// <summary>Lists an item's earlier versions.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>Newest first.</returns>
    public Task<IReadOnlyList<Revision>> RevisionsAsync(string path, CancellationToken cancellationToken) =>
        store.ListRevisionsAsync(path, cancellationToken);

    /// <summary>Opens an earlier version of an item as a draft, to restore by saving it.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="versionId">The version.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>That version's fields, or null.</returns>
    public async Task<ContentDraft?> OpenRevisionAsync(string path, string versionId, CancellationToken cancellationToken) =>
        await store.ReadRevisionAsync(path, versionId, cancellationToken) is { } text
            && ContentParser.Parse(path, text, content.Current.Site.Language).Item is { } item
            ? ContentDraft.From(item)
            : null;

    // posts/{slug}.md, or with -2, -3... when an earlier item already has that file.
    private async Task<string> NewPathAsync(ContentDraft draft, CancellationToken cancellationToken)
    {
        var folder = draft.Kind == ContentKind.Page ? "pages" : "posts";
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{folder}/{draft.Slug}.md" : $"{folder}/{draft.Slug}-{n}.md";
            if (await store.ReadAsync(candidate, cancellationToken) is null)
            {
                return candidate;
            }
        }
    }
}
