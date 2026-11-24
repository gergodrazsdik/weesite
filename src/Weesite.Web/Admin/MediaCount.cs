using Weesite.Content;
using Weesite.Content.Media;

namespace Weesite.Web.Admin;

/// <summary>
/// The sidebar's media count, as the media page's All tab counts (archived files left out, F-0120),
/// without listing the whole media container on every admin page (F-0248). It's kept for a minute,
/// and worked out again sooner whenever the files change: an upload, a replacement, a delete, an
/// archive or bringing one back, from the admin or an app (<see cref="MediaLibrary.Changes"/>), or
/// when the content reloads (an import, a change on another replica).
/// </summary>
/// <param name="library">The media library.</param>
/// <param name="content">The content, whose reloads follow every media write.</param>
/// <param name="time">The clock.</param>
internal sealed class MediaCount(MediaLibrary library, ContentCatalogProvider content, TimeProvider time)
{
    /// <summary>How long a count is kept.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private Counted? _last;

    /// <summary>Gets the number of files that aren't archived.</summary>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <returns>The count.</returns>
    public async Task<int> GetAsync(CancellationToken cancellationToken)
    {
        // Read before the listing: a change during it raises the number, so the next page counts again.
        var catalog = content.Current;
        var changes = library.Changes;
        var now = time.GetUtcNow();
        if (_last is { } last && ReferenceEquals(last.Catalog, catalog) && last.Changes == changes && now - last.At < Lifetime)
        {
            return last.Count;
        }

        var count = (await library.ListAsync(cancellationToken)).Count(f => !f.IsArchived);
        _last = new Counted(catalog, changes, now, count);
        return count;
    }

    private sealed record Counted(ContentCatalog Catalog, long Changes, DateTimeOffset At, int Count);
}
