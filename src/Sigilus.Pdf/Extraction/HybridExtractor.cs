using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Pdf.Abstractions;

namespace Sigilus.Pdf.Extraction;

/// <summary>
/// Extrai uma <see cref="PageContext"/> combinando texto nativo (iText
/// LocationTextExtractionStrategy + char coords) e, opcionalmente, OCR
/// (quando um <see cref="IOcrEngine"/> e um <see cref="IPdfPageRenderer"/>
/// são fornecidos).
/// </summary>
public sealed class HybridExtractor : ITextExtractor
{
    private readonly IPageClassifier _classifier;
    private readonly IOcrEngine? _ocr;
    private readonly IPdfPageRenderer? _renderer;
    private readonly int _ocrDpi;

    public HybridExtractor(
        IPageClassifier classifier,
        IOcrEngine? ocr = null,
        IPdfPageRenderer? renderer = null,
        int ocrDpi = 300)
    {
        _classifier = classifier;
        _ocr = ocr;
        _renderer = renderer;
        _ocrDpi = ocrDpi;
    }

    public PageContext Extract(Stream pdf, int pageIndex, CancellationToken ct)
    {
        if (!pdf.CanSeek) throw new ArgumentException("Stream precisa ser seekable.", nameof(pdf));

        var classification = _classifier.Classify(pdf, pageIndex);

        pdf.Position = 0;
        using var reader = new PdfReader(pdf).SetUnethicalReading(true);
        using var doc = new PdfDocument(reader);
        var page = doc.GetPage(pageIndex + 1);

        var strat = new CharCoordExtractionStrategy();
        new PdfCanvasProcessor(strat).ProcessPageContent(page);

        var runs = new List<TextRun>();
        if (classification is PageClassification.NativeText or PageClassification.Hybrid
            && strat.Text.Length > 0)
        {
            runs.Add(BuildNativeRun(strat.Text, strat.CharRects, pageIndex));
        }

        var size = page.GetPageSizeWithRotation();
        if (classification is PageClassification.Scanned or PageClassification.Hybrid
            && _ocr is not null && _renderer is not null)
        {
            var png = _renderer.RenderPng(pdf, pageIndex, _ocrDpi);
            var ocrRuns = _ocr.Recognize(png, pageIndex, size.GetWidth(), size.GetHeight(), ct);
            runs.AddRange(ocrRuns);
        }

        var concatenated = string.Join('\n', runs.Select(r => r.Text));
        var elements = BuildElements(runs);

        return new PageContext(
            PageIndex: pageIndex,
            Classification: classification,
            ConcatenatedText: concatenated,
            Runs: runs,
            Elements: elements,
            WidthPts: size.GetWidth(),
            HeightPts: size.GetHeight(),
            RotationDegrees: page.GetRotation());
    }

    private static TextRun BuildNativeRun(string s, IReadOnlyList<PdfRect> chars, int pageIndex)
    {
        var nonEmpty = chars.Where(c => !c.IsEmpty).ToList();
        if (nonEmpty.Count == 0)
            return new TextRun(s, new PdfRect(0, 0, 0, 0), pageIndex, chars);

        var minX = nonEmpty.Min(c => c.X);
        var minY = nonEmpty.Min(c => c.Y);
        var maxR = nonEmpty.Max(c => c.Right);
        var maxT = nonEmpty.Max(c => c.Top);

        return new TextRun(
            Text: s,
            Bounds: new PdfRect(minX, minY, maxR - minX, maxT - minY),
            PageIndex: pageIndex,
            CharBounds: chars)
        {
            Source = DetectionSource.Regex,
            Confidence = 1.0f,
        };
    }

    private static IReadOnlyList<PageElement> BuildElements(IReadOnlyList<TextRun> runs)
    {
        var list = new List<PageElement>(runs.Count);
        foreach (var r in runs)
            list.Add(new TextPageElement(r.PageIndex, r.Bounds, r));
        return list;
    }
}
