using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using System.IO.Compression;
using iText.Kernel.Pdf.Xobject;
using Sigilus.Core.Domain;
using Sigilus.Core.Pseudonymization;
using Sigilus.Pdf.Abstractions;
using SkiaSharp;

namespace Sigilus.Pdf.Redaction;

/// <summary>
/// Variante do <see cref="SkiaImageRedactor"/> que escreve o pseudônimo
/// sobre o retângulo branco em vez de tarja preta. Usado pelo
/// <see cref="PseudonymizationRedactionEngine"/> para páginas escaneadas.
/// </summary>
public sealed class SkiaPseudonymizingImageRedactor : IImageRedactor
{
    private readonly IPseudonymizer _pseudonymizer;
    private readonly PseudonymContext _ctx;

    public SkiaPseudonymizingImageRedactor(IPseudonymizer pseudonymizer, PseudonymContext ctx)
    {
        _pseudonymizer = pseudonymizer;
        _ctx = ctx;
    }

    public Task RedactImagesAsync(
        PdfDocument doc,
        IReadOnlyList<RedactionDecision> decisions,
        CancellationToken ct)
    {
        var globalRefs = CountXObjectReferences(doc);
        var byPage = decisions.Where(d => d.Approved && !d.Bounds.IsEmpty)
                              .GroupBy(d => d.PageIndex);

        foreach (var group in byPage)
        {
            ct.ThrowIfCancellationRequested();
            var page = doc.GetPage(group.Key + 1);
            var placements = CollectImagePlacements(page);
            var localSeen = new HashSet<int>();

            foreach (var (xobj, ctm) in placements)
            {
                var refNum = xobj.GetPdfObject().GetIndirectReference()?.GetObjNumber() ?? 0;
                var shared = refNum > 0 && globalRefs.TryGetValue(refNum, out var n) && n > 1;
                var target = (shared || !localSeen.Add(refNum)) ? CloneXObject(doc, xobj) : xobj;

                var inv = TryInvert(ctm);
                if (inv is null) continue;

                var raw = target.GetImageBytes(true);
                using var bmp = SKBitmap.Decode(raw);
                if (bmp is null) continue;

                using var surf = SKSurface.Create(new SKImageInfo(bmp.Width, bmp.Height));
                surf.Canvas.DrawBitmap(bmp, 0, 0);
                using var white = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
                using var black = new SKPaint { Color = SKColors.Black, IsAntialias = true };

                // Mesma lógica do PseudonymizationRedactionEngine: quando uma
                // entidade ocupa múltiplos rects (texto em 2+ linhas), o
                // pseudônimo aparece SÓ no rect principal (mais largo) — os
                // demais ficam só com tarja branca para apagar o original.
                var painted = false;
                var groupedByEntity = group.GroupBy(d => (
                    d.Origin?.CharStart ?? -1,
                    d.Origin?.CharLength ?? 0,
                    d.Origin?.MatchedText ?? string.Empty));

                foreach (var subGroup in groupedByEntity)
                {
                    var primary = subGroup.OrderByDescending(d => d.Bounds.Width).First();
                    var original = primary.Origin?.MatchedText ?? string.Empty;
                    var type = primary.Origin?.Type ?? EntityType.Other;
                    var text = string.IsNullOrEmpty(original)
                        ? "[REDIGIDO]"
                        : _pseudonymizer.Substitute(type, original, _ctx);

                    foreach (var d in subGroup)
                    {
                        var quad = MapRectToImagePixels(d.Bounds, inv, bmp.Width, bmp.Height);
                        if (quad.IsEmpty) continue;
                        surf.Canvas.DrawRect(quad, white);
                        painted = true;
                    }

                    // Desenha o pseudônimo APENAS no primary.
                    var primaryQuad = MapRectToImagePixels(primary.Bounds, inv, bmp.Width, bmp.Height);
                    if (!primaryQuad.IsEmpty)
                        DrawTextInRect(surf.Canvas, black, text, primaryQuad);
                }
                if (!painted) continue;

                // Serializa em DeviceRGB raw pixels + FlateDecode, formato que o
                // PDF entende nativamente. PNG embutido no stream NÃO funciona —
                // os bytes seriam interpretados como flate puro e dariam lixo.
                using var snap = surf.Snapshot();
                using var snapBmp = SKBitmap.FromImage(snap);
                var deflated = SkiaImageRedactor.EncodeRgb8FromBitmap(snapBmp);
                var stream = target.GetPdfObject();
                stream.SetData(deflated, /*compress*/ false);
                stream.Put(PdfName.Filter, PdfName.FlateDecode);
                stream.Put(PdfName.Width, new PdfNumber(bmp.Width));
                stream.Put(PdfName.Height, new PdfNumber(bmp.Height));
                stream.Put(PdfName.BitsPerComponent, new PdfNumber(8));
                stream.Put(PdfName.ColorSpace, PdfName.DeviceRGB);
                stream.Remove(PdfName.DecodeParms);
                stream.Remove(PdfName.SMask);
                stream.Remove(PdfName.ImageMask);
            }
        }
        return Task.CompletedTask;
    }

private static void DrawTextInRect(SKCanvas canvas, SKPaint paint, string text, SKRect rect)
    {
        if (rect.Width <= 4 || rect.Height <= 4) return;
        var size = rect.Height * 0.75f;
        paint.TextSize = size;
        var w = paint.MeasureText(text);
        if (w > rect.Width) { paint.TextSize = size * rect.Width / w; }
        var baseline = rect.Top + rect.Height * 0.78f;
        canvas.DrawText(text, rect.Left + 2, baseline, paint);
    }

