using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.PdfCleanup;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Core.Pseudonymization;
using Sigilus.Pdf.Abstractions;
using Sigilus.Pdf.Extraction;

namespace Sigilus.Pdf.Redaction;

/// <summary>
/// Redação por <b>substituição</b>: apaga o valor original com pdfSweep
/// (destrutivo, mesmo mecanismo do <see cref="PdfSweepRedactionEngine"/>)
/// mas re-escreve um pseudônimo no lugar usando <c>PdfCanvas.ShowText</c>.
///
/// <b>Importante</b>: pdfSweep <i>não</i> é chamado em páginas classificadas
/// como <c>Scanned</c>/<c>Empty</c>. Em PDFs escaneados, chamar
/// <c>PdfCleaner.CleanUp</c> sobre uma página cuja área principal é uma
/// imagem faz o iText regenerar o content stream sem o XObject e apaga a
/// página inteira. Para essas, só o <see cref="IImageRedactor"/> opera —
/// pintando o pseudônimo direto nos pixels do bitmap.
/// </summary>
public sealed class PseudonymizationRedactionEngine : IRedactionEngine
{
    private readonly IPseudonymizer _pseudonymizer;
    private readonly IImageRedactor _imageRedactor;
    private readonly IMetadataScrubber _scrubber;
    private readonly MetadataPolicy _metadataPolicy;
    private readonly PseudonymContext _ctx;

    public PseudonymizationRedactionEngine(
        IPseudonymizer pseudonymizer,
        PseudonymContext ctx,
        IImageRedactor imageRedactor,
        IMetadataScrubber scrubber,
        MetadataPolicy? metadataPolicy = null)
    {
        _pseudonymizer = pseudonymizer;
        _ctx = ctx;
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

        // Pré-classifica cada página alvo para decidir caminho de redação.
        var classifier = new HeuristicPageClassifier();
        var targetPages = decisions.Where(d => d.Approved && !d.Bounds.IsEmpty)
                                   .Select(d => d.PageIndex).Distinct().ToList();
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

        var approved = decisions.Where(d => d.Approved && !d.Bounds.IsEmpty).ToList();

        // 1) pdfSweep só em páginas que NÃO são puramente raster.
        //    Em página Scanned, o "texto" mora dentro de um XObject de imagem;
        //    apagar a área com pdfSweep destrói o XObject inteiro.
        var sweepable = approved
            .Where(d => !classifications.TryGetValue(d.PageIndex, out var c)
                        || c is PageClassification.NativeText or PageClassification.Hybrid)
            .ToList();
        var locations = sweepable
            .Select(d => new PdfCleanUpLocation(
                d.PageIndex + 1,
                new Rectangle(d.Bounds.X, d.Bounds.Y, d.Bounds.Width, d.Bounds.Height),
                ColorConstants.WHITE))
            .ToList();
        if (locations.Count > 0)
            PdfCleaner.CleanUp(doc, locations);

        // 2) Desenha texto pseudônimo só nas páginas em que apagamos com pdfSweep
        //    (Native/Hybrid). Em Scanned, o ImageRedactor cuida do texto.
        //
        //    IMPORTANTE: quando uma mesma entidade ocupa MÚLTIPLOS rects
        //    (texto quebrado em 2+ linhas), o regex/LLM emite uma decisão
        //    por rect — mas todas têm o mesmo Origin.CharStart. Pra não
        //    repetir o pseudônimo em cada pedaço, desenhamos APENAS no
        //    primeiro rect (o de maior largura, mais visível). Os demais
        //    ficam só com a tarja branca, sem texto duplicado.
        var font = PdfFontFactory.CreateFont();
        var groupedByEntity = sweepable
            .GroupBy(d => (
                d.PageIndex,
                d.Origin?.CharStart ?? -1,
                d.Origin?.CharLength ?? 0,
                d.Origin?.MatchedText ?? string.Empty));

        foreach (var group in groupedByEntity)
        {
            ct.ThrowIfCancellationRequested();
            // Escolhe o "rect principal": maior largura — onde o pseudônimo
            // tem mais espaço para caber legível.
            var primary = group.OrderByDescending(d => d.Bounds.Width).First();

            var type = primary.Origin?.Type ?? EntityType.Other;
            var original = primary.Origin?.MatchedText ?? string.Empty;
            var replacement = string.IsNullOrEmpty(original)
                ? "[[REDIGIDO]]"
                : _pseudonymizer.Substitute(type, original, _ctx);

            var page = doc.GetPage(primary.PageIndex + 1);
            var canvas = new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), doc);
            DrawTextAtRect(canvas, font, replacement, primary.Bounds);
        }

        // 3) Imagens (Scanned + Hybrid): caminho raster.
        await _imageRedactor.RedactImagesAsync(doc, approved, ct).ConfigureAwait(false);

        // 4) Metadados.
        _scrubber.Scrub(doc, _metadataPolicy);
    }

    private static void DrawTextAtRect(PdfCanvas canvas, PdfFont font, string text, PdfRect rect)
    {
        if (string.IsNullOrEmpty(text) || rect.Width <= 0 || rect.Height <= 0) return;
        var size = rect.Height * 0.78f;
        size = Math.Min(size, MaxSizeForWidth(font, text, rect.Width));
        size = Math.Max(size, 4f);
        var baselineY = rect.Y + (rect.Height - size) / 2f + size * 0.2f;
        canvas.SaveState();
        canvas.SetFillColor(ColorConstants.BLACK);
        canvas.BeginText();
        canvas.SetFontAndSize(font, size);
        canvas.MoveText(rect.X, baselineY);
        canvas.ShowText(text);
        canvas.EndText();
        canvas.RestoreState();
    }

    private static float MaxSizeForWidth(PdfFont font, string text, float maxWidth)
    {
        var widthAt1Pt = font.GetWidth(text, 1f);
        if (widthAt1Pt <= 0) return 12f;
        return maxWidth / widthAt1Pt;
    }
}
