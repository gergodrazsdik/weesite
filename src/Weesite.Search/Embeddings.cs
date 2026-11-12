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

    /// <summary>Gets the similarity floor for meaning hits measured for this model (<c>Search:MinSimilarity</c> overrides it).</summary>
    float DefaultMinSimilarity => HybridSearch.DefaultMinSimilarity;

    /// <summary>Gets the lead over the median a meaning hit needs, measured for this model (<c>Search:MinLead</c> overrides it).</summary>
    float DefaultMinLead => HybridSearch.DefaultMinLead;
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

/// <summary>How a model reads text into token ids.</summary>
public enum TokenizerKind
{
    /// <summary>BERT WordPiece, lowercased, from <c>vocab.txt</c>.</summary>
    WordPiece,

    /// <summary>
    /// SentencePiece from <c>sentencepiece.bpe.model</c>, numbered as XLM-RoBERTa does: ids one
    /// above SentencePiece's, with <c>&lt;s&gt;</c> 0, <c>&lt;/s&gt;</c> 2 and unknown 3.
    /// </summary>
    XlmRobertaSentencePiece,
}

/// <summary>How a model's token states become one vector.</summary>
public enum Pooling
{
    /// <summary>The first (<c>[CLS]</c>) token's state.</summary>
    Cls,

    /// <summary>The mean of every token's state.</summary>
    Mean,
}

/// <summary>An embedding model Weesite can run (ADR 0003, ADR 0008): its files sit in <c>Models/&lt;Name&gt;/</c>.</summary>
/// <param name="Name">The directory name, and the <c>Search:EmbeddingModel</c> value that picks it.</param>
/// <param name="Tokenizer">How it tokenizes.</param>
/// <param name="TokenizerFile">The tokenizer's file next to <c>model.onnx</c>.</param>
/// <param name="Pooling">How token states become a vector.</param>
/// <param name="QueryPrefix">What goes before a search query.</param>
/// <param name="PassagePrefix">What goes before a passage.</param>
/// <param name="MinSimilarity">The similarity floor for meaning hits, measured on the sample site.</param>
/// <param name="MinLead">The lead over the median a meaning hit needs, measured on the sample site.</param>
public sealed record EmbeddingModel(string Name, TokenizerKind Tokenizer, string TokenizerFile, Pooling Pooling, string QueryPrefix, string PassagePrefix, float MinSimilarity, float MinLead)
{
    /// <summary>
    /// <c>bge-small-en-v1.5</c>, int8: English, the default. The <c>[CLS]</c> state, and queries get
    /// the model's retrieval instruction, as its model card prescribes.
    /// </summary>
    public static EmbeddingModel BgeSmallEn { get; } = new(
        "bge-small-en-v1.5", TokenizerKind.WordPiece, "vocab.txt", Pooling.Cls, "Represent this sentence for searching relevant passages: ", "",
        HybridSearch.DefaultMinSimilarity, HybridSearch.DefaultMinLead);

    /// <summary>
    /// <c>multilingual-e5-small</c>, int8: about a hundred languages, Hungarian among them. The mean
    /// of the token states, and the <c>query: </c> and <c>passage: </c> prefixes its model card asks
    /// for. It rates any two texts 0.75 to 0.9, so its thresholds are its own: on the sample site
    /// what a query was about led the median by 0.073 and more, unrelated text by 0.06 at most
    /// (2026-11-12, ADR 0008).
    /// </summary>
    public static EmbeddingModel MultilingualE5Small { get; } = new(
        "multilingual-e5-small", TokenizerKind.XlmRobertaSentencePiece, "sentencepiece.bpe.model", Pooling.Mean, "query: ", "passage: ",
        0.8f, 0.07f);

    /// <summary>Gets every model, the default first.</summary>
    public static IReadOnlyList<EmbeddingModel> All { get; } = [BgeSmallEn, MultilingualE5Small];

