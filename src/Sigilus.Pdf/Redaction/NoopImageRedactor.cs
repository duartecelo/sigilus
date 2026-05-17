using iText.Kernel.Pdf;
using Sigilus.Core.Domain;
using Sigilus.Pdf.Abstractions;

namespace Sigilus.Pdf.Redaction;

/// <summary>
/// Placeholder até o <c>SkiaImageRedactor</c> ser implementado. Útil para
/// validar o caminho texto+vetor isoladamente.
/// </summary>
public sealed class NoopImageRedactor : IImageRedactor
{
    public Task RedactImagesAsync(
        PdfDocument doc,
        IReadOnlyList<RedactionDecision> decisions,
        CancellationToken ct) => Task.CompletedTask;
}
