namespace Weesite.Content.Sources;

/// <summary>Names of the Blob containers (and directories) the content lives in.</summary>
public static class ContentContainers
{
    /// <summary>Posts and pages (Markdown).</summary>
    public const string Content = "content";

    /// <summary>Site settings.</summary>
    public const string Settings = "settings";

    /// <summary>Images and attachments.</summary>
    public const string Media = "media";

    /// <summary>The site settings file in <see cref="Settings"/>.</summary>
    public const string SiteSettingsFile = "site.json";

    /// <summary>The editors' author profiles in <see cref="Settings"/>, by author ID (ADR 0005).</summary>
    public const string AuthorsFile = "authors.json";

    /// <summary>The version marker in <see cref="Settings"/>: every save rewrites it (ADR 0005).</summary>
    public const string VersionFile = "version";
}
