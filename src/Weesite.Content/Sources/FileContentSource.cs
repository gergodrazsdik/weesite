namespace Weesite.Content.Sources;

/// <summary>Reads content from a directory laid out like the Blob containers (the repo's <c>seed/</c>).</summary>
/// <param name="root">The directory with <c>content/</c> and <c>settings/</c>.</param>
public sealed class FileContentSource(string root) : IContentSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceFile>> ReadContentAsync(CancellationToken cancellationToken)
    {
        var content = Path.Combine(root, ContentContainers.Content);
        if (!Directory.Exists(content))
        {
            return [];
        }

        var files = new List<SourceFile>();
        foreach (var file in Directory.EnumerateFiles(content, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var path = Path.GetRelativePath(content, file).Replace('\\', '/');
            files.Add(new SourceFile(path, await File.ReadAllTextAsync(file, cancellationToken)));
        }

        return files;
    }

    /// <inheritdoc />
    public Task<string?> ReadSettingsAsync(CancellationToken cancellationToken) =>
        ReadSettingsFileAsync(ContentContainers.SiteSettingsFile, cancellationToken);

    /// <inheritdoc />
    public async Task<string?> ReadSettingsFileAsync(string name, CancellationToken cancellationToken)
    {
        var file = Path.Combine(root, ContentContainers.Settings, Path.GetFileName(name));
        return File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken) : null;
    }
}
