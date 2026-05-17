using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Detection.Coordinates;

namespace Sigilus.Detection.Llm;

/// <summary>
/// Detector via LLM local (llama.cpp / LLamaSharp). Manda chunks do texto
/// da página com um prompt que pede uma lista JSON de entidades sensíveis
/// e converte a resposta em <see cref="DetectedEntity"/>, validando cada
/// item contra o texto original (descarta alucinações).
///
/// <para>Funciona com qualquer GGUF; padrão recomendado: Gemma 3 4B IT
/// Q4_K_M (~2.5 GB) ou Qwen 2.5 7B (~4.7 GB).</para>
/// </summary>
public sealed class LlmEntityDetector : IEntityDetector, IDisposable
{
    private readonly LLamaWeights _model;
    private readonly ModelParams _params;
    private readonly StatelessExecutor _executor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ChatTemplate _template;
    private readonly IReadOnlyList<string> _stopTokens;
    private readonly string _modelName;

    private const int MaxNewTokens = 800;

    public string ModelName => _modelName;
    public ChatTemplate Template => _template;

    public LlmEntityDetector(string ggufPath, uint contextSize = 8192, int threads = 0)
    {
        _modelName = Path.GetFileNameWithoutExtension(ggufPath);
        _template = ChatTemplates.Detect(ggufPath);
        _stopTokens = ChatTemplates.StopTokens(_template);

        _params = new ModelParams(ggufPath)
        {
            ContextSize = contextSize,
            GpuLayerCount = 0,
            Threads = threads > 0 ? threads : Math.Max(2, Environment.ProcessorCount / 2),
        };
        _model = LLamaWeights.LoadFromFile(_params);
        // StatelessExecutor cria um novo KV cache a cada chamada — não acumula
        // estado entre páginas/chunks (que causava ContextOverflowException).
        _executor = new StatelessExecutor(_model, _params);
    }

