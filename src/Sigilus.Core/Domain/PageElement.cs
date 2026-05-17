namespace Sigilus.Core.Domain;

public abstract record PageElement(int PageIndex, PdfRect Bounds);

public sealed record TextPageElement(int PageIndex, PdfRect Bounds, TextRun Run)
    : PageElement(PageIndex, Bounds);

public sealed record ImagePageElement(int PageIndex, PdfRect Bounds, string XObjectRef)
    : PageElement(PageIndex, Bounds);
