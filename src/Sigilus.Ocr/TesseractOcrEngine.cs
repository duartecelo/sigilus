using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Tesseract;

namespace Sigilus.Ocr;

/// <summary>
/// Wrapper Tesseract: roda OCR sobre o PNG da página rasterizada e devolve
/// <see cref="TextRun"/>s no nível de palavra com coordenadas em PDF
/// user-space (bottom-left, pontos). Char bounds são sintetizados
/// subdividindo a caixa da palavra linearmente.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine, IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly object _lock = new();

    public TesseractOcrEngine(string tessdataPath, string language = "por")
    {
        _engine = new TesseractEngine(tessdataPath, language, EngineMode.LstmOnly);
    }

    public IReadOnlyList<TextRun> Recognize(
        ReadOnlyMemory<byte> pngBitmap,
        int pageIndex,
        float pageWidthPts,
        float pageHeightPts,
        CancellationToken ct)
    {
        var runs = new List<TextRun>();

        lock (_lock)
        {
            using var pix = Pix.LoadFromMemory(pngBitmap.ToArray());
            using var page = _engine.Process(pix, PageSegMode.Auto);
            var bmpW = pix.Width;
            var bmpH = pix.Height;
            using var iter = page.GetIterator();
            iter.Begin();
            do
            {
                ct.ThrowIfCancellationRequested();
                if (!iter.TryGetBoundingBox(PageIteratorLevel.Word, out var rect)) continue;
                var word = iter.GetText(PageIteratorLevel.Word);
                if (string.IsNullOrWhiteSpace(word)) continue;

                var bounds = PixelRectToPdfRect(rect, bmpW, bmpH, pageWidthPts, pageHeightPts);
                var charBounds = SubdivideForChars(bounds, word.Length);
                var confidence = iter.GetConfidence(PageIteratorLevel.Word) / 100f;

                runs.Add(new TextRun(word, bounds, pageIndex, charBounds)
                {
                    Source = DetectionSource.Ner,   // origem != regex; ainda assim "automática"
                    Confidence = confidence,
                });
            } while (iter.Next(PageIteratorLevel.Word));
        }

        return runs;
    }

    private static PdfRect PixelRectToPdfRect(Rect r, int bmpW, int bmpH, float wPts, float hPts)
    {
        var sx = wPts / bmpW;
        var sy = hPts / bmpH;
        var x = r.X1 * sx;
        var width = (r.X2 - r.X1) * sx;
        var height = (r.Y2 - r.Y1) * sy;
        var y = hPts - r.Y2 * sy;   // flip vertical
        return new PdfRect(x, y, width, height);
    }

    private static IReadOnlyList<PdfRect> SubdivideForChars(PdfRect word, int chars)
    {
        if (chars <= 0) return Array.Empty<PdfRect>();
        var step = word.Width / chars;
        var result = new PdfRect[chars];
        for (var i = 0; i < chars; i++)
            result[i] = new PdfRect(word.X + i * step, word.Y, step, word.Height);
        return result;
    }

    public void Dispose() => _engine.Dispose();
}
