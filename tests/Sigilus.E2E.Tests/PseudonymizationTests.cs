using System.Text;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Layout;
using iText.Layout.Element;
using Sigilus.Core.Domain;
using Sigilus.Core.Pseudonymization;
using Sigilus.Core.Pipeline;
using Sigilus.Detection;
using Sigilus.Pdf.Extraction;
using Sigilus.Pdf.Redaction;
using Xunit;

namespace Sigilus.E2E.Tests;

public sealed class PseudonymizationTests
{
    private const string TargetCpf = "390.533.447-05";
    private const string TargetCnpj = "11.222.333/0001-81";

    [Fact]
    public async Task Pseudonymize_replaces_cpf_and_cnpj_with_valid_fakes()
    {
        var inputBytes = BuildSamplePdf();

        var classifier = new HeuristicPageClassifier();
        var extractor = new HybridExtractor(classifier);
        var detector = new RegexEntityDetector(BrazilianRegexRules.Default);
        var pseudo = new BrazilianPseudonymizer();
        var ctx = new PseudonymContext();
        var engine = new PseudonymizationRedactionEngine(
            pseudo,
            ctx,
            new SkiaPseudonymizingImageRedactor(pseudo, ctx),
            new ItextMetadataScrubber());
        var pipeline = new RedactionPipeline(extractor, detector, engine, pageCount: 1);

        using var inMs = new MemoryStream(inputBytes, writable: false);
        using var outMs = new MemoryStream();

        var decisions = await pipeline.RunAsync(
            inMs, outMs,
            review: (_, _, entities) => entities
                .Select(e => new RedactionDecision(e.Bounds, e.PageIndex, Approved: true, Reason: "auto", Origin: e))
                .ToList(),
            ct: CancellationToken.None);

        Assert.NotEmpty(decisions);

        var extracted = ReExtractText(outMs.ToArray());

        // Originais foram destruídos.
        Assert.DoesNotContain(TargetCpf, extracted);
        Assert.DoesNotContain(TargetCnpj, extracted);

        // Pseudônimos aparecem com formato CPF/CNPJ válidos.
        Assert.Matches(@"\d{3}\.\d{3}\.\d{3}-\d{2}", extracted);
        Assert.Matches(@"\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}", extracted);
    }

    private static byte[] BuildSamplePdf()
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var pdf = new PdfDocument(writer))
        using (var doc = new Document(pdf))
        {
            doc.SetFont(PdfFontFactory.CreateFont());
            doc.Add(new Paragraph($"Reclamante CPF {TargetCpf}."));
            doc.Add(new Paragraph($"Empresa CNPJ {TargetCnpj}."));
        }
        return ms.ToArray();
    }

    private static string ReExtractText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var reader = new PdfReader(ms).SetUnethicalReading(true);
        using var doc = new PdfDocument(reader);
        var sb = new StringBuilder();
        for (var p = 1; p <= doc.GetNumberOfPages(); p++)
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(p), new LocationTextExtractionStrategy()));
        return sb.ToString();
    }
}
