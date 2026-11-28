using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Nexora.Core.Speech;

namespace Nexora.App.Speech;

/// <summary>
/// The small meaning model bundled with NEXORA (all-MiniLM-L6-v2, 8-bit, 23 MB) run with ONNX Runtime on the CPU.
/// Turns a sentence into 384 numbers; sentences that mean the same point the same way.
/// </summary>
public sealed class MeaningModel : IEmbedder, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly object _gate = new();

    private MeaningModel(InferenceSession session, BertTokenizer tokenizer)
    {
        _session = session;
        _tokenizer = tokenizer;
    }

    public int Dimensions => 384;

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "Speech", "Models", "meaning-minilm-int8.onnx");
    public static string VocabPath => Path.Combine(AppContext.BaseDirectory, "Speech", "Models", "meaning-vocab.txt");
    public static bool Available => File.Exists(ModelPath) && File.Exists(VocabPath);

    /// <summary>A stamp for saved verse vectors: they are made again if the model file changes.</summary>
    public static string Stamp => Available ? $"minilm-{new FileInfo(ModelPath).Length}" : "";

    public static MeaningModel? TryOpen()
    {
        if (!Available) return null;
        var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        var session = new InferenceSession(ModelPath, opts);
        var tok = BertTokenizer.Create(VocabPath, new BertOptions { LowerCaseBeforeTokenization = true });
        return new MeaningModel(session, tok);
    }

    public float[] Embed(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, 128, out _, out _);
        var n = ids.Count;
        var input = new DenseTensor<long>(new[] { 1, n });
        var mask = new DenseTensor<long>(new[] { 1, n });
        var types = new DenseTensor<long>(new[] { 1, n });
        for (var i = 0; i < n; i++)
        {
            input[0, i] = ids[i];
            mask[0, i] = 1;
        }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", input),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", types),
        };
        float[] result;
        lock (_gate)
        {
            using var outputs = _session.Run(inputs);
            var hidden = outputs[0].AsTensor<float>();
            var dim = hidden.Dimensions[2];
            result = new float[dim];
            // The mean of every word's vector is the sentence's vector.
            for (var t = 0; t < n; t++)
                for (var d = 0; d < dim; d++)
                    result[d] += hidden[0, t, d];
        }
        var norm = 0.0;
        for (var d = 0; d < result.Length; d++) norm += result[d] * result[d];
        var inv = norm > 0 ? (float)(1 / Math.Sqrt(norm)) : 0f;
        for (var d = 0; d < result.Length; d++) result[d] *= inv;
        return result;
    }

    public void Dispose() => _session.Dispose();
}
