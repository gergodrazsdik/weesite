using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Weesite.Content.Sources;

namespace Weesite.Content;

/// <summary><c>site.json</c> as the settings editor opens it.</summary>
/// <param name="Settings">The parsed settings.</param>
/// <param name="ETag">The version read, for the conflict check on save; null when there's no file yet.</param>
public sealed record OpenedSettings(SiteSettings Settings, string? ETag);

/// <summary>What a settings save did.</summary>
public enum SettingsSaveResult
{
    /// <summary>Someone saved in between: nothing was written.</summary>
    Conflict,

    /// <summary>The change was written.</summary>
    Saved,

    /// <summary>The change left the settings as they were: nothing was written, so no version was made (F-0175).</summary>
    Unchanged,

    /// <summary>The stored file doesn't parse, so writing would drop what it holds: nothing was written (F-0235).</summary>
    Unreadable,
}

/// <summary>
/// Changes <c>settings/site.json</c> for the settings, menu and theme editors (T-0049): each
/// change sets only its own keys and keeps the rest of the file, writes only if nobody saved since
/// it was opened (ETag), then bumps the version marker and reloads, as content saves do.
/// </summary>
public sealed class SiteSettingsEditor(IContentStore store, ContentReloader reloader)
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly JsonSerializerOptions WriteOptions = new(SiteSettings.JsonOptions)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Opens the settings.</summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The settings and their version.</returns>
    public async Task<OpenedSettings> OpenAsync(CancellationToken cancellationToken)
    {
        var file = await store.ReadSettingsFileAsync(ContentContainers.SiteSettingsFile, cancellationToken);
        return new OpenedSettings(file is null ? SiteSettings.Empty : SiteSettings.Parse(file.Text), file?.ETag);
    }

    /// <summary>Applies a change to <c>site.json</c> if it's still the version opened, then reloads.</summary>
    /// <param name="etag">The version opened (null when there was no file).</param>
    /// <param name="change">Sets the keys this editor owns on the file's JSON object.</param>
    /// <param name="cancellationToken">Stops the save.</param>
    /// <returns>Whether it was saved, left as it was, or met someone else's save (nothing written).</returns>
    public async Task<SettingsSaveResult> SaveAsync(string? etag, Action<JsonObject> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        // A form posts "no file yet" back as an empty field, not as null (F-0055).
        var file = await store.ReadSettingsFileAsync(ContentContainers.SiteSettingsFile, cancellationToken);
        if (!string.Equals(file?.ETag, string.IsNullOrEmpty(etag) ? null : etag, StringComparison.Ordinal))
        {
            return SettingsSaveResult.Conflict;
        }

        var json = (file is null ? null : JsonNode.Parse(file.Text, documentOptions: ReadOptions) as JsonObject) ?? [];
        var before = json.DeepClone();
        change(json);
        // The same settings, however the file was written: a write would only add a version that's
        // the same as the last, and an audit entry for nothing (F-0175).
        if (JsonNode.DeepEquals(before, json))
        {
            return SettingsSaveResult.Unchanged;
        }

        var text = json.ToJsonString(WriteOptions) + "\n";
        _ = SiteSettings.Parse(text);
        if (await store.WriteSettingsFileAsync(ContentContainers.SiteSettingsFile, text, file?.ETag, cancellationToken) is null)
        {
            return SettingsSaveResult.Conflict;
        }

        await reloader.ReloadAfterWriteAsync(store);
        return SettingsSaveResult.Saved;
    }

    /// <summary>Turns a value into JSON the way <c>site.json</c> is written (camelCase).</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The JSON node.</returns>
    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, NodeOptions);

    private static readonly JsonSerializerOptions NodeOptions = new(SiteSettings.JsonOptions)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
