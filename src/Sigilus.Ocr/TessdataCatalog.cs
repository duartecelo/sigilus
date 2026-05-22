namespace Sigilus.Ocr;

/// <summary>
/// Catálogo de arquivos de dados do Tesseract OCR disponíveis para
/// download direto do repositório oficial no GitHub
/// (<c>tesseract-ocr/tessdata_fast</c>).
/// </summary>
public sealed record TessdataAsset(
    string Language,
    string FileName,
    string Url,
    long SizeBytes,
    string DisplayName,
    string Description);

public static class TessdataCatalog
{
    /// <summary>Pacotes de dados do Tesseract suportados.</summary>
    public static IReadOnlyList<TessdataAsset> Available { get; } = new[]
    {
        new TessdataAsset(
            Language: "por",
            FileName: "por.traineddata",
            // tessdata_fast: ~3 MB, qualidade boa para texto impresso.
            Url: "https://github.com/tesseract-ocr/tessdata_fast/raw/main/por.traineddata",
            SizeBytes: 3_362_829L,
            DisplayName: "Português (rápido)",
            Description: "Reconhecimento de texto em documentos em português. Tamanho compacto, alta velocidade."),

        new TessdataAsset(
            Language: "por-best",
            FileName: "por.traineddata",
            // tessdata_best: ~15 MB, qualidade máxima (LSTM completo).
            Url: "https://github.com/tesseract-ocr/tessdata_best/raw/main/por.traineddata",
            SizeBytes: 15_336_931L,
            DisplayName: "Português (qualidade máxima)",
            Description: "Mesmo idioma, mais preciso em documentos difíceis (escaneados ruins). 5x maior."),
    };

    /// <summary>Padrão recomendado (português rápido).</summary>
    public static TessdataAsset Recommended => Available[0];
}
