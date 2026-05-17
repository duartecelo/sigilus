using Sigilus.Core.Domain;

namespace Sigilus.Core.Abstractions;

public interface IRedactionEngine
{
    Task RedactAsync(
        Stream input,
        Stream output,
        IReadOnlyList<RedactionDecision> decisions,
        CancellationToken ct);
}
