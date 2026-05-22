namespace Sigilus.Detection.Onnx;

/// <summary>
/// Catálogo de modelos NER disponíveis para download. Cada entrada
/// é um conjunto de 4 arquivos: <c>.onnx</c>, <c>vocab.txt</c>,
/// <c>labels.json</c> e (opcional) <c>tokenizer_config.json</c>.
/// </summary>
public sealed record NerModelAsset(
    string DisplayName,
    string ModelUrl,
    string VocabUrl,
    string LabelsUrl,
    string? TokenizerConfigUrl,
    long ModelSizeBytes,
    string Description);

public static class NerModelCatalog
{
    /// <summary>
    /// Modelos NER pré-quantizados (INT8) disponíveis.
    ///
    /// <para>O Sigilus distribui o LeNER-Br quantizado num GitHub Release.
    /// Se as URLs apontarem para um repo privado/inexistente, a UI mostra
    /// instrução para baixar manualmente — alternativa: rodar o script
    /// <c>scripts/build-ner-model.py</c> que regenera o ONNX a partir do
    /// modelo PyTorch original do Pierre Guillou no Hugging Face.</para>
    /// </summary>
    public static IReadOnlyList<NerModelAsset> Available { get; } = new[]
    {
        new NerModelAsset(
            DisplayName: "LeNER-Br (jurídico, INT8)",
            // Base URL é parametrizável via env SIGILUS_NER_BASE_URL.
            // Default: GitHub Release público (suba os 4 artefatos lá).
            ModelUrl: ResolveBaseUrl() + "ner-ptbr.onnx",
            VocabUrl: ResolveBaseUrl() + "vocab.txt",
            LabelsUrl: ResolveBaseUrl() + "labels.json",
            TokenizerConfigUrl: ResolveBaseUrl() + "tokenizer_config.json",
            ModelSizeBytes: 109_135_610L,
            Description: "Modelo neural BERT pequeno especializado em textos jurídicos PT-BR (LeNER-Br). " +
                         "Detecta pessoas, locais, organizações, leis. ~104 MB quantizado em INT8. " +
                         "Roda em qualquer computador, ~150 ms por página em CPU mediana."),
    };

    public static NerModelAsset Recommended => Available[0];

    /// <summary>
    /// Resolve a URL base dos arquivos NER. Em ordem de preferência:
    /// <list type="number">
    ///   <item>variável de ambiente <c>SIGILUS_NER_BASE_URL</c>;</item>
    ///   <item>default hard-coded.</item>
    /// </list>
    /// A base deve terminar em <c>/</c>.
    /// </summary>
    public static string ResolveBaseUrl()
    {
        var env = Environment.GetEnvironmentVariable("SIGILUS_NER_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.EndsWith('/') ? env : env + "/";

        // Default: hospedado em release público no GitHub. Atualize aqui
        // ou via SIGILUS_NER_BASE_URL caso mude.
        return "https://github.com/duartecelo/sigilus/releases/download/models-v1/";
    }
}
