using System.Text.Json;

namespace Sigilus.Detection.Onnx;

public sealed record NerModelPaths(string ModelPath, string VocabPath, string[] Labels, bool LowerCase = true);

/// <summary>
/// Localiza um modelo NER ONNX local com a mesma estratégia do
/// <c>TessdataResolver</c>: variável de ambiente → pasta ao lado do
/// exe → subindo até 5 níveis (procura <c>models/</c>).
/// </summary>
/// <remarks>
/// Espera 3 arquivos:
/// <list type="bullet">
///   <item><c>ner-ptbr.onnx</c></item>
///   <item><c>vocab.txt</c></item>
///   <item><c>labels.json</c> com array de strings em ordem: <c>["O","B-PER",...]</c></item>
/// </list>
/// Retorna <c>null</c> se faltar qualquer um — sem exceção, sem crash.
/// </summary>
public static class NerModelResolver
{
    private const string ModelFile = "ner-ptbr.onnx";
    private const string VocabFile = "vocab.txt";
    private const string LabelsFile = "labels.json";

    public static NerModelPaths? Find()
    {
        var env = Environment.GetEnvironmentVariable("SIGILUS_NER_MODELS");
        if (TryDir(env, out var found)) return found;

        var baseDir = AppContext.BaseDirectory;
        if (TryDir(Path.Combine(baseDir, "models"), out found)) return found;

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (TryDir(Path.Combine(dir.FullName, "models"), out found)) return found;
        }
        return null;
    }

    private static bool TryDir(string? dir, out NerModelPaths? found)
    {
        found = null;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        var model = Path.Combine(dir, ModelFile);
        var vocab = Path.Combine(dir, VocabFile);
        var labels = Path.Combine(dir, LabelsFile);
        if (!File.Exists(model) || !File.Exists(vocab) || !File.Exists(labels)) return false;

        try
        {
            var arr = JsonSerializer.Deserialize<string[]>(File.ReadAllText(labels));
            if (arr is null || arr.Length == 0) return false;

            // Lê tokenizer_config.json (opcional). Se "do_lower_case": false →
            // modelo é cased (LeNER-Br), o que muda como tokenizamos. Default
            // true (assume uncased) é o padrão BERT base PT-BR comum.
            var lowercase = true;
            var tokCfgPath = Path.Combine(dir, "tokenizer_config.json");
            if (File.Exists(tokCfgPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(tokCfgPath));
                    if (doc.RootElement.TryGetProperty("do_lower_case", out var dlc))
                        lowercase = dlc.GetBoolean();
                }
                catch { /* mantém default */ }
            }

            found = new NerModelPaths(model, vocab, arr, lowercase);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
