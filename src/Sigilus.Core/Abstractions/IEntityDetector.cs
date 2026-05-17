using Sigilus.Core.Domain;

namespace Sigilus.Core.Abstractions;

public interface IEntityDetector
{
    IAsyncEnumerable<DetectedEntity> DetectAsync(PageContext page, CancellationToken ct);
}

public interface INerProvider
{
    Task<IReadOnlyList<NerSpan>> InferAsync(string text, CancellationToken ct);
}