    private static Dictionary<int, int> CountXObjectReferences(PdfDocument doc)
    {
        var counts = new Dictionary<int, int>();
        for (var p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            var dict = doc.GetPage(p).GetResources().GetPdfObject()?.GetAsDictionary(PdfName.XObject);
            if (dict is null) continue;
            foreach (var name in dict.KeySet())
            {
                var refNum = dict.Get(name, false)?.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (refNum > 0)
                    counts[refNum] = counts.TryGetValue(refNum, out var c) ? c + 1 : 1;
            }
        }
        return counts;
    }

    private static List<(PdfImageXObject xobj, Matrix ctm)> CollectImagePlacements(PdfPage page)
    {
        var list = new List<(PdfImageXObject, Matrix)>();
        var listener = new ImagePlacementListener(list);
        new PdfCanvasProcessor(listener).ProcessPageContent(page);
        return list;
    }

    private static PdfImageXObject CloneXObject(PdfDocument doc, PdfImageXObject src)
    {
        var clone = (PdfStream)src.GetPdfObject().Clone();
        clone.MakeIndirect(doc);
        return new PdfImageXObject(clone);
    }

    private static Matrix? TryInvert(Matrix m)
    {
        float a = m.Get(0), b = m.Get(1), c = m.Get(3), d = m.Get(4), e = m.Get(6), f = m.Get(7);
        var det = a * d - b * c;
        if (Math.Abs(det) < 1e-9f) return null;
        var inv = 1f / det;
        return new Matrix(d * inv, -b * inv, -c * inv, a * inv,
                          (c * f - d * e) * inv, (b * e - a * f) * inv);
    }

    private static SKRect MapRectToImagePixels(PdfRect r, Matrix inv, int wPx, int hPx)
    {
        Span<(float x, float y)> corners = stackalloc (float, float)[]
        {
            (r.X, r.Y), (r.X + r.Width, r.Y),
            (r.X + r.Width, r.Y + r.Height), (r.X, r.Y + r.Height),
        };
        float minU = 1, minV = 1, maxU = 0, maxV = 0;
        foreach (var (x, y) in corners)
        {
            var u = x * inv.Get(0) + y * inv.Get(3) + inv.Get(6);
            var v = x * inv.Get(1) + y * inv.Get(4) + inv.Get(7);
            if (u < minU) minU = u; if (u > maxU) maxU = u;
            if (v < minV) minV = v; if (v > maxV) maxV = v;
        }
        if (maxU <= 0 || maxV <= 0 || minU >= 1 || minV >= 1) return SKRect.Empty;
        var px0 = Math.Clamp(minU, 0f, 1f) * wPx;
        var px1 = Math.Clamp(maxU, 0f, 1f) * wPx;
        var py0 = (1f - Math.Clamp(maxV, 0f, 1f)) * hPx;
        var py1 = (1f - Math.Clamp(minV, 0f, 1f)) * hPx;
        return new SKRect(px0, py0, px1, py1);
    }

    private sealed class ImagePlacementListener : IEventListener
    {
        private readonly List<(PdfImageXObject, Matrix)> _sink;
        public ImagePlacementListener(List<(PdfImageXObject, Matrix)> sink) => _sink = sink;
        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_IMAGE };
        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is ImageRenderInfo ir && ir.GetImage() is PdfImageXObject xobj)
                _sink.Add((xobj, ir.GetImageCtm()));
        }
    }
}
