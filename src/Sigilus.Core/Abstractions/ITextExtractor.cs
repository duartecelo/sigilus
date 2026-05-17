using Sigilus.Core.Domain;

namespace Sigilus.Core.Abstractions;

public interface ITextExtractor
{
    PageContext Extract(Stream pdf, int pageIndex, CancellationToken ct);
}
