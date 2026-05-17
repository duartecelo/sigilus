using iText.Kernel.Pdf;
using Sigilus.Core.Domain;

namespace Sigilus.Pdf.Abstractions;

/// <summary>
/// Aplica redação destrutiva sobre XObjects de imagem do documento já aberto.
/// Manipula pixels diretamente (SkiaSharp) — pdfSweep não cobre raster.
/// </summary>
public interface IImageRedactor
{
    Task RedactImagesAsync(
        PdfDocument doc,
        IReadOnlyList<RedactionDecision> decisions,
        CancellationToken ct);
}
