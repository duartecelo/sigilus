namespace Sigilus.Pdf.Abstractions;

/// <summary>
/// Rasteriza uma página de PDF para PNG no DPI solicitado.
/// Implementação WPF/CLI usa PDFium; pode ser stub em ambientes sem nativo.
/// </summary>
public interface IPdfPageRenderer
{
    ReadOnlyMemory<byte> RenderPng(Stream pdf, int pageIndex, int dpi);
}
