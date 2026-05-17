using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.PdfCleanup;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Pdf.Abstractions;
using Sigilus.Pdf.Extraction;

namespace Sigilus.Pdf.Redaction;

/// <summary>
/// Redação destrutiva: pdfSweep apaga texto e vetores nos retângulos aprovados;
/// um <see cref="IImageRedactor"/> trata raster XObjects no mesmo
/// <see cref="PdfDocument"/>; um <see cref="IMetadataScrubber"/> finaliza
/// higienizando metadados.
///
/// <b>Importante</b>: pdfSweep <i>não</i> é chamado em páginas classificadas
/// como <c>Scanned</c>/<c>Empty</c>. Em páginas escaneadas, o conteúdo mora
/// dentro de um XObject de imagem; chamar <c>PdfCleaner.CleanUp</c> faz o
/// iText regenerar o content stream sem referenciar o XObject e apaga a
/// página inteira. Para essas, só o <see cref="IImageRedactor"/> opera.
/// </summary>
public sealed class PdfSweepRedactionEngine : IRedactionEngine
{
    private readonly IImageRedactor _imageRedactor;
    private readonly IMetadataScrubber _scrubber;
    private readonly MetadataPolicy _metadataPolicy;

    public PdfSweepRedactionEngine(
        IImageRedactor imageRedactor,
        IMetadataScrubber scrubber,
        MetadataPolicy? metadataPolicy = null)
    {
        _imageRedactor = imageRedactor;
        _scrubber = scrubber;
        _metadataPolicy = metadataPolicy ?? MetadataPolicy.Default;
    }

    public async Task RedactAsync(
        Stream input,
        Stream output,
        IReadOnlyList<RedactionDecision> decisions,
        CancellationToken ct)
    {
        if (input.CanSeek) input.Position = 0;

        var classifier = new HeuristicPageClassifier();
        var approved = decisions.Where(d => d.Approved && !d.Bounds.IsEmpty).ToList();
        var targetPages = approved.Select(d => d.PageIndex).Distinct().ToList();
        var classifications = new Dictionary<int, PageClassification>();
        if (input.CanSeek)
        {
            foreach (var p in targetPages)
            {
                input.Position = 0;
                classifications[p] = classifier.Classify(input, p);
            }
            input.Position = 0;
        }

        using var reader = new PdfReader(input).SetUnethicalReading(true);
        using var writer = new PdfWriter(output, new WriterProperties().SetFullCompressionMode(true));
        using var doc = new PdfDocument(reader, writer);

        // pdfSweep só onde há conteúdo nativo a apagar.
        var sweepable = approved
            .Where(d => !classifications.TryGetValue(d.PageIndex, out var c)
                        || c is PageClassification.NativeText or PageClassification.Hybrid)
            .ToList();

        var locations = sweepable
            .Select(d => new PdfCleanUpLocation(
                d.PageIndex + 1,
                new Rectangle(d.Bounds.X, d.Bounds.Y, d.Bounds.Width, d.Bounds.Height),
                ColorConstants.BLACK))
            .ToList();

        if (locations.Count > 0)
            PdfCleaner.CleanUp(doc, locations);

        // Imagens (Scanned + Hybrid): caminho raster recebe TODAS as decisões,
        // já que retângulos em Hybrid podem cair sobre raster também.
        await _imageRedactor.RedactImagesAsync(doc, approved, ct).ConfigureAwait(false);
        _scrubber.Scrub(doc, _metadataPolicy);
    }
}
