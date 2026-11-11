namespace Weesite.Content.Sources;

/// <summary>
/// Writes a file so that readers see the old contents or the new, never an empty or half-written
/// file: the data goes to a temporary file beside it, which then replaces it in one rename
/// (F-0061). Only the file stores need this; Blob uploads are atomic already.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Writes text (UTF-8).</summary>
    /// <param name="file">The file.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task.</returns>
    public static Task WriteAllTextAsync(string file, string text, CancellationToken cancellationToken) =>
        WriteAsync(file, temp => File.WriteAllTextAsync(temp, text, cancellationToken));

    /// <summary>Writes bytes.</summary>
    /// <param name="file">The file.</param>
    /// <param name="data">The bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task.</returns>
    public static Task WriteAllBytesAsync(string file, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        WriteAsync(file, temp => File.WriteAllBytesAsync(temp, data, cancellationToken));

    private static async Task WriteAsync(string file, Func<string, Task> write)
    {
        var temp = Path.Combine(Path.GetDirectoryName(file)!, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await write(temp);
            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
