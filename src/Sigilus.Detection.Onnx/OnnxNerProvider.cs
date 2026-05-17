using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;

namespace Sigilus.Detection.Onnx;

/// <summary>
/// Provedor NER baseado em modelo ONNX BIO (ex: BERTimbau-NER INT8).
/// Decodifica spans BIO em <see cref="NerSpan"/> com offsets de caractere
/// no texto original. O mapeamento span → retângulo é responsabilidade do
/// detector (via <c>CharCoordIndex</c>).
/// </summary>
public sealed class OnnxNerProvider : INerProvider, IDisposable
{
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tok;
    private readonly string[] _labels;

    public OnnxNerProvider(string modelPath, string vocabPath, string[] labels, bool lowercase = true)
    {
        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
        };
        _session = new InferenceSession(modelPath, opts);
        _tok = new WordPieceTokenizer(vocabPath, lowercase: lowercase);
        _labels = labels;
    }

    public Task<IReadOnlyList<NerSpan>> InferAsync(string text, CancellationToken ct)
    {
        var enc = _tok.Encode(text);
        var len = enc.Ids.Length;
        var ids = new DenseTensor<long>(enc.Ids.Select(i => (long)i).ToArray(), new[] { 1, len });
        var mask = new DenseTensor<long>(enc.Mask.Select(i => (long)i).ToArray(), new[] { 1, len });
        var types = new DenseTensor<long>(new long[len], new[] { 1, len });

        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", types),
        });

        var logits = results.First().AsTensor<float>();
        var seq = logits.Dimensions[1];
        var nLabels = logits.Dimensions[2];

        var spans = new List<NerSpan>();
        EntityType? cur = null;
        int s = -1, e = -1; float scoreSum = 0; int n = 0;

        for (var t = 0; t < seq; t++)
        {
            ct.ThrowIfCancellationRequested();
            var best = 0; var bv = float.NegativeInfinity;
            for (var k = 0; k < nLabels; k++)
            {
                var v = logits[0, t, k];
                if (v > bv) { bv = v; best = k; }
            }
            var label = best < _labels.Length ? _labels[best] : "O";
            var (cs, cl) = enc.Offsets[t];
            if (cl == 0) continue;

            if (label.StartsWith("B-", StringComparison.Ordinal)
                || (label.StartsWith("I-", StringComparison.Ordinal) && s < 0))
            {
                Flush();
                cur = MapLabel(label[2..]);
                if (cur is not null)
                {
                    s = cs; e = cs + cl; scoreSum = Sigmoid(bv); n = 1;
                }
            }
            else if (label.StartsWith("I-", StringComparison.Ordinal) && cur is not null && MapLabel(label[2..]) == cur)
            {
                e = cs + cl; scoreSum += Sigmoid(bv); n++;
            }
            else
            {
                Flush();
            }
        }
        Flush();
        return Task.FromResult<IReadOnlyList<NerSpan>>(spans);

        void Flush()
        {
            if (s < 0 || cur is null) { s = -1; return; }
            spans.Add(new NerSpan(cur.Value, s, e - s, n == 0 ? 0 : scoreSum / n));
            s = -1;
        }
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    /// <summary>
    /// Mapeia rótulos comuns de modelos PT-BR (LeNER-Br, BERTimbau-NER) para
    /// nossos <see cref="EntityType"/>. Retorna <c>null</c> para tipos que
    /// <b>não</b> devem ser pseudonimizados — datas (TEMPO), leis
    /// (LEGISLACAO), jurisprudência citada. Esses são descartados pelo
    /// chamador (sem emitir <see cref="NerSpan"/>).
    /// </summary>
    private static EntityType? MapLabel(string tag) => tag switch
    {
        "PER" or "PESSOA" => EntityType.PersonName,
        "LOC" or "LOCAL" => EntityType.Address,
        "ORG" or "ORGANIZACAO" => EntityType.Other,
        // TEMPO/LEGISLACAO/JURISPRUDENCIA: não pseudonimizar.
        "TEMPO" or "LEGISLACAO" or "JURISPRUDENCIA" => null,
        _ => null,
    };

    public void Dispose() => _session.Dispose();
}
