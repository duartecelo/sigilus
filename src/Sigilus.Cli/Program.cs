using System.CommandLine;
using System.CommandLine.Invocation;
using iText.Kernel.Pdf;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Audit;
using Sigilus.Core.Domain;
using Sigilus.Core.Pipeline;
using Sigilus.Detection;
using Sigilus.Ocr;
using Sigilus.Pdf.Extraction;
using Sigilus.Pdf.Redaction;
using Sigilus.Pdf.Rendering;

namespace Sigilus.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var input = new Option<FileInfo>(new[] { "--input", "-i" }, "PDF de entrada") { IsRequired = true };
        var output = new Option<FileInfo>(new[] { "--output", "-o" }, "PDF de saída") { IsRequired = true };
        var audit = new Option<FileInfo?>(new[] { "--audit", "-a" }, "Sidecar JSON com a auditoria");
        var minConfidence = new Option<float>("--min-confidence", () => 0.85f);

        var root = new RootCommand("Sigilus 2.0 — redator destrutivo de PDFs jurídicos.")
        {
            input, output, audit, minConfidence,
        };

        root.SetHandler(async (ctx) =>
        {
            var inFile = ctx.ParseResult.GetValueForOption(input)!;
            var outFile = ctx.ParseResult.GetValueForOption(output)!;
            var auditFile = ctx.ParseResult.GetValueForOption(audit);
            var threshold = ctx.ParseResult.GetValueForOption(minConfidence);
            ctx.ExitCode = await RunAsync(inFile, outFile, auditFile, threshold).ConfigureAwait(false);
        });

        return await root.InvokeAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> RunAsync(FileInfo input, FileInfo output, FileInfo? audit, float threshold)
    {
        Console.WriteLine($"[sigilus] entrada: {input.FullName}");

        int pageCount;
        await using (var probe = input.OpenRead())
        using (var reader = new PdfReader(probe).SetUnethicalReading(true))
        using (var doc = new PdfDocument(reader))
        {
            pageCount = doc.GetNumberOfPages();
        }
        Console.WriteLine($"[sigilus] {pageCount} página(s)");

        var classifier = new HeuristicPageClassifier();
        var tessdata = TessdataResolver.FindTessdata();
        TesseractOcrEngine? ocr = null;
        PdfiumPageRenderer? renderer = null;
        if (tessdata is not null)
        {
            ocr = new TesseractOcrEngine(tessdata);
            renderer = new PdfiumPageRenderer();
            Console.WriteLine($"[sigilus] OCR ativo (tessdata={tessdata})");
        }
        else
        {
            Console.WriteLine("[sigilus] OCR desativado (nenhum tessdata encontrado; páginas escaneadas serão ignoradas)");
        }

        var extractor = new HybridExtractor(classifier, ocr, renderer);
        var detector = new SnappingEntityDetector(
            new PublicEntityFilterDetector(
                new CompositeEntityDetector(new IEntityDetector[]
                {
                    new RegexEntityDetector(BrazilianRegexRules.Default),
                })));
        var engine = new PdfSweepRedactionEngine(new SkiaImageRedactor(), new ItextMetadataScrubber());
        var pipeline = new RedactionPipeline(extractor, detector, engine, pageCount);

        var inBytes = await File.ReadAllBytesAsync(input.FullName).ConfigureAwait(false);
        using var inMs = new MemoryStream(inBytes, writable: false);
        await using var outFs = output.Create();

        var decisions = await pipeline.RunAsync(
            inMs,
            outFs,
            review: (p, _, entities) =>
            {
                var approved = entities
                    .Where(e => e.Confidence >= threshold)
                    .Select(e => new RedactionDecision(
                        Bounds: e.Bounds,
                        PageIndex: e.PageIndex,
                        Approved: true,
                        Reason: $"auto-approve confidence>={threshold:F2}",
                        Origin: e))
                    .ToList();
                Console.WriteLine($"[sigilus] página {p + 1}: {entities.Count} detecções, {approved.Count} aprovadas");
                return approved;
            },
            ct: CancellationToken.None).ConfigureAwait(false);

        if (audit is not null)
        {
            using var hashStream = new MemoryStream(inBytes, writable: false);
            var log = AuditWriter.Build(hashStream, pageCount, decisions);
            AuditWriter.WriteTo(log, audit.FullName);
            Console.WriteLine($"[sigilus] audit: {audit.FullName}");
        }

        Console.WriteLine($"[sigilus] saída: {output.FullName}");
        return 0;
    }
}
