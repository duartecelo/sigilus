using Sigilus.Core;

namespace Sigilus.Detection.Llm;

/// <summary>
/// Wrapper de <see cref="AssetDownloader"/> especializado em modelos LLM
/// — sabe a relação <see cref="LlmModelInfo"/> ↔ caminho final em
/// <c>models/llm/</c>.
/// </summary>
public sealed class LlmModelDownloader : IDisposable
{
    private readonly AssetDownloader _inner = new();

    /// <summary>
    /// Baixa <paramref name="model"/> para <paramref name="destDir"/>.
    /// Retorna o caminho final do arquivo. Resume parcial + verificação
    /// de tamanho — ver <see cref="AssetDownloader.DownloadAsync"/>.
    /// </summary>
    public Task<string> DownloadAsync(
        LlmModelInfo model,
        string destDir,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dest = Path.Combine(destDir, model.FileName);
        return _inner.DownloadAsync(model.Url, dest, model.SizeBytes, progress,
            displayName: model.DisplayName, ct: ct);
    }

    public void Dispose() { /* HttpClient é estático em AssetDownloader */ }
}
