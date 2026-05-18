using System.Net.Http;
using System.Net.Http.Headers;

namespace Sigilus.Detection.Llm;

/// <summary>
/// Baixa modelos GGUF do Hugging Face via HTTP puro. Sem dependências
/// (não exige Python, biblioteca HF, conta nem login). Suporta:
/// <list type="bullet">
///   <item>Download em pedaços com progresso (callback);</item>
///   <item>Resume parcial (header <c>Range</c>) — se a conexão cair,
///     o próximo download continua de onde parou;</item>
///   <item>Verificação de tamanho contra o catálogo;</item>
///   <item>Cancelamento via <see cref="CancellationToken"/>.</item>
/// </list>
/// </summary>
public sealed class LlmModelDownloader : IDisposable
{
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };
        var client = new HttpClient(handler)
        {
            // Modelos grandes (~5 GB) em conexão lenta podem levar bastante tempo.
            // Esse timeout é por requisição HTTP, não cobre o stream — leitura
            // de body usa o CancellationToken separado.
            Timeout = TimeSpan.FromMinutes(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sigilus/2.0 (+downloads)");
        return client;
    }

    /// <summary>
    /// Baixa <paramref name="model"/> para <paramref name="destDir"/>.
    /// Retorna o caminho final do arquivo. Se já existir com tamanho
    /// correto, devolve imediatamente sem baixar. Se existir parcial,
    /// continua de onde parou (resume).
    /// </summary>
    public async Task<string> DownloadAsync(
        LlmModelInfo model,
        string destDir,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var finalPath = Path.Combine(destDir, model.FileName);
        var partialPath = finalPath + ".partial";

        // Caso 1: arquivo final já existe com tamanho esperado → nada a fazer.
        if (File.Exists(finalPath))
        {
            var sz = new FileInfo(finalPath).Length;
            if (sz == model.SizeBytes)
            {
                progress?.Report(new DownloadProgress(model, sz, model.SizeBytes, 0, "Já baixado"));
                return finalPath;
            }
            // Tamanho errado → trata como inválido e força re-download
            File.Delete(finalPath);
        }

        // Caso 2: download parcial existente → retoma.
        long startByte = 0;
        if (File.Exists(partialPath))
        {
            startByte = new FileInfo(partialPath).Length;
            if (startByte >= model.SizeBytes)
            {
                // Já temos tudo no .partial — renomeia e termina.
                File.Move(partialPath, finalPath);
                progress?.Report(new DownloadProgress(model, model.SizeBytes, model.SizeBytes, 0, "Concluído"));
                return finalPath;
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, model.Url);
        if (startByte > 0)
            req.Headers.Range = new RangeHeaderValue(startByte, null);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                    .ConfigureAwait(false);
        // Se o servidor não suportou Range, ele responde 200 (não 206) — nesse caso
        // recomeçamos do zero.
        if (startByte > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            startByte = 0;
            try { File.Delete(partialPath); } catch { }
        }
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength is long cl
            ? cl + startByte
            : model.SizeBytes;

        await using var http = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(partialPath,
            startByte > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];   // 1 MB
        long received = startByte;
        var lastReport = DateTime.UtcNow;
        var lastBytes = received;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await http.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n <= 0) break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            received += n;

            // Reporta progresso ~5 vezes por segundo.
            var now = DateTime.UtcNow;
            var dt = (now - lastReport).TotalSeconds;
            if (dt >= 0.2)
            {
                var bps = (long)((received - lastBytes) / dt);
                progress?.Report(new DownloadProgress(model, received, totalBytes, bps, BuildEta(received, totalBytes, bps)));
                lastReport = now; lastBytes = received;
            }
        }

        // Flush + fecha + renomeia atomicamente.
        await file.FlushAsync(ct).ConfigureAwait(false);
        file.Close();
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(partialPath, finalPath);

        progress?.Report(new DownloadProgress(model, received, totalBytes, 0, "Concluído"));
        return finalPath;
    }

    private static string BuildEta(long received, long total, long bytesPerSec)
    {
        if (bytesPerSec <= 0 || total <= 0 || received >= total) return "—";
        var remaining = (total - received) / bytesPerSec;
        if (remaining < 60) return $"{remaining}s";
        if (remaining < 3600) return $"{remaining / 60}m {remaining % 60}s";
        return $"{remaining / 3600}h {(remaining % 3600) / 60}m";
    }

    public void Dispose() { /* HttpClient é estático compartilhado */ }
}

public readonly record struct DownloadProgress(
    LlmModelInfo Model,
    long BytesReceived,
    long BytesTotal,
    long BytesPerSecond,
    string Eta)
{
    public double Percent => BytesTotal == 0 ? 0 : 100.0 * BytesReceived / BytesTotal;
    public string ReceivedMb => $"{BytesReceived / 1024.0 / 1024.0:F1} MB";
    public string TotalMb => $"{BytesTotal / 1024.0 / 1024.0:F1} MB";
    public string SpeedHuman => BytesPerSecond switch
    {
        >= 1_048_576 => $"{BytesPerSecond / 1024.0 / 1024.0:F1} MB/s",
        >= 1024 => $"{BytesPerSecond / 1024.0:F0} KB/s",
        _ => $"{BytesPerSecond} B/s",
    };
}