    public static Task<LlmEntityDetector> LoadAsync(
        string ggufPath, uint contextSize = 8192, int threads = 0,
        CancellationToken ct = default)
    {
        if (!File.Exists(ggufPath))
            return Task.FromException<LlmEntityDetector>(new FileNotFoundException("Modelo GGUF não encontrado.", ggufPath));

        return Task.Factory.StartNew(
            () =>
            {
                ct.ThrowIfCancellationRequested();
                // Pré-condições: OneDrive/iCloud/Google Drive guardam arquivos
                // grandes como "placeholder" — só baixam quando alguém abre o
                // arquivo de verdade. O llama.cpp usa mmap, que NÃO dispara
                // hidratação automática → falha com LoadWeightsFailedException.
                // Forçamos a hidratação aqui lendo o arquivo inteiro uma vez.
                EnsureFileFullyMaterialized(ggufPath, ct);
                return new LlmEntityDetector(ggufPath, contextSize, threads);
            },
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// Garante que o arquivo está totalmente baixado no disco local (não é
    /// um placeholder do OneDrive/iCloud/Drive). Lê em blocos descartando o
    /// conteúdo — força o cloud provider a hidratar.
    /// </summary>
    private static void EnsureFileFullyMaterialized(string path, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          bufferSize: 1 << 20, FileOptions.SequentialScan);
            var buffer = new byte[1 << 20];   // 1 MB
            while (fs.Read(buffer, 0, buffer.Length) > 0)
            {
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            throw new IOException(
                $"Não foi possível ler o arquivo do modelo. Se o Sigilus está em " +
                $"OneDrive/iCloud/Google Drive, mova-o para um diretório local " +
                $"(ex: C:\\Sigilus\\). Detalhe: {ex.Message}", ex);
        }
    }

    /// <summary>True se o caminho parece estar dentro de pasta sincronizada por OneDrive/iCloud/Google Drive.</summary>
    public static bool LooksLikeCloudSyncedPath(string path)
    {
        var norm = path.Replace('/', '\\');
        return norm.Contains("\\OneDrive", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("\\iCloud", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("\\iCloudDrive", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("\\Google Drive", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("\\GoogleDrive", StringComparison.OrdinalIgnoreCase)
            || norm.Contains("\\Dropbox", StringComparison.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<DetectedEntity> DetectAsync(
        PageContext page,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(page.ConcatenatedText)) yield break;

        var text = page.ConcatenatedText;
        // Normaliza o texto OCR: junta tokens "uma palavra por linha" em
        // parágrafos legíveis pro LLM. Sem isso o Gemma/Llama lê linhas
        // soltas e ignora contexto.
        var normalized = NormalizeOcrLines(text);

        var chunks = ChunkText(normalized, maxChars: 1600, overlap: 120);
        var index = new CharCoordIndex(page);

        // Dedup por (CharStart, Length): chunks com overlap acham a mesma
        // entidade 2× ; é o mesmo span literal, mesmo rect.
        var seen = new HashSet<(int, int)>();

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var items = await Task.Run(() => ExtractFromChunk(chunk.Text, ct), ct);
            foreach (var item in items)
            {
                var (idx, len) = LocateInOriginal(text, item.Value);
                if (idx < 0) continue;
                if (!seen.Add((idx, len))) continue;

                var rects = index.RectsFor(idx, len);
                foreach (var rect in rects)
                {
                    if (rect.IsEmpty) continue;
                    var matchedText = text.Substring(idx, len);
                    yield return new DetectedEntity(
                        Type: item.Type,
                        MatchedText: matchedText,
                        Confidence: 0.88f,
                        Bounds: rect,
                        PageIndex: page.PageIndex,
                        Source: DetectionSource.Ner,
                        CharStart: idx,
                        CharLength: len);
                }
            }
        }
    }

    private List<(string Value, EntityType Type)> ExtractFromChunk(string chunkText, CancellationToken ct)
    {
        _gate.Wait(ct);
        try
        {
            var prompt = BuildPrompt(chunkText);
            var sb = new StringBuilder(1024);
            var infer = new InferenceParams
            {
                MaxTokens = MaxNewTokens,
                AntiPrompts = _stopTokens.ToList(),
                SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.0f },
            };
            var enumerator = _executor.InferAsync(prompt, infer, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    var moveNext = enumerator.MoveNextAsync().AsTask();
                    moveNext.Wait(ct);
                    if (!moveNext.Result) break;
                    sb.Append(enumerator.Current);
                    if (sb.Length > 4096) break;
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().Wait();
            }
            return ParseJson(sb.ToString());
        }
        finally { _gate.Release(); }
    }

    private string BuildPrompt(string chunk)
    {
        const string System =
            "Você é um especialista em anonimização de documentos jurídicos brasileiros sob a LGPD. Sua tarefa é encontrar dados pessoais sensíveis no texto.\n\n" +
            "EXTRAIA:\n" +
            "- Nomes COMPLETOS de pessoas físicas (com sobrenome): \"João da Silva\", \"Maria Santos Pereira\". Inclua TODOS os componentes do nome.\n" +
            "- Endereços de logradouro: \"Rua Marques de Souza, 528\", \"Av. Brasil, 1500\".\n" +
            "- Nomes próprios de empresas privadas (não órgãos públicos).\n" +
            "- E-mails e telefones se aparecerem isolados (sem prefixo Tel./E-mail).\n\n" +
            "NÃO EXTRAIA:\n" +
            "- Cargos ou títulos sozinhos: \"Promotor\", \"Diretor\", \"Sr.\", \"Excelentíssimo\".\n" +
            "- Órgãos públicos: Ministério Público, Tribunal, OAB, Polícia, INSS, Receita Federal.\n" +
            "- Cidades, estados, bairros, regiões.\n" +
            "- Datas, horas, valores monetários, números de processo, leis, jurisprudência.\n" +
            "- Prefixos como \"CNPJ:\", \"Tel.\", \"E-mail:\" — não inclua na resposta.\n\n" +
            "FORMATO DE RESPOSTA:\n" +
            "Uma linha JSON por entidade encontrada:\n" +
            "{\"text\":\"<trecho LITERAL do texto>\",\"type\":\"PersonName|Address|Other\"}\n" +
            "Se nada sensível: NONE\n" +
            "Não escreva mais nada além das linhas JSON.\n\n" +
            "EXEMPLOS:\n\n" +
            "Texto: \"Conforme determinação do Promotor Sr. Manoel Luiz Prates Guimarães, residente na Rua das Flores, 200.\"\n" +
            "Resposta:\n" +
            "{\"text\":\"Manoel Luiz Prates Guimarães\",\"type\":\"PersonName\"}\n" +
            "{\"text\":\"Rua das Flores, 200\",\"type\":\"Address\"}\n\n" +
            "Texto: \"O Ministério Público do Estado do Rio Grande do Sul instaurou inquérito.\"\n" +
            "Resposta:\n" +
            "NONE\n\n" +
            "Texto: \"Diretor Cristiano Araújo da Silva e o assistente Celso Prezzi compareceram.\"\n" +
            "Resposta:\n" +
            "{\"text\":\"Cristiano Araújo da Silva\",\"type\":\"PersonName\"}\n" +
            "{\"text\":\"Celso Prezzi\",\"type\":\"PersonName\"}";

        return ChatTemplates.Format(_template, System, "Texto: \"" + chunk + "\"\nResposta:");
    }

    private static List<(string Value, EntityType Type)> ParseJson(string output)
    {
        var result = new List<(string, EntityType)>();
        if (string.IsNullOrWhiteSpace(output)) return result;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd(',');
            if (line.Length < 5 || line.Equals("NONE", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.StartsWith("{")) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("text", out var t)) continue;
                if (!doc.RootElement.TryGetProperty("type", out var ty)) continue;
                var value = t.GetString();
                var typeStr = ty.GetString();
                if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(typeStr)) continue;
                if (value.Length < 3) continue;
                var type = typeStr switch
                {
                    "PersonName" => EntityType.PersonName,
                    "Address" => EntityType.Address,
                    "Other" => EntityType.Other,
                    _ => (EntityType?)null,
                };
                if (type is null) continue;
                result.Add((value!.Trim(), type.Value));
            }
            catch (JsonException) { }
        }
        return result;
    }

    // ─────────────────────── Normalização & matching ───────────────────────

    /// <summary>
    /// Junta tokens OCR ("uma palavra por linha") em parágrafos. Mantém
    /// quebra de linha quando a linha termina com pontuação forte ou
    /// quando o gap entre linhas é grande (heurística pelo tamanho).
    /// </summary>
    private static string NormalizeOcrLines(string text)
    {
        // Se o texto tem MUITAS linhas curtas (mediana < 15 chars), assumimos OCR
        // e juntamos com espaço. Senão devolve igual (texto nativo já vem bem).
        var lines = text.Split('\n');
        if (lines.Length < 20) return text;
        var lens = lines.Where(l => l.Trim().Length > 0).Select(l => l.Trim().Length).OrderBy(x => x).ToArray();
        if (lens.Length == 0) return text;
        var median = lens[lens.Length / 2];
        if (median >= 30) return text;   // não parece OCR fragmentado

        var sb = new StringBuilder(text.Length);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) { sb.Append('\n'); continue; }
            if (sb.Length > 0 && sb[^1] != '\n' && sb[^1] != ' ') sb.Append(' ');
            sb.Append(line);
            // Mantém quebra "real" quando há pontuação forte de fim de frase
            if (line.EndsWith('.') || line.EndsWith('!') || line.EndsWith('?')
                || line.EndsWith(':') || line.EndsWith(';'))
                sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Localiza <paramref name="needle"/> em <paramref name="hay"/> com
    /// tolerâncias progressivas: case → acento → fuzzy por tokens.
    /// Devolve (-1, 0) se nada bater.
    /// </summary>
    private static (int Start, int Length) LocateInOriginal(string hay, string needle)
    {
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(hay)) return (-1, 0);

        // 1) Match exato case-insensitive.
        var idx = hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return (idx, needle.Length);

        // 2) Match acent/case-insensitive: percorre o hay e compara
        //    char-a-char ignorando diacríticos.
        var found = IndexOfFold(hay, needle);
        if (found >= 0) return (found, MatchLengthFold(hay, found, needle));

        // 3) Fuzzy por tokens: pega as palavras do needle, acha a primeira
        //    no hay; verifica se as demais aparecem nas N palavras seguintes.
        //    Cobre casos OCR onde palavras estão separadas por \n.
        var tokens = SplitWords(needle);
        if (tokens.Length == 0) return (-1, 0);
        var firstIdx = IndexOfFold(hay, tokens[0]);
        while (firstIdx >= 0)
        {
            // Janela = posição da primeira palavra até 2× length original
            var winEnd = Math.Min(hay.Length, firstIdx + needle.Length * 2);
            var window = hay[firstIdx..winEnd];
            if (AllTokensInOrder(window, tokens))
            {
                // Comprimento real = posição do último token + tamanho do último
                var endRel = LastTokenEndOffset(window, tokens);
                if (endRel > 0) return (firstIdx, endRel);
            }
            firstIdx = IndexOfFold(hay, tokens[0], firstIdx + 1);
        }
        return (-1, 0);
    }

