using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Weesite.Search;

/// <summary>Turns text into unit-length vectors whose dot product measures similarity in meaning.</summary>
public interface IEmbedder
{
    /// <summary>Gets an ID for the model, stored with vectors so a model change is noticed.</summary>
    string ModelId { get; }

    /// <summary>Gets the vector length, or 0 when embeddings are off.</summary>
    int Dimensions { get; }

    /// <summary>Embeds a passage (a chunk of a post or page).</summary>
    /// <param name="text">The text.</param>
    /// <returns>A unit-length vector.</returns>
    float[] EmbedPassage(string text);

    /// <summary>Embeds a search query.</summary>
    /// <param name="text">The query.</param>
    /// <returns>A unit-length vector.</returns>
    float[] EmbedQuery(string text);
}

/// <summary>No embeddings: used when the model files are missing, so search runs on keywords only.</summary>
public sealed class NoEmbedder : IEmbedder
{
    /// <inheritdoc />
    public string ModelId => "none";

    /// <inheritdoc />
    public int Dimensions => 0;

    /// <inheritdoc />
    public float[] EmbedPassage(string text) => [];

    /// <inheritdoc />
    public float[] EmbedQuery(string text) => [];
}

/// <summary>
/// <c>bge-small-en-v1.5</c> (int8 ONNX, 384 dimensions) on ONNX Runtime in the app (brief, Hybrid
/// search; ADR 0003): BERT WordPiece tokens, at most 512, then the <c>[CLS]</c> token's state,
/// normalized, as the model card prescribes. Queries get the model's retrieval instruction.
/// Thread-safe: ONNX Runtime sessions can run concurrently.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    /// <summary>The model's directory name under the app's <c>Models/</c> folder.</summary>
    public const string ModelName = "bge-small-en-v1.5";

    private const int MaxTokens = 512;
    private const string QueryInstruction = "Represent this sentence for searching relevant passages: ";

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;

    /// <summary>Initializes a new instance of the <see cref="OnnxEmbedder"/> class.</summary>
    /// <param name="directory">The directory with <c>model.onnx</c> and <c>vocab.txt</c>.</param>
    public OnnxEmbedder(string directory)
    {
        using var options = new SessionOptions
        {
            // One thread: the web replica has a quarter of a vCPU, and requests run side by side.
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(Path.Combine(directory, "model.onnx"), options);
        _tokenizer = BertTokenizer.Create(Path.Combine(directory, "vocab.txt"), new BertOptions { LowerCaseBeforeTokenization = true });
        Dimensions = _session.OutputMetadata["last_hidden_state"].Dimensions[^1];
    }

    /// <inheritdoc />
    public string ModelId => $"{ModelName}-int8";

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <summary>Gets whether a directory has the model files.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns>True when both files are there.</returns>
    public static bool IsAvailable(string directory) =>
        File.Exists(Path.Combine(directory, "model.onnx")) && File.Exists(Path.Combine(directory, "vocab.txt"));

    /// <inheritdoc />
    public float[] EmbedPassage(string text) => Embed(text);

    /// <inheritdoc />
    public float[] EmbedQuery(string text) => Embed(QueryInstruction + text);

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();

    private float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ids = _tokenizer.EncodeToIds(text, MaxTokens, addSpecialTokens: true, out _, out _);
        var length = ids.Count;
        var inputIds = new DenseTensor<long>([1, length]);
        var attention = new DenseTensor<long>([1, length]);
        var tokenTypes = new DenseTensor<long>([1, length]);
        for (var i = 0; i < length; i++)
        {
            inputIds[0, i] = ids[i];
            attention[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attention),
        };
        if (_session.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypes));
        }

        using var outputs = _session.Run(inputs);
        var hidden = outputs.First(o => o.Name == "last_hidden_state").AsTensor<float>();
        var vector = new float[Dimensions];
        for (var d = 0; d < Dimensions; d++)
        {
            vector[d] = hidden[0, 0, d];
        }

        Vectors.Normalize(vector);
        return vector;
    }
}

/// <summary>Vector helpers on <see cref="TensorPrimitives"/>.</summary>
public static class Vectors
{
    /// <summary>Scales a vector to unit length in place (a zero vector stays zero).</summary>
    /// <param name="vector">The vector.</param>
    public static void Normalize(Span<float> vector)
    {
        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0)
        {
            TensorPrimitives.Divide(vector, norm, vector);
        }
    }

    /// <summary>Gets the cosine similarity of two unit-length vectors.</summary>
    /// <param name="a">One vector.</param>
    /// <param name="b">The other, of the same length.</param>
    /// <returns>From -1 to 1.</returns>
    public static float Similarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => TensorPrimitives.Dot(a, b);
}
