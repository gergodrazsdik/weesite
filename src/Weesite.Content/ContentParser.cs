using System.Globalization;

namespace Weesite.Content;

/// <summary>The outcome of parsing one content file: an item, or why it was skipped.</summary>
/// <param name="Item">The parsed item, if the file was valid.</param>
/// <param name="Error">Why the file was skipped, if it wasn't.</param>
public sealed record ParseResult(ContentItem? Item, string? Error);

/// <summary>Parses Markdown files with front matter into content items.</summary>
public static class ContentParser
{
    /// <summary>Parses one file. Invalid files are reported, not thrown, so one bad file can't take the site down.</summary>
    /// <param name="sourcePath">The file's path relative to the content root.</param>
    /// <param name="text">The file's text.</param>
    /// <param name="defaultLanguage">The language when front matter has none.</param>
    /// <returns>The item, or the reason it was skipped.</returns>
    public static ParseResult Parse(string sourcePath, string text, string defaultLanguage = "en")
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(text);

        if (!FrontMatter.TrySplit(text, out var fm, out var body, out var error))
        {
            return Fail(sourcePath, error!);
        }

        var kind = fm.Kind switch
        {
            null or "post" => ContentKind.Post,
            "page" => ContentKind.Page,
            _ => (ContentKind?)null,
        };
        if (kind is null)
        {
            return Fail(sourcePath, $"The kind (kind) must be post or page, not '{fm.Kind}'.");
        }

        if (string.IsNullOrWhiteSpace(fm.Title))
        {
            return Fail(sourcePath, "The title is missing.");
        }

        if (!Slugs.IsValid(fm.Slug))
        {
            return Fail(sourcePath, $"The slug '{fm.Slug}' isn't lowercase letters, digits and single hyphens.");
        }

        var (date, dateOk) = Day(fm.Date);
        if (!dateOk)
        {
            return Fail(sourcePath, $"The date '{fm.Date}' isn't a date such as 2026-11-09.");
        }

        var (updated, updatedOk) = Day(fm.Updated);
        if (!updatedOk)
        {
            return Fail(sourcePath, $"The update date (updated) '{fm.Updated}' isn't a date such as 2026-11-09.");
        }

        if (kind == ContentKind.Post && date is null)
        {
            return Fail(sourcePath, "A post needs a date.");
        }

        // An empty entry (`tags: [a, ~]`) is null: a problem with the file, not an error (F-0108).
        if (fm.Tags?.Any(t => t is null) == true || fm.RedirectFrom?.Any(p => p is null) == true)
        {
            return Fail(sourcePath, "A list in the front matter (tags or redirect_from) has an empty entry.");
        }

        var tags = (fm.Tags ?? []).Select(t => t.Trim()).ToList();
        var badTag = tags.FirstOrDefault(t => !Slugs.IsValid(t));
        if (badTag is not null)
        {
            return Fail(sourcePath, $"The tag '{badTag}' isn't lowercase letters, digits and single hyphens.");
        }

        var previous = fm.RedirectFrom ?? [];
        var badPrevious = previous.FirstOrDefault(p => !Slugs.IsValid(p));
        if (badPrevious is not null)
        {
            return Fail(sourcePath, $"The earlier address (redirect_from) '{badPrevious}' isn't a valid slug.");
        }

        var status = fm.Status?.Trim() switch
        {
            null or "" or "published" => ContentStatus.Published,
            "draft" => ContentStatus.Draft,
            "scheduled" => ContentStatus.Scheduled,
            "archived" => ContentStatus.Archived,
            _ => (ContentStatus?)null,
        };
        if (status is null)
        {
            return Fail(sourcePath, $"The status must be published, draft, scheduled or archived, not '{fm.Status}'.");
        }

        DateTimeOffset? publishAt = null;
        if (fm.PublishAt is { } at)
        {
            // An explicit offset ("Z" or "+01:00"), so a scheduled time is never ambiguous.
            if (!DateTimeOffset.TryParse(at, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
                || !HasOffset(at.Trim()))
            {
                return Fail(sourcePath, $"The publish time (publish_at) '{at}' isn't a time with an offset, such as 2026-11-10T09:00:00Z.");
            }

            publishAt = parsed;
        }

        if (status == ContentStatus.Scheduled && publishAt is null)
        {
            return Fail(sourcePath, "A scheduled item needs a publish time (publish_at).");
        }

        var item = new ContentItem(kind.Value, fm.Slug!, fm.Title.Trim(), fm.Lang ?? defaultLanguage, body, sourcePath)
        {
            Summary = fm.Summary?.Trim(),
            Date = date,
            Updated = updated,
            AuthorId = fm.Author,
            Tags = tags.Distinct(StringComparer.Ordinal).ToList(),
            ShowInMenu = fm.Menu ?? false,
            PreviousSlugs = previous,
            Image = fm.Image,
            ImageAlt = fm.ImageAlt,
            Status = status.Value,
            PublishAt = publishAt,
        };
        return new ParseResult(item, null);
    }

    private static ParseResult Fail(string sourcePath, string reason) => new(null, $"{sourcePath}: {reason}");

    /// <summary>Gets a parse error without the file's path in front, for an editor or an app.</summary>
    /// <param name="error">The error, <c>path: reason</c>.</param>
    /// <returns>The reason; the whole error when there's no path in front (F-0119).</returns>
    public static string Reason(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var at = error.IndexOf(": ", StringComparison.Ordinal);
        return at < 0 ? error : error[(at + 2)..];
    }

    // The day as written: 2026-11-09, or a time (with or without an offset), whose day is taken in
    // its own offset, never the server's time zone (F-0116). Ok is false for text that isn't one.
    private static (DateOnly? Day, bool Ok) Day(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, true);
        }

        var trimmed = text.Trim();
        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return (day, true);
        }

        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? (DateOnly.FromDateTime(time.DateTime), true)
            : (null, false);
    }

    // Ends in "Z" or an offset such as "+01:00" or "-05:00".
    private static bool HasOffset(string time) =>
        (time.Length > 0 && time[^1] is 'Z' or 'z')
        || (time.Length > 6 && time[^6] is '+' or '-' && time[^3] == ':');
}