    private static int IndexOfFold(string hay, string needle, int startAt = 0)
    {
        if (needle.Length == 0) return startAt;
        for (var i = startAt; i + needle.Length <= hay.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (Fold(hay[i + j]) != Fold(needle[j])) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static int MatchLengthFold(string hay, int start, string needle)
        => Math.Min(needle.Length, hay.Length - start);

    private static char Fold(char c)
    {
        var lower = char.ToLowerInvariant(c);
        var norm = lower.ToString().Normalize(NormalizationForm.FormD);
        foreach (var ch in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                return ch;
        return lower;
    }

    private static string[] SplitWords(string s) =>
        s.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '"', '\'', '(', ')', '—', '–', '-' },
                StringSplitOptions.RemoveEmptyEntries);

    private static bool AllTokensInOrder(string window, string[] tokens)
    {
        var cursor = 0;
        foreach (var tok in tokens)
        {
            var f = IndexOfFold(window, tok, cursor);
            if (f < 0) return false;
            cursor = f + tok.Length;
        }
        return true;
    }

    private static int LastTokenEndOffset(string window, string[] tokens)
    {
        var cursor = 0;
        var lastEnd = 0;
        foreach (var tok in tokens)
        {
            var f = IndexOfFold(window, tok, cursor);
            if (f < 0) return -1;
            lastEnd = f + tok.Length;
            cursor = lastEnd;
        }
        return lastEnd;
    }

    private static IEnumerable<(string Text, int Start)> ChunkText(string text, int maxChars, int overlap)
    {
        if (text.Length <= maxChars)
        {
            yield return (text, 0);
            yield break;
        }
        var i = 0;
        while (i < text.Length)
        {
            var len = Math.Min(maxChars, text.Length - i);
            if (i + len < text.Length)
            {
                var nl = text.LastIndexOf('\n', i + len - 1, len);
                if (nl > i + maxChars / 2) len = nl - i;
            }
            yield return (text.Substring(i, len), i);
            if (i + len >= text.Length) break;
            i += len - overlap;
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _model.Dispose();
    }
}