    /// <summary>Finds a model by name; null for an unknown one.</summary>
    /// <param name="name">The name, such as <c>multilingual-e5-small</c>.</param>
    /// <returns>The model, or null.</returns>
    public static EmbeddingModel? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? BgeSmallEn : All.FirstOrDefault(m => string.Equals(m.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// An embedding model (int8 ONNX, 384 dimensions) on ONNX Runtime in the app (brief, Hybrid search;
/// ADR 0003, ADR 0008): at most 512 tokens, then the model's pooling, normalized. Thread-safe: ONNX
/// Runtime sessions can run concurrently.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder, IDisposable
{
    /// <summary>The default model's directory name under the app's <c>Models/</c> folder.</summary>
    public const string ModelName = "bge-small-en-v1.5";

    private const int MaxTokens = 512;

    private readonly InferenceSession _session;
    private readonly EmbeddingModel _model;
    private readonly BertTokenizer? _wordPiece;
    private readonly SentencePieceTokenizer? _sentencePiece;

    /// <summary>Initializes a new instance of the <see cref="OnnxEmbedder"/> class.</summary>
    /// <param name="directory">The directory with <c>model.onnx</c> and the model's tokenizer file.</param>
    /// <param name="model">The model; <see cref="EmbeddingModel.BgeSmallEn"/> when null.</param>
    public OnnxEmbedder(string directory, EmbeddingModel? model = null)
    {
        _model = model ?? EmbeddingModel.BgeSmallEn;
        using var options = new SessionOptions
        {
            // One thread: the web replica has a quarter of a vCPU, and requests run side by side.
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(Path.Combine(directory, "model.onnx"), options);
        var tokenizer = Path.Combine(directory, _model.TokenizerFile);
        if (_model.Tokenizer == TokenizerKind.WordPiece)
        {
            _wordPiece = BertTokenizer.Create(tokenizer, new BertOptions { LowerCaseBeforeTokenization = true });
        }
        else
        {
            using var stream = File.OpenRead(tokenizer);
            _sentencePiece = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);
        }

        Dimensions = _session.OutputMetadata["last_hidden_state"].Dimensions[^1];
    }

    /// <inheritdoc />
    public string ModelId => $"{_model.Name}-int8";

    /// <inheritdoc />
    public float DefaultMinSimilarity => _model.MinSimilarity;

    /// <inheritdoc />
    public float DefaultMinLead => _model.MinLead;

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <summary>Gets whether a directory has a model's files.</summary>
    /// <param name="directory">The directory.</param>
    /// <param name="model">The model; <see cref="EmbeddingModel.BgeSmallEn"/> when null.</param>
    /// <returns>True when both files are there.</returns>
    public static bool IsAvailable(string directory, EmbeddingModel? model = null) =>
        File.Exists(Path.Combine(directory, "model.onnx")) && File.Exists(Path.Combine(directory, (model ?? EmbeddingModel.BgeSmallEn).TokenizerFile));

    /// <inheritdoc />
    public float[] EmbedPassage(string text) => Embed(_model.PassagePrefix + text);

    /// <inheritdoc />
    public float[] EmbedQuery(string text) => Embed(_model.QueryPrefix + text);

    /// <summary>Gets the token ids the model reads for a text, special tokens included.</summary>
    /// <param name="text">The text, prefix included.</param>
    /// <returns>The ids.</returns>
    public IReadOnlyList<int> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_wordPiece is not null)
        {
            return _wordPiece.EncodeToIds(text, MaxTokens, addSpecialTokens: true, out _, out _);
        }

        // XLM-RoBERTa's numbering (fairseq): <s> 0, <pad> 1, </s> 2, <unk> 3, then SentencePiece's
        // pieces one above their own ids (its unknown, 0, becomes 3).
        var pieces = _sentencePiece!.EncodeToIds(text.Normalize(System.Text.NormalizationForm.FormKC), addBeginningOfSentence: false, addEndOfSentence: false, MaxTokens - 2, out _, out _);
        var ids = new List<int>(pieces.Count + 2) { 0 };
        ids.AddRange(pieces.Select(id => id == 0 ? 3 : id + 1));
        ids.Add(2);
        return ids;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();

    private float[] Embed(string text)
    {
        var ids = Tokenize(text);
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
        if (_model.Pooling == Pooling.Cls)
        {
            for (var d = 0; d < Dimensions; d++)
            {
                vector[d] = hidden[0, 0, d];
            }
        }
        else
        {
            for (var t = 0; t < length; t++)
            {
                for (var d = 0; d < Dimensions; d++)
                {
                    vector[d] += hidden[0, t, d];
                }
            }
        }

        // The mean's division by the token count disappears in the normalization.
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
