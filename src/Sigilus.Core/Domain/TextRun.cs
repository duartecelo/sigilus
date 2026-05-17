namespace Sigilus.Core.Domain;

/// <summary>
/// Trecho de texto extraído de uma página com as caixas delimitadoras por caractere.
/// <see cref="CharBounds"/> tem o mesmo comprimento que <see cref="Text"/>.
/// </summary>
public sealed record TextRun(
    string Text,
    PdfRect Bounds,
    int PageIndex,
    IReadOnlyList<PdfRect> CharBounds)
{
    /// <summary>
    /// Origem do run: texto nativo do PDF ou OCR sobre raster.
    /// </summary>
    public DetectionSource Source { get; init; } = DetectionSource.Regex;

    /// <summary>Confiança (0..1) — 1.0 para texto nativo, &lt;1 para OCR.</summary>
    public float Confidence { get; init; } = 1.0f;
}
