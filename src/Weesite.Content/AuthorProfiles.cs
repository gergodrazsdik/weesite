using System.Text.Json;
using Weesite.Content.Sources;

namespace Weesite.Content;

/// <summary>
/// The editors' author profiles (ADR 0005): name, short bio and links, in <c>settings/authors.json</c>
/// by author ID (<c>editor-</c> and a hash, never an address). They're loaded with the content, so
/// bylines on the public site, in feeds and in JSON-LD come from the catalog like any author's.
/// </summary>
public sealed class AuthorProfiles(IContentStore store, ContentReloader reloader)
{
    // Names as written ("Gergő", not "Gerg\u0151"), as site.json is (F-0120).
    private static readonly JsonSerializerOptions WriteOptions = new(SiteSettings.JsonOptions)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    /// <summary>Parses <c>authors.json</c>; text that isn't a list of authors gives none, as does a <c>null</c> where a value belongs (F-0235).</summary>
    /// <param name="json">The file's text.</param>
    /// <returns>The profiles.</returns>
    public static IReadOnlyList<Author> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Author>>(json, SiteSettings.JsonOptions) is { } authors
                ? Author.Safe(authors).Where(a => a.Id.Length > 0).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Finds a profile.</summary>
    /// <param name="id">The author ID.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The profile, or null.</returns>
    public async Task<Author?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var file = await store.ReadSettingsFileAsync(ContentContainers.AuthorsFile, cancellationToken);
        return Parse(file?.Text).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// Saves a profile, replacing the one with its ID, then reloads. Each editor owns one entry, so
    /// when another save got in first, the file is read again and the entry applied to it.
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <returns>Saved, unchanged (the stored profile already says this: nothing written), unreadable (the stored file doesn't parse: nothing written), or a conflict after repeated tries.</returns>
    public async Task<SettingsSaveResult> SaveAsync(Author profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var wanted = JsonSerializer.Serialize(profile, WriteOptions);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var file = await store.ReadSettingsFileAsync(ContentContainers.AuthorsFile, cancellationToken);
            var stored = Parse(file?.Text);
            // A file that's there but isn't a list of authors isn't empty: writing it as one profile
            // would drop everyone else's (F-0235). Someone fixes or restores it first.
            if (stored.Count == 0 && !IsEmptyList(file?.Text))
            {
                return SettingsSaveResult.Unreadable;
            }

            // Saving the profile as it is would only add a version and an audit entry (F-0175).
            if (stored.FirstOrDefault(a => string.Equals(a.Id, profile.Id, StringComparison.Ordinal)) is { } current
                && JsonSerializer.Serialize(current, WriteOptions) == wanted)
            {
                return SettingsSaveResult.Unchanged;
            }

            var profiles = stored.Where(a => !string.Equals(a.Id, profile.Id, StringComparison.Ordinal)).Append(profile).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
            var text = JsonSerializer.Serialize(profiles, WriteOptions) + "\n";
            if (await store.WriteSettingsFileAsync(ContentContainers.AuthorsFile, text, file?.ETag, cancellationToken) is not null)
            {
                await reloader.ReloadAfterWriteAsync(store);
                return SettingsSaveResult.Saved;
            }
        }

        return SettingsSaveResult.Conflict;
    }

    // No file, an empty one, or an empty JSON list: nothing to keep.
    private static bool IsEmptyList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is System.Text.Json.Nodes.JsonArray { Count: 0 };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
