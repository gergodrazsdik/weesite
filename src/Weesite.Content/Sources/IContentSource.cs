namespace Weesite.Content.Sources;

/// <summary>A text file from a content source.</summary>
/// <param name="Path">The path relative to its container, with forward slashes.</param>
/// <param name="Text">The file's text.</param>
public sealed record SourceFile(string Path, string Text);

/// <summary>
/// Where content and settings come from: Blob Storage in Azure and with Aspire, a directory in
/// tests. Both use the same layout: a <c>content</c> container (posts and pages, Markdown) and a
/// <c>settings</c> container (<c>site.json</c>).
/// </summary>
public interface IContentSource
{
    /// <summary>Reads every Markdown file of the content container.</summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The files.</returns>
    Task<IReadOnlyList<SourceFile>> ReadContentAsync(CancellationToken cancellationToken);

    /// <summary>Reads <c>settings/site.json</c>.</summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>Its text, or null if there is none yet.</returns>
    Task<string?> ReadSettingsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the version marker that every save changes (ADR 0005), so other replicas notice and
    /// reload. Sources that don't change while the app runs return null.
    /// </summary>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>An opaque version, or null.</returns>
    Task<string?> ReadVersionAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
