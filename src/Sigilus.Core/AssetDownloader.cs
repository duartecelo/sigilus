using System.Net.Http;
using System.Net.Http.Headers;

namespace Sigilus.Core;

/// <summary>
/// Downloader genérico HTTP (compartilhado entre LLM, NER e OCR).
/// Suporta progresso, resume (header <c>Range</c>), verificação de tamanho
/// e cancelamento. Sem dependências externas — só <see cref="HttpClient"/>.
/// </summary>
public sealed class AssetDownloader
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
            Timeout = TimeSpan.FromMinutes(15),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sigilus/2.0 (+downloads)");
        return client;
    }

    /// <summary>
    /// Baixa <paramref name="url"/> para <paramref name="destPath"/> com
    /// resume parcial. <paramref name="expectedSizeBytes"/>, se &gt; 0,
    /// é usado para verificar se o arquivo já está completo (pula
    /// download) e para reportar progresso (caso o servidor não envie
    /// Content-Length por causa de Range).
    /// </summary>
    public async Task<string> DownloadAsync(
        string url,
        string destPath,
        long expectedSizeBytes = 0,
        IProgress<DownloadProgress>? progress = null,
        string? displayName = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var partialPath = destPath + ".partial";
        displayName ??= Path.GetFileName(destPath);

        // Já existe completo?
        if (File.Exists(destPath))
        {
            var sz = new FileInfo(destPath).Length;
            if (expectedSizeBytes <= 0 || sz == expectedSizeBytes)
            {
                progress?.Report(new DownloadProgress(displayName, sz, sz, 0, "Já baixado"));
                return destPath;
            }
            File.Delete(destPath);
        }

        long startByte = 0;
        if (File.Exists(partialPath))
        {
            startByte = new FileInfo(partialPath).Length;
            if (expectedSizeBytes > 0 && startByte >= expectedSizeBytes)
            {
                File.Move(partialPath, destPath);
                progress?.Report(new DownloadProgress(displayName, expectedSizeBytes, expectedSizeBytes, 0, "Concluído"));
                return destPath;
            }
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (startByte > 0)
            req.Headers.Range = new RangeHeaderValue(startByte, null);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                                    .ConfigureAwait(false);
        if (startByte > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            startByte = 0;
            try { File.Delete(partialPath); } catch { }
        }
        resp.EnsureSuccessStatusCode();

        var totalBytes = resp.Content.Headers.ContentLength is long cl
            ? cl + startByte
            : expectedSizeBytes;

        await using var http = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(partialPath,
            startByte > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];
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

            var now = DateTime.UtcNow;
            var dt = (now - lastReport).TotalSeconds;
            if (dt >= 0.2)
            {
                var bps = (long)((received - lastBytes) / dt);
                progress?.Report(new DownloadProgress(displayName, received, totalBytes, bps, BuildEta(received, totalBytes, bps)));
                lastReport = now; lastBytes = received;
            }
        }

        await file.FlushAsync(ct).ConfigureAwait(false);
        file.Close();
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(partialPath, destPath);

        progress?.Report(new DownloadProgress(displayName, received, totalBytes, 0, "Concluído"));
        return destPath;
    }

    private static string BuildEta(long received, long total, long bytesPerSec)
    {
        if (bytesPerSec <= 0 || total <= 0 || received >= total) return "—";
        var remaining = (total - received) / bytesPerSec;
        if (remaining < 60) return $"{remaining}s";
        if (remaining < 3600) return $"{remaining / 60}m {remaining % 60}s";
        return $"{remaining / 3600}h {(remaining % 3600) / 60}m";
    }
}

public readonly record struct DownloadProgress(
    string ItemName,
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
