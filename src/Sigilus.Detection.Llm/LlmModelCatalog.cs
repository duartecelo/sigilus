namespace Sigilus.Detection.Llm;

/// <summary>
/// Catálogo de modelos LLM que o Sigilus sabe baixar diretamente do
/// Hugging Face (HTTP puro — não exige cliente HF instalado nem conta).
/// </summary>
public sealed record LlmModelInfo(
    /// <summary>Nome amigável mostrado na UI.</summary>
    string DisplayName,
    /// <summary>Nome do arquivo .gguf no disco (= como salva em models/llm/).</summary>
    string FileName,
    /// <summary>URL direta do GGUF no Hugging Face.</summary>
    string Url,
    /// <summary>Tamanho aproximado em bytes (para mostrar progresso/decidir caber).</summary>
    long SizeBytes,
    /// <summary>Descrição curta (1-2 linhas) pra ajudar o usuário escolher.</summary>
    string Description);

public static class LlmModelCatalog
{
    /// <summary>Modelos suportados pelo Sigilus, em ordem de recomendação.</summary>
    public static IReadOnlyList<LlmModelInfo> Available { get; } = new[]
    {
        new LlmModelInfo(
            DisplayName: "Gemma 3 4B IT (recomendado)",
            FileName: "google_gemma-3-4b-it-Q4_K_M.gguf",
            Url: "https://huggingface.co/bartowski/google_gemma-3-4b-it-GGUF/resolve/main/google_gemma-3-4b-it-Q4_K_M.gguf",
            SizeBytes: 2_489_758_112L,
            Description: "Google Gemma 3. Equilíbrio ótimo de qualidade e velocidade em PT-BR. ~4 GB de RAM em uso."),

        // NOTA: removida a variante IQ2_M do Gemma 3 — a versão do
        // llama.cpp embutida no LLamaSharp atual não tem suporte ao
        // quant IQ2 para arquitetura Gemma 3 (falha com
        // LoadWeightsFailedException no LoadFromFile). Use Q4_K_M.

        new LlmModelInfo(
            DisplayName: "Qwen 2.5 7B Instruct (máxima qualidade)",
            FileName: "Qwen2.5-7B-Instruct-Q4_K_M.gguf",
            Url: "https://huggingface.co/bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf",
            SizeBytes: 4_683_074_240L,
            Description: "Alibaba Qwen 2.5. Mais preciso, mais lento (10-30s/página). Exige ~7 GB de RAM livre."),

        new LlmModelInfo(
            DisplayName: "Phi 3.5 Mini Instruct (rápido)",
            FileName: "Phi-3.5-mini-instruct-Q4_K_M.gguf",
            Url: "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF/resolve/main/Phi-3.5-mini-instruct-Q4_K_M.gguf",
            SizeBytes: 2_393_232_672L,
            Description: "Microsoft Phi 3.5. Rápido, PT-BR mais limitado que Gemma."),

        new LlmModelInfo(
            DisplayName: "Llama 3.2 3B Instruct (leve)",
            FileName: "Llama-3.2-3B-Instruct-Q4_K_M.gguf",
            Url: "https://huggingface.co/bartowski/Llama-3.2-3B-Instruct-GGUF/resolve/main/Llama-3.2-3B-Instruct-Q4_K_M.gguf",
            SizeBytes: 2_019_377_184L,
            Description: "Meta Llama 3.2 3B. Menor, mais rápido. PT-BR aceitável."),
    };

    /// <summary>Modelo recomendado como padrão (primeiro da lista).</summary>
    public static LlmModelInfo Recommended => Available[0];
}
