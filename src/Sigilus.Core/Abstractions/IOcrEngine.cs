using Sigilus.Core.Domain;

namespace Sigilus.Core.Abstractions;

/// <summary>
/// Reconhece texto sobre um bitmap de página já rasterizada. Implementações
/// recebem o bitmap codificado em PNG (bytes) para evitar arrastar SkiaSharp
/// para o Core. A conversão pixel→ponto fica a cargo da implementação.
/// </summary>
public interface IOcrEngine
{
    IReadOnlyList<TextRun> Recognize(
        ReadOnlyMemory<byte> pngBitmap,
        int pageIndex,
        float pageWidthPts,
        float pageHeightPts,
        CancellationToken ct);
}
