using System.Runtime.Versioning;
using PDFtoImage;
using Sigilus.Pdf.Abstractions;
using SkiaSharp;

namespace Sigilus.Pdf.Rendering;

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("android31.0")]

/// <summary>
/// Rasteriza páginas via PDFium (pacote PDFtoImage). Saída em PNG para
/// preservar a interface com <see cref="IPdfPageRenderer"/> em
/// <c>ReadOnlyMemory&lt;byte&gt;</c>.
/// </summary>
public sealed class PdfiumPageRenderer : IPdfPageRenderer
{
    public ReadOnlyMemory<byte> RenderPng(Stream pdf, int pageIndex, int dpi)
    {
        if (pdf.CanSeek) pdf.Position = 0;
        var options = new RenderOptions(Dpi: dpi, WithAnnotations: false, WithFormFill: false);
        using var bmp = Conversion.ToImage(pdf, leaveOpen: true, password: null, page: new Index(pageIndex), options: options);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
