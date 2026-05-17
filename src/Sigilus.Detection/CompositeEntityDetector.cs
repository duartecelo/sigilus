using System.Runtime.CompilerServices;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;

namespace Sigilus.Detection;

/// <summary>
/// Agrega múltiplos <see cref="IEntityDetector"/> (ex: regex + ONNX NER) e
/// deduplica detecções sobrepostas mantendo a de maior confiança.
/// </summary>
public sealed class CompositeEntityDetector : IEntityDetector
{
    private readonly IReadOnlyList<IEntityDetector> _inner;
    private readonly float _iouThreshold;

    public CompositeEntityDetector(IReadOnlyList<IEntityDetector> inner, float iouThreshold = 0.5f)
    {
        _inner = inner;
        _iouThreshold = iouThreshold;
    }

    public async IAsyncEnumerable<DetectedEntity> DetectAsync(
        PageContext page,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var all = new List<DetectedEntity>();
        foreach (var d in _inner)
            await foreach (var e in d.DetectAsync(page, ct).ConfigureAwait(false))
                all.Add(e);

        var deduped = Dedupe(all, _iouThreshold);
        foreach (var e in deduped) yield return e;
    }

    private static List<DetectedEntity> Dedupe(List<DetectedEntity> items, float iou)
    {
        var sorted = items.OrderByDescending(e => e.Confidence).ToList();
        var kept = new List<DetectedEntity>(sorted.Count);
        foreach (var e in sorted)
        {
            var redundant = kept.Any(k => k.PageIndex == e.PageIndex
                && (Iou(k.Bounds, e.Bounds) >= iou
                    || Contains(k.Bounds, e.Bounds)));
            if (!redundant) kept.Add(e);
        }
        return kept;
    }

    /// <summary>True se <paramref name="outer"/> cobre &gt; 80% da área de <paramref name="inner"/>.</summary>
    private static bool Contains(PdfRect outer, PdfRect inner)
    {
        var ix = Math.Max(0, Math.Min(outer.Right, inner.Right) - Math.Max(outer.X, inner.X));
        var iy = Math.Max(0, Math.Min(outer.Top, inner.Top) - Math.Max(outer.Y, inner.Y));
        var inter = ix * iy;
        var innerArea = inner.Width * inner.Height;
        return innerArea > 0 && inter / innerArea > 0.8f;
    }

    private static float Iou(PdfRect a, PdfRect b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        var inter = ix * iy;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }
}
