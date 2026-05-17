using System.Text;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Layout;
using iText.Layout.Element;
using Sigilus.Core.Domain;
using Sigilus.Core.Pipeline;
using Sigilus.Detection;
using Sigilus.Pdf.Extraction;
using Sigilus.Pdf.Redaction;
using Xunit;

namespace Sigilus.E2E.Tests;

public sealed class NativeTextRedactionTests
{
    private const string TargetCpf = "390.533.447-05";
    private const string TargetCnpj = "11.222.333/0001-81";

    [Fact]
    public async Task Redacts_cpf_and_cnpj_from_native_text_pdf()
    {
        var inputBytes = BuildSamplePdf();

        var classifier = new HeuristicPageClassifier();
        var extractor = new HybridExtractor(classifier);
        var detector = new RegexEntityDetector(BrazilianRegexRules.Default);
        var engine = new PdfSweepRedactionEngine(new NoopImageRedactor(), new ItextMetadataScrubber());
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

        var redactedBytes = outMs.ToArray();
        var extracted = ReExtractText(redactedBytes);
        Assert.DoesNotContain(TargetCpf, extracted);
        Assert.DoesNotContain(TargetCnpj, extracted);
    }

    private static byte[] BuildSamplePdf()
    {
        using var ms = new MemoryStream();
        using (var writer = new PdfWriter(ms))
        using (var pdf = new PdfDocument(writer))
        using (var doc = new Document(pdf))
        {
            var font = PdfFontFactory.CreateFont();
            doc.SetFont(font);
            doc.Add(new Paragraph("Sentença Judicial — sample para teste."));
            doc.Add(new Paragraph($"Reclamante: João da Silva, CPF {TargetCpf}."));
            doc.Add(new Paragraph($"Empregadora: Acme Ltda., CNPJ {TargetCnpj}."));
            doc.Add(new Paragraph("Processo nº 0001234-56.2024.5.02.0001."));
            doc.Add(new Paragraph("Contato: joao.silva@example.com (11) 91234-5678."));
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
        {
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(p), new LocationTextExtractionStrategy()));
            sb.AppendLine(PdfTextExtractor.GetTextFromPage(doc.GetPage(p), new SimpleTextExtractionStrategy()));
        }
        return sb.ToString();
    }
}
