namespace Sigilus.Detection.Llm;

/// <summary>
/// Localiza modelos GGUF em <c>./models/llm/</c>. Suporta múltiplos
/// modelos lado a lado (Gemma 3, Qwen 2.5, Phi, etc.) — a UI lista
/// todos e o usuário escolhe.
/// </summary>
public static class LlmModelResolver
{
    /// <summary>Primeiro GGUF encontrado (ordenado por nome). <c>null</c> se nada.</summary>
    public static string? FindGguf() => ListGgufs().FirstOrDefault();

    /// <summary>Todos os GGUFs encontrados, ordenados alfabeticamente.</summary>
    public static IReadOnlyList<string> ListGgufs()
    {
        var env = Environment.GetEnvironmentVariable("SIGILUS_LLM_MODEL");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return new[] { env };

        foreach (var dir in CandidateDirs())
        {
            if (!Directory.Exists(dir)) continue;
            var ggufs = Directory.EnumerateFiles(dir, "*.gguf", SearchOption.TopDirectoryOnly)
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ggufs.Length > 0) return ggufs;
        }
        return Array.Empty<string>();
    }

    private static IEnumerable<string> CandidateDirs()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "models", "llm");
    }
}
