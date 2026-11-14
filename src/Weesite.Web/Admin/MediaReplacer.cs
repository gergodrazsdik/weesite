using Weesite.Content;
using Weesite.Content.Media;

namespace Weesite.Web.Admin;

/// <summary>What a replacement did (F-0143).</summary>
internal abstract record ReplaceOutcome
{
    /// <summary>The new file is stored and every post and page that used the old one uses it now.</summary>
    /// <param name="Item">The new file.</param>
    /// <param name="Updated">The posts and pages changed, by path.</param>
    /// <param name="Failed">Those that couldn't be changed (saved by someone else twice), by path; the old file stays unarchived then.</param>
    internal sealed record Replaced(MediaItem Item, IReadOnlyList<string> Updated, IReadOnlyList<string> Failed) : ReplaceOutcome;

    /// <summary>Nothing changed.</summary>
    /// <param name="Reason">Why.</param>
    internal sealed record Refused(string Reason) : ReplaceOutcome;
}

/// <summary>
/// Replaces a media file (F-0143). Media addresses carry the file's hash and are cached for a year
/// as unchangeable, so a new file can't take the old address. Instead the new file is stored under
/// its own, every post and page that used the old address (drafts, scheduled and archived ones
/// included) is saved with the new one as a new version, and the old file is archived: still served
/// for anything outside the site that links to it, but no longer offered.
/// </summary>
internal sealed class MediaReplacer(MediaLibrary library, ContentEditor editor, ContentCatalogProvider content)
{
    /// <summary>Replaces a file.</summary>
    /// <param name="oldName">The stored file's name.</param>
    /// <param name="fileName">The new file's name, as uploaded.</param>
    /// <param name="data">Its bytes.</param>
    /// <param name="alt">An image's description; empty keeps the old one's.</param>
    /// <param name="cancellationToken">Stops it.</param>
    /// <returns>What happened.</returns>
    public async Task<ReplaceOutcome> ReplaceAsync(string oldName, string fileName, byte[] data, string? alt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var files = await library.ListAsync(cancellationToken);
        if (files.FirstOrDefault(f => f.Name == oldName) is not { } old)
        {
            return new ReplaceOutcome.Refused($"There's no file {oldName} to replace.");
        }

        // An image stays an image: posts show it with ![…](…), which a PDF or ZIP wouldn't fill.
        if (ImageProcessor.IsProcessable(fileName) != old.IsImage)
        {
            return new ReplaceOutcome.Refused(old.IsImage
                ? "Replace an image with an image (JPEG, PNG or WebP)."
                : "Replace a file that isn't an image with another such file.");
        }

        var outcome = await library.UploadAsync(fileName, data, cancellationToken, string.IsNullOrWhiteSpace(alt) ? old.Alt : alt);
        if (outcome is not MediaUploaded { Item: var item })
        {
            return new ReplaceOutcome.Refused(((MediaRejected)outcome).Reason);
        }

        if (item.Name == oldName)
        {
            return new ReplaceOutcome.Refused("The new file is the same as the stored one.");
        }

        List<string> updated = [];
        List<string> failed = [];
        foreach (var use in content.Current.UsesOf(oldName))
        {
            (await PointAtAsync(use.SourcePath, oldName, item.Name, cancellationToken) ? updated : failed).Add(use.SourcePath);
        }

        if (failed.Count == 0)
        {
            await library.SetArchivedAsync(oldName, true, cancellationToken);
        }

        return new ReplaceOutcome.Replaced(item, updated, failed);
    }

    // Saves an item with the new address in its text and image; again once if someone saved it
    // meanwhile. An item that no longer uses the old address counts as done.
    private async Task<bool> PointAtAsync(string path, string oldName, string newName, CancellationToken cancellationToken)
    {
        var from = "/media/" + oldName;
        var to = "/media/" + newName;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (await editor.OpenAsync(path, cancellationToken) is not { } opened)
            {
                return false;
            }

            var draft = opened.Draft;
            if (!draft.Body.Contains(from, StringComparison.Ordinal) && draft.Image != from)
            {
                return true;
            }

            var changed = draft with
            {
                Body = draft.Body.Replace(from, to, StringComparison.Ordinal),
                Image = draft.Image == from ? to : draft.Image,
            };
            switch (await editor.SaveAsync(path, changed, opened.ETag, cancellationToken))
            {
                case ContentSaved:
                    return true;
                case ContentInvalid:
                    return false;
            }
        }

        return false;
    }
}
