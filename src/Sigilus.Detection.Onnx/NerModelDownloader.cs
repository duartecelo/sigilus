using Sigilus.Core;

namespace Sigilus.Detection.Onnx;

/// <summary>
/// Baixa um pacote NER completo (4 arquivos) usando <see cref="AssetDownloader"/>.
/// Reporta progresso para cada arquivo separadamente.
/// </summary>
public sealed class NerModelDownloader
{
    private readonly AssetDownloader _inner = new();

    public async Task<string> DownloadAsync(
        NerModelAsset asset,
        string destDir,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        // Modelo (.onnx) — o maior, vai primeiro porque é o que demora.
        await _inner.DownloadAsync(asset.ModelUrl,
            Path.Combine(destDir, "ner-ptbr.onnx"),
            asset.ModelSizeBytes, progress,
            displayName: $"{asset.DisplayName} (modelo)", ct: ct);

        // Vocab — pequeno.
        await _inner.DownloadAsync(asset.VocabUrl,
            Path.Combine(destDir, "vocab.txt"),
            expectedSizeBytes: 0, progress,
            displayName: $"{asset.DisplayName} (vocab)", ct: ct);

        // Labels — micro.
        await _inner.DownloadAsync(asset.LabelsUrl,
            Path.Combine(destDir, "labels.json"),
            expectedSizeBytes: 0, progress,
            displayName: $"{asset.DisplayName} (labels)", ct: ct);

        // tokenizer_config opcional.
        if (!string.IsNullOrWhiteSpace(asset.TokenizerConfigUrl))
        {
            try
            {
                await _inner.DownloadAsync(asset.TokenizerConfigUrl,
                    Path.Combine(destDir, "tokenizer_config.json"),
                    expectedSizeBytes: 0, progress,
                    displayName: $"{asset.DisplayName} (config)", ct: ct);
            }
            catch
            {
                // tokenizer_config é opcional: se o servidor não tiver, usa default.
            }
        }

        return destDir;
    }
}
