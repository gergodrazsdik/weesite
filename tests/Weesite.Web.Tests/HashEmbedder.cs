using Weesite.Search;

namespace Weesite.Web.Tests;

/// <summary>
/// A fake embedding model for tests (brief, Testing): each word is hashed into one of 64
/// dimensions, so texts that share words are similar. Deterministic and instant, unlike the real
/// model, which every test app would otherwise load.
/// </summary>
internal sealed class HashEmbedder : IEmbedder
{
    /// <inheritdoc />
    public string ModelId => "hash-64";

    /// <inheritdoc />
    public int Dimensions => 64;

    /// <inheritdoc />
    public float[] EmbedPassage(string text) => Embed(text);

    /// <inheritdoc />
    public float[] EmbedQuery(string text) => Embed(text);

    private static float[] Embed(string text)
    {
        var vector = new float[64];
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var hash = 0u;
            foreach (var ch in word.ToUpperInvariant())
            {
                hash = (hash * 31) + ch;
            }

            vector[hash % 64] += 1;
        }

        Vectors.Normalize(vector);
        return vector;
    }
}
