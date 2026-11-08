using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Weesite.Content;

/// <summary>The YAML block at the top of a Markdown file, between <c>---</c> lines.</summary>
internal sealed class FrontMatter
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public string? Kind { get; set; }

    public string? Title { get; set; }

    public string? Slug { get; set; }

    public string? Lang { get; set; }

    public string? Summary { get; set; }

    public DateTime? Date { get; set; }

    public DateTime? Updated { get; set; }

    public string? Author { get; set; }

    public List<string>? Tags { get; set; }

    public string? Translation { get; set; }

    public bool? Menu { get; set; }

    public List<string>? RedirectFrom { get; set; }

    public string? Image { get; set; }

    public string? ImageAlt { get; set; }

    /// <summary>Gets or sets <c>published</c> (the default), <c>draft</c> or <c>scheduled</c>.</summary>
    public string? Status { get; set; }

    /// <summary>Gets or sets when a scheduled item becomes public: an ISO 8601 time with an offset.</summary>
    public string? PublishAt { get; set; }

    /// <summary>Splits a file into its front matter and body.</summary>
    /// <param name="text">The whole file.</param>
    /// <param name="frontMatter">The parsed front matter.</param>
    /// <param name="body">The Markdown after it.</param>
    /// <param name="error">Why the split failed, if it did.</param>
    /// <returns>Whether the file had valid front matter.</returns>
    public static bool TrySplit(string text, out FrontMatter frontMatter, out string body, out string? error)
    {
        frontMatter = new FrontMatter();
        body = "";
        error = null;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            error = "the file doesn't start with a front matter block (---)";
            return false;
        }

        var end = normalized.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        var endAtEof = normalized.EndsWith("\n---", StringComparison.Ordinal) ? normalized.Length - 4 : -1;
        if (end < 0 && endAtEof < 0)
        {
            error = "the front matter block isn't closed (---)";
            return false;
        }

        var close = end >= 0 ? end : endAtEof;
        var yaml = normalized[4..(close + 1)];
        body = end >= 0 ? normalized[(end + 5)..].TrimStart('\n') : "";

        try
        {
            frontMatter = Deserializer.Deserialize<FrontMatter>(yaml) ?? new FrontMatter();
            return true;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            error = $"invalid front matter: {ex.Message}";
            return false;
        }
    }
}
