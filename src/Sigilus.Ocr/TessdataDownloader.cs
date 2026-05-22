using Sigilus.Core;

namespace Sigilus.Ocr;

/// <summary>
/// Baixa arquivos do Tesseract (tessdata) direto do repositório oficial
/// no GitHub. Sem dependências — usa <see cref="AssetDownloader"/>.
/// </summary>
public sealed class TessdataDownloader
{
    private readonly AssetDownloader _inner = new();

    public Task<string> DownloadAsync(
        TessdataAsset asset,
        string destDir,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dest = Path.Combine(destDir, asset.FileName);
        return _inner.DownloadAsync(asset.Url, dest, asset.SizeBytes, progress,
            displayName: asset.DisplayName, ct: ct);
    }
}
