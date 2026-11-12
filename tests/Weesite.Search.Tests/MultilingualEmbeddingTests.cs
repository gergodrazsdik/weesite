using System.Text.Json;

namespace Weesite.Search.Tests;

/// <summary>
/// <c>multilingual-e5-small</c> (ADR 0008): the same token ids and vectors as Hugging Face's own
/// tokenizer and ONNX Runtime give (Fixtures/e5-reference.json, from scripts/e5-reference.py). Run
/// `just model` first; CI downloads it.
/// </summary>
internal sealed class MultilingualEmbeddingTests
{
    private static readonly EmbeddingModel E5 = EmbeddingModel.MultilingualE5Small;
    private static readonly string ModelDirectory = Path.Combine(AppContext.BaseDirectory, "Models", E5.Name);

    private static readonly Lazy<OnnxEmbedder> Model = new(() =>
        OnnxEmbedder.IsAvailable(ModelDirectory, E5)
            ? new OnnxEmbedder(ModelDirectory, E5)
            : throw new InvalidOperationException($"No embedding model in {ModelDirectory}: run `just model`."));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEnumerable<Func<Reference>> References() =>
        JsonSerializer.Deserialize<Reference[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "e5-reference.json")), Json)!
        .Select(r => (Func<Reference>)(() => r));

    [Test]
    [MethodDataSource(nameof(References))]
    public async Task Tokens_and_vectors_match_hugging_face(Reference reference)
    {
        var ids = Model.Value.Tokenize(reference.Text);
        var prefix = reference.Text.StartsWith("query: ", StringComparison.Ordinal) ? "query: " : "passage: ";
        var text = reference.Text[prefix.Length..];
        var vector = prefix == "query: " ? Model.Value.EmbedQuery(text) : Model.Value.EmbedPassage(text);

        await Assert.That(ids).IsEquivalentTo(reference.Ids, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        // The same tokens; the int8 model's arithmetic differs a little between ONNX Runtime
        // versions (the reference used Python's 1.22), so the vectors agree to about 0.998.
        await Assert.That(Vectors.Similarity(vector, reference.Vector)).IsGreaterThan(0.995f);
    }

    [Test]
    public async Task A_hungarian_query_finds_the_hungarian_passage_by_meaning()
    {
        var query = Model.Value.EmbedQuery("konténerek futtatása a felhőben");
        var related = Model.Value.EmbedPassage("A Kubernetes konténereket ütemez a fürt csomópontjaira.");
        var unrelated = Model.Value.EmbedPassage("A kutyát naponta kétszer kell megsétáltatni.");
        var english = Model.Value.EmbedPassage("Kubernetes schedules containers onto the nodes of a cluster.");

        await Assert.That(Model.Value.ModelId).IsEqualTo("multilingual-e5-small-int8");
        await Assert.That(Vectors.Similarity(query, related)).IsGreaterThan(Vectors.Similarity(query, unrelated));
        await Assert.That(Vectors.Similarity(query, english)).IsGreaterThan(Vectors.Similarity(query, unrelated));
    }

    /// <summary>A text with its reference token ids and vector.</summary>
    /// <param name="Text">The text, with its e5 prefix.</param>
    /// <param name="Ids">The token ids.</param>
    /// <param name="Vector">The normalized mean-pooled vector.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "Created by the JSON serializer.")]
    internal sealed record Reference(string Text, int[] Ids, float[] Vector);
}
