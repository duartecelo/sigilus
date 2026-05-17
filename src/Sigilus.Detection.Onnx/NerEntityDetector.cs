using System.Runtime.CompilerServices;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Detection.Coordinates;

namespace Sigilus.Detection.Onnx;

/// <summary>
/// Roda um <see cref="INerProvider"/> sobre o texto da página e devolve
/// <see cref="DetectedEntity"/>s usando <see cref="CharCoordIndex"/> para
/// obter os retângulos correspondentes.
/// </summary>
public sealed class NerEntityDetector : IEntityDetector
{
    private readonly INerProvider _ner;

    public NerEntityDetector(INerProvider ner) => _ner = ner;

    public async IAsyncEnumerable<DetectedEntity> DetectAsync(
        PageContext page,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(page.ConcatenatedText)) yield break;

        var spans = await _ner.InferAsync(page.ConcatenatedText, ct).ConfigureAwait(false);
        var index = new CharCoordIndex(page);

        foreach (var span in spans)
        {
            ct.ThrowIfCancellationRequested();
            var rects = index.RectsFor(span.CharStart, span.CharLength);
            var text = page.ConcatenatedText.Substring(
                Math.Min(span.CharStart, page.ConcatenatedText.Length),
                Math.Min(span.CharLength, Math.Max(0, page.ConcatenatedText.Length - span.CharStart)));

            foreach (var rect in rects)
            {
                if (rect.IsEmpty) continue;
                yield return new DetectedEntity(
                    Type: span.Type,
                    MatchedText: text,
                    Confidence: span.Score,
                    Bounds: rect,
                    PageIndex: page.PageIndex,
                    Source: DetectionSource.Ner,
                    CharStart: span.CharStart,
                    CharLength: span.CharLength);
            }
        }
    }
}
