using System.IO.Compression;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;
using Sigilus.Core.Domain;
using Sigilus.Pdf.Abstractions;
using SkiaSharp;

namespace Sigilus.Pdf.Redaction;

/// <summary>
/// Redação destrutiva em XObjects de imagem: decodifica o bitmap, pinta o
/// retângulo solicitado e re-codifica como PNG, substituindo o stream.
/// Faz clone-on-write quando o mesmo XObject é referenciado por mais de uma
/// página, para não vazar redação entre páginas.
/// </summary>
public sealed class SkiaImageRedactor : IImageRedactor
{
    public Task RedactImagesAsync(
        PdfDocument doc,
        IReadOnlyList<RedactionDecision> decisions,
        CancellationToken ct)
    {
        var globalRefCounts = CountXObjectReferences(doc);
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
                var shared = refNum > 0 && globalRefCounts.TryGetValue(refNum, out var n) && n > 1;
                var target = (shared || !localSeen.Add(refNum)) ? CloneXObject(doc, xobj) : xobj;

                var inv = TryInvert(ctm);
                if (inv is null) continue;
                var invMatrix = inv;

                var raw = target.GetImageBytes(true);
                using var bmp = SKBitmap.Decode(raw);
                if (bmp is null) continue;

                using var surf = SKSurface.Create(new SKImageInfo(bmp.Width, bmp.Height));
                surf.Canvas.DrawBitmap(bmp, 0, 0);
                using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };

                var painted = false;
                foreach (var d in group)
                {
                    var quad = MapRectToImagePixels(d.Bounds, invMatrix, bmp.Width, bmp.Height);
                    if (quad.IsEmpty) continue;
                    surf.Canvas.DrawRect(quad, paint);
                    painted = true;
                }
                if (!painted) continue;

                // DeviceRGB raw + FlateDecode — PDF não embute PNG diretamente.
                using var snap = surf.Snapshot();
                using var snapBmp = SKBitmap.FromImage(snap);
                var deflated = EncodeRgb8FromBitmap(snapBmp);

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

    internal static byte[] EncodeRgb8FromBitmap(SKBitmap bmp)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var rgb = new byte[w * h * 3];
        var di = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                rgb[di++] = c.Red;
                rgb[di++] = c.Green;
                rgb[di++] = c.Blue;
            }
        }
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(rgb, 0, rgb.Length);
        return ms.ToArray();
    }

    private static Dictionary<int, int> CountXObjectReferences(PdfDocument doc)
    {
        var counts = new Dictionary<int, int>();
        for (var p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            var resources = doc.GetPage(p).GetResources().GetPdfObject();
            var xobjects = resources?.GetAsDictionary(PdfName.XObject);
            if (xobjects is null) continue;
            foreach (var name in xobjects.KeySet())
            {
                var obj = xobjects.Get(name, false);
                var refNum = obj?.GetIndirectReference()?.GetObjNumber() ?? 0;
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
        var a = m.Get(0); var b = m.Get(1);
        var c = m.Get(3); var d = m.Get(4);
        var e = m.Get(6); var f = m.Get(7);
        var det = a * d - b * c;
        if (Math.Abs(det) < 1e-9f) return null;
        var inv = 1f / det;
        var na =  d * inv; var nb = -b * inv;
        var nc = -c * inv; var nd =  a * inv;
        var ne = (c * f - d * e) * inv;
        var nf = (b * e - a * f) * inv;
        return new Matrix(na, nb, nc, nd, ne, nf);
    }

    private static SKRect MapRectToImagePixels(PdfRect r, Matrix inv, int wPx, int hPx)
    {
        Span<(float x, float y)> corners = stackalloc (float, float)[]
        {
            (r.X, r.Y),
            (r.X + r.Width, r.Y),
            (r.X + r.Width, r.Y + r.Height),
            (r.X, r.Y + r.Height),
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
