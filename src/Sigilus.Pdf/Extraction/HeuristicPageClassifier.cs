using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;

namespace Sigilus.Pdf.Extraction;

/// <summary>
/// Classifica a página em NativeText / Scanned / Hybrid / Empty contando
/// caracteres extraíveis e imagens grandes no content stream.
/// </summary>
public sealed class HeuristicPageClassifier : IPageClassifier
{
    private const int MinTextChars = 40;
    private const float MinImageCoverageRatio = 0.4f;

    public PageClassification Classify(Stream pdf, int pageIndex)
    {
        if (!pdf.CanSeek)
            throw new ArgumentException("Stream precisa ser seekable.", nameof(pdf));
        pdf.Position = 0;

        using var reader = new PdfReader(pdf).SetUnethicalReading(true);
        using var doc = new PdfDocument(reader);
        var page = doc.GetPage(pageIndex + 1);

        var strat = new CharCoordExtractionStrategy();
        new PdfCanvasProcessor(strat).ProcessPageContent(page);
        var charCount = strat.Text.AsSpan().Trim().Length;

        var imageListener = new ImageCoverageListener(page.GetPageSize().GetWidth() * page.GetPageSize().GetHeight());
        new PdfCanvasProcessor(imageListener).ProcessPageContent(page);

        var hasText = charCount >= MinTextChars;
        var hasLargeImage = imageListener.CoverageRatio >= MinImageCoverageRatio;

        return (hasText, hasLargeImage) switch
        {
            (false, false) => PageClassification.Empty,
            (true, false) => PageClassification.NativeText,
            (false, true) => PageClassification.Scanned,
            (true, true) => PageClassification.Hybrid,
        };
    }

    private sealed class ImageCoverageListener : IEventListener
    {
        private readonly float _pageArea;
        private float _imageArea;
        public float CoverageRatio => _pageArea <= 0 ? 0 : Math.Min(1f, _imageArea / _pageArea);

        public ImageCoverageListener(float pageArea) => _pageArea = pageArea;

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_IMAGE };

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not ImageRenderInfo ir) return;
            if (ir.GetImage() is not PdfImageXObject) return;
            var ctm = ir.GetImageCtm();
            // O quadrado unitário transformado pela CTM tem área = |a*d - b*c|.
            var area = Math.Abs(ctm.Get(0) * ctm.Get(4) - ctm.Get(1) * ctm.Get(3));
            _imageArea += area;
        }
    }
}
