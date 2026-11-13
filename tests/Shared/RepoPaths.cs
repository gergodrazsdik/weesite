namespace Weesite.Tests;

/// <summary>Folders of the repository, found from the test binaries; linked into every test project.</summary>
internal static class RepoPaths
{
    /// <summary>Gets the repository's root: the folder with <c>Weesite.slnx</c>.</summary>
    public static string Root { get; } = Find();

    /// <summary>Gets the sample site, <c>seed/</c>.</summary>
    public static string Seed { get; } = Path.Combine(Root, "seed");

    /// <summary>Gets a folder under <c>artifacts/</c>, the build and test output.</summary>
    /// <param name="name">The folder, such as <c>screens</c>.</param>
    /// <returns>Its path.</returns>
    public static string Artifacts(string name) => Path.Combine(Root, "artifacts", name);

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Weesite.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Weesite.slnx not found above the test binaries.");
    }
}
