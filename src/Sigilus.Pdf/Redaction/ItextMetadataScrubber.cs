using iText.Kernel.Pdf;
using Sigilus.Core.Domain;
using Sigilus.Pdf.Abstractions;

namespace Sigilus.Pdf.Redaction;

public sealed class ItextMetadataScrubber : IMetadataScrubber
{
    public void Scrub(PdfDocument doc, MetadataPolicy policy)
    {
        if (policy.ClearInfoDict)
        {
            var info = doc.GetDocumentInfo();
            info.SetAuthor(string.Empty);
            info.SetTitle(string.Empty);
            info.SetSubject(string.Empty);
            info.SetKeywords(string.Empty);
            info.SetCreator(string.Empty);
            if (policy.ClearProducer)
                info.SetProducer(string.Empty);
        }

        if (policy.ClearXmp)
        {
            // Remove a entrada /Metadata do catálogo — iText não regravará XMP
            // se nenhum SetXmpMetadata for chamado depois disto.
            doc.GetCatalog().GetPdfObject().Remove(PdfName.Metadata);
        }

        if (policy.StripStructureTree && doc.IsTagged())
        {
            var root = doc.GetCatalog().GetPdfObject();
            root.Remove(PdfName.StructTreeRoot);
            root.Remove(PdfName.MarkInfo);
        }
    }
}
