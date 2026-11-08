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
            return Fail(sourcePath, $"kind must be post or page, not '{fm.Kind}'");
        }

        if (string.IsNullOrWhiteSpace(fm.Title))
        {
            return Fail(sourcePath, "title is missing");
        }

        if (!Slugs.IsValid(fm.Slug))
        {
            return Fail(sourcePath, $"slug '{fm.Slug}' isn't lowercase letters, digits and single hyphens");
        }

        if (kind == ContentKind.Post && fm.Date is null)
        {
            return Fail(sourcePath, "a post needs a date");
        }

        var tags = (fm.Tags ?? []).Select(t => t.Trim()).ToList();
        var badTag = tags.FirstOrDefault(t => !Slugs.IsValid(t));
        if (badTag is not null)
        {
            return Fail(sourcePath, $"tag '{badTag}' isn't lowercase letters, digits and single hyphens");
        }

        var previous = fm.RedirectFrom ?? [];
        var badPrevious = previous.FirstOrDefault(p => !Slugs.IsValid(p));
        if (badPrevious is not null)
        {
            return Fail(sourcePath, $"redirect_from '{badPrevious}' isn't a valid slug");
        }

        var item = new ContentItem(kind.Value, fm.Slug!, fm.Title.Trim(), fm.Lang ?? defaultLanguage, body, sourcePath)
        {
            Summary = fm.Summary?.Trim(),
            Date = fm.Date is { } d ? DateOnly.FromDateTime(d) : null,
            Updated = fm.Updated is { } u ? DateOnly.FromDateTime(u) : null,
            AuthorId = fm.Author,
            Tags = tags.Distinct(StringComparer.Ordinal).ToList(),
            TranslationGroup = fm.Translation,
            ShowInMenu = fm.Menu ?? false,
            PreviousSlugs = previous,
            Image = fm.Image,
            ImageAlt = fm.ImageAlt,
        };
        return new ParseResult(item, null);
    }

    private static ParseResult Fail(string sourcePath, string reason) => new(null, $"{sourcePath}: {reason}");
}
