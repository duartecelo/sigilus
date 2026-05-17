using iText.Kernel.Pdf;
using Sigilus.Core.Domain;

namespace Sigilus.Pdf.Abstractions;

public interface IMetadataScrubber
{
    void Scrub(PdfDocument doc, MetadataPolicy policy);
}
