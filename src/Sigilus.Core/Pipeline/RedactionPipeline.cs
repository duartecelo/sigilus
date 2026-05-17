using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;

namespace Sigilus.Core.Pipeline;

/// <summary>
/// Orquestra extração → detecção → revisão → redação. A função de revisão
/// recebe as detecções e devolve as decisões finais (UI ou auto-aprovação
/// no CLI).
/// </summary>
public sealed class RedactionPipeline
{
    private readonly ITextExtractor _extractor;
    private readonly IEntityDetector _detector;
    private readonly IRedactionEngine _engine;
    private readonly int _pageCount;

    public RedactionPipeline(
        ITextExtractor extractor,
        IEntityDetector detector,
        IRedactionEngine engine,
        int pageCount)
    {
        _extractor = extractor;
        _detector = detector;
        _engine = engine;
        _pageCount = pageCount;
    }

    public delegate IReadOnlyList<RedactionDecision> ReviewFn(
        int pageIndex, PageContext page, IReadOnlyList<DetectedEntity> entities);

    public async Task<IReadOnlyList<RedactionDecision>> RunAsync(
        Stream input,
        Stream output,
        ReviewFn review,
        CancellationToken ct)
    {
        var allDecisions = new List<RedactionDecision>();

        for (var p = 0; p < _pageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = _extractor.Extract(input, p, ct);

            var entities = new List<DetectedEntity>();
            await foreach (var e in _detector.DetectAsync(page, ct).ConfigureAwait(false))
                entities.Add(e);

            var decisions = review(p, page, entities);
            allDecisions.AddRange(decisions);
        }

        await _engine.RedactAsync(input, output, allDecisions, ct).ConfigureAwait(false);
        return allDecisions;
    }
}
