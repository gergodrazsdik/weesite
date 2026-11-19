using Weesite.Content.Sources;

namespace Weesite.Content;

/// <summary>What happened to a save: <see cref="ContentSaved"/>, <see cref="ContentConflict"/> or <see cref="ContentInvalid"/>.</summary>
public abstract record SaveOutcome;

/// <summary>The item was saved.</summary>
/// <param name="Path">Its path in the content store.</param>
/// <param name="ETag">Its new ETag, for the next save.</param>
/// <param name="Unchanged">Whether the stored file already had this text, so nothing was written and no version was made.</param>
public sealed record ContentSaved(string Path, string ETag, bool Unchanged = false) : SaveOutcome;

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
/// Told about each post or page a save or delete changed (T-0146), after it's written and loaded:
/// the item as it was and as it is now, either null when there was or is none. Unchanged saves
/// aren't changes. Mustn't throw or wait: it runs at the end of the save.
/// </summary>
public interface IContentChanges
{
    /// <summary>An item changed.</summary>
    /// <param name="before">The item before, or null for a new one.</param>
    /// <param name="after">The item now, or null when it was deleted.</param>
    void Changed(ContentItem? before, ContentItem? after);
}

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
    ContentReloader reloader,
    TimeProvider time,
    Markdown.MarkdownRenderer renderer,
    IEnumerable<IContentChanges> changes)
{
    /// <summary>Opens an item.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The item, or null when there's none or it doesn't parse.</returns>
    public async Task<OpenedItem?> OpenAsync(string path, CancellationToken cancellationToken)
    {
        if (!ContentPaths.IsValid(path))
        {
            return null;
        }

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
    public Task<SaveOutcome> SaveAsync(string? path, ContentDraft draft, string? etag, CancellationToken cancellationToken) =>
        SaveAsync(path, draft, etag, 1, cancellationToken);

    /// <summary>Saves a draft, naming lines as the file the editor sent counts them.</summary>
    /// <param name="path">The item's path, or null for a new item.</param>
    /// <param name="draft">The fields.</param>
    /// <param name="etag">The ETag it was opened at; ignored for a new item.</param>
    /// <param name="firstLine">The line the text starts on in what the editor sent: 1 for the text alone, after the front matter for a whole file.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <returns>What happened.</returns>
    public async Task<SaveOutcome> SaveAsync(string? path, ContentDraft draft, string? etag, int firstLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var catalog = content.Current;
        var now = time.GetUtcNow();
        draft = draft with
        {
            Slug = draft.Slug.Trim(),
            Tags = [.. draft.Tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)],
        };

        if (path is not null && !ContentPaths.IsValid(path))
        {
            return new ContentInvalid("That isn't a post's or page's file.");
        }

        StoredFile? current = null;
        ContentItem? before = null;
        if (path is not null)
        {
            current = await store.ReadAsync(path, cancellationToken);
            if (current is null)
            {
                return new ContentInvalid("The item no longer exists.");
            }

            // A public item's old slug keeps working as a redirect.
            before = ContentParser.Parse(path, current.Text, catalog.Site.Language).Item;
            if (before is not null && before.Slug != draft.Slug && before.IsPublicAt(now) && !before.IsSpecial && draft.Slug.Length > 0)
            {
                draft = draft with { RedirectFrom = [.. draft.RedirectFrom, before.Slug] };
            }
        }

        draft = draft with { RedirectFrom = [.. draft.RedirectFrom.Where(s => s != draft.Slug).Distinct(StringComparer.Ordinal)] };
        // As the editor's counter says: search engines show about 160 characters (F-0125).
        if (draft.Summary?.Length > ContentDraft.MaxSummaryLength)
        {
            return new ContentInvalid($"The summary can be at most {ContentDraft.MaxSummaryLength} characters; this one has {draft.Summary.Length}. Search engines and feeds show about that many.");
        }

        if (draft.Body.Length > ContentDraft.MaxBodyLength)
        {
            return new ContentInvalid($"The text can be at most {ContentDraft.MaxBodyLength:N0} characters; this one has {draft.Body.Length:N0}. Split it into parts.");
        }

        if (draft.Slug.Length > Slugs.MaxLength)
        {
            return new ContentInvalid($"The slug can be at most {Slugs.MaxLength} characters long; this one has {draft.Slug.Length}.");
        }

        var reserved = draft.Kind == ContentKind.Page ? Slugs.Reserved : Slugs.ReservedForPosts;
        if (reserved.Contains(draft.Slug) && !(before?.Slug == draft.Slug && Slugs.SpecialAddresses.Contains(draft.Slug)))
        {
            return new ContentInvalid($"The slug '{draft.Slug}' is reserved for the site.");
        }

        var target = path ?? await NewPathAsync(draft, catalog, cancellationToken);
        if (target is null)
        {
            return new ContentInvalid($"The slug '{draft.Slug}' was used by an item saved a moment ago on another server; choose another, or reload to see it.");
        }

        var text = draft.ToMarkdown();
        var parsed = ContentParser.Parse(target, text, catalog.Site.Language);
        if (parsed.Error is { } error)
        {
            return new ContentInvalid(ContentParser.Reason(error));
        }

        // Every image needs a description (brief, Images and attachments).
        var missingAlt = renderer.Render(draft.Body, catalog.Media, firstLine).MissingAlt;
        if (missingAlt.Count > 0)
        {
            return new ContentInvalid($"Every image needs a description (alt text) in its brackets: ![what it shows](/media/...). Missing: {string.Join("; ", missingAlt)}.");
        }

        var taken = draft.Slug.Length == 0 ? null : catalog.Posts.Concat(catalog.Pages).Concat(catalog.Unpublished)
            .FirstOrDefault(i => i.Kind == draft.Kind && i.Slug == draft.Slug && i.SourcePath != target);
        if (taken is not null)
        {
            return new ContentInvalid($"The slug '{draft.Slug}' is already used by \"{taken.Title}\".");
        }

        // The version opened already says this: a write would only add a version that's the same as
        // the last, and an audit entry for nothing (F-0174). At an older ETag it's still a conflict,
        // as any save there is. A file written by hand or by an app counts as the editor would write
        // it: quotes, the order of the fields or a list's style alone aren't a change (F-0177).
        if (current is not null && current.ETag == etag
            && (string.Equals(current.Text, text, StringComparison.Ordinal) || string.Equals(AsWritten(target, current.Text, catalog.Site.Language), text, StringComparison.Ordinal)))
        {
            return new ContentSaved(target, current.ETag, Unchanged: true);
        }

        var saved = await store.WriteAsync(target, text, path is null ? null : etag, cancellationToken);
        if (saved is null)
        {
            return new ContentConflict();
        }

        // Written: the rest isn't stopped with the request, or no replica would reload (F-0114).
        await reloader.ReloadAfterWriteAsync(store);
        Tell(before, parsed.Item);
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
        if (!ContentPaths.IsValid(path))
        {
            return false;
        }

        var stored = await store.ReadAsync(path, cancellationToken);
        if (!await store.DeleteAsync(path, etag, cancellationToken))
        {
            return false;
        }

        await reloader.ReloadAfterWriteAsync(store);
        Tell(stored is null ? null : ContentParser.Parse(path, stored.Text, content.Current.Site.Language).Item, null);
        return true;
    }

    /// <summary>Lists an item's earlier versions.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>Newest first.</returns>
    public Task<IReadOnlyList<Revision>> RevisionsAsync(string path, CancellationToken cancellationToken) =>
        ContentPaths.IsValid(path) ? store.ListRevisionsAsync(path, cancellationToken) : Task.FromResult<IReadOnlyList<Revision>>([]);

    /// <summary>Opens an earlier version of an item as a draft, to restore by saving it.</summary>
    /// <param name="path">Its path.</param>
    /// <param name="versionId">The version.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>That version's fields, or null.</returns>
    /// <remarks>
    /// Only the versions listed for the item now: those of the file's current life. A file deleted
    /// and created again keeps its earlier life's versions in storage for a while, and whoever
    /// creates it again mustn't read them by their IDs (T-0122).
    /// </remarks>
    public async Task<ContentDraft?> OpenRevisionAsync(string path, string versionId, CancellationToken cancellationToken) =>
        ContentPaths.IsValid(path)
            && (await store.ListRevisionsAsync(path, cancellationToken)).Any(r => r.VersionId == versionId)
            && await store.ReadRevisionAsync(path, versionId, cancellationToken) is { } text
            && ContentParser.Parse(path, text, content.Current.Site.Language).Item is { } item
            ? ContentDraft.From(item)
            : null;

    private void Tell(ContentItem? before, ContentItem? after)
    {
        foreach (var listener in changes)
        {
            listener.Changed(before, after);
        }
    }

    // A stored file as the editor writes it, or null when it doesn't parse.
    private static string? AsWritten(string path, string text, string language) =>
        ContentParser.Parse(path, text, language).Item is { } item ? ContentDraft.From(item).ToMarkdown() : null;

    // posts/{slug}.md, or with -2, -3... when an earlier item already has that file.
    // A free file name for a new item; null when a file this replica hasn't loaded yet (saved a
    // moment ago on another one) already has the slug, which the catalog's check can't see (F-0119).
    private async Task<string?> NewPathAsync(ContentDraft draft, ContentCatalog catalog, CancellationToken cancellationToken)
    {
        var folder = draft.Kind == ContentKind.Page ? "pages" : "posts";
        var known = catalog.Posts.Concat(catalog.Pages).Concat(catalog.SpecialPages).Concat(catalog.Unpublished).Select(i => i.SourcePath).ToHashSet(StringComparer.Ordinal);

        // A special page has no slug: its file is named after its title (T-0159).
        var name = draft.Slug.Length > 0 ? draft.Slug : Slugs.FromTitle(draft.Title, "page");
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{folder}/{name}.md" : $"{folder}/{name}-{n}.md";
            if (await store.ReadAsync(candidate, cancellationToken) is not { } existing)
            {
                return candidate;
            }

            if (draft.Slug.Length > 0 && !known.Contains(candidate) && ContentParser.Parse(candidate, existing.Text, catalog.Site.Language).Item?.Slug == draft.Slug)
            {
                return null;
            }
        }
    }
}
