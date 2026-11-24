using Weesite.Content.Media;

namespace Weesite.Content.Markdown;

/// <summary>What a render depends on besides Media: the text, by its hash, and the renderer's settings.</summary>
/// <param name="Text">The text's hash.</param>
/// <param name="SourceLines">Whether blocks say their line (the editor's preview).</param>
/// <param name="ChecksMedia">Whether links to Media were checked against an index.</param>
internal readonly record struct RenderKey(string Text, bool SourceLines, bool ChecksMedia);

/// <summary>A problem found in a text, at its line in the text (from 0), before the file's line is added.</summary>
/// <param name="Text">What's wrong.</param>
/// <param name="Line">The line in the text.</param>
/// <param name="MissingAlt">Whether it's an image without alt text.</param>
internal readonly record struct RenderProblem(string Text, int Line, bool MissingAlt);

/// <summary>A render as kept: it holds for another media index while the images it looked up are the same there.</summary>
/// <param name="Html">The HTML.</param>
/// <param name="PlainText">The plain text.</param>
/// <param name="Problems">The problems.</param>
/// <param name="MediaUsed">What the render looked up in Media, and found.</param>
internal sealed record RenderedText(string Html, string PlainText, IReadOnlyList<RenderProblem> Problems, IReadOnlyList<MediaUse> MediaUsed)
{
    /// <summary>Gets its size in characters, roughly, for the cache's limit.</summary>
    public long Size { get; } = Html.Length + PlainText.Length + Problems.Sum(p => (long)p.Text.Length) + (64L * MediaUsed.Count);

    /// <summary>Gets whether it renders the same with a media index.</summary>
    /// <param name="media">The index.</param>
    /// <returns>True when every file it looked up is as it was.</returns>
    public bool HoldsFor(MediaIndex media)
    {
        foreach (var use in MediaUsed)
        {
            if (!use.HoldsFor(media))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A file a render looked up in Media, and what it found.</summary>
/// <param name="Name">The stored name.</param>
/// <param name="Stored">Whether it was stored.</param>
/// <param name="Image">Its size and variants, for an image.</param>
internal sealed record MediaUse(string Name, bool Stored, ImageInfo? Image)
{
    /// <summary>Gets whether a media index has the same.</summary>
    /// <param name="media">The index.</param>
    /// <returns>True when it does.</returns>
    public bool HoldsFor(MediaIndex media) => media.Has(Name) == Stored && Same(media.Find(Name), Image);

    private static bool Same(ImageInfo? a, ImageInfo? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.Width == b.Width && a.Height == b.Height && a.Variants.SequenceEqual(b.Variants));
}

/// <summary>A media index that notes what a render looked up in it.</summary>
/// <param name="media">The index.</param>
internal sealed class MediaLookups(MediaIndex media)
{
    private readonly List<MediaUse> _used = [];

    /// <summary>Gets what was looked up.</summary>
    public IReadOnlyList<MediaUse> Used => _used;

    /// <summary>Gets whether a file is stored.</summary>
    /// <param name="name">The stored name.</param>
    /// <returns>True when it is.</returns>
    public bool Has(string name) => Note(name).Stored;

    /// <summary>Finds an image.</summary>
    /// <param name="name">The stored name.</param>
    /// <returns>Its info, or null.</returns>
    public ImageInfo? Find(string name) => Note(name).Image;

    private MediaUse Note(string name)
    {
        var use = new MediaUse(name, media.Has(name), media.Find(name));
        _used.Add(use);
        return use;
    }
}

/// <summary>The renders of one load, which the next load starts from.</summary>
internal sealed class RenderLoad
{
    /// <summary>Gets the renders the load used, by key.</summary>
    public Dictionary<RenderKey, RenderedText> Used { get; } = [];
}
