namespace Sigilus.Detection.Llm;

/// <summary>
/// Templates de chat suportados. Cada família de modelos tem um formato
/// próprio de marcação (special tokens). Usar o errado faz o modelo
/// ignorar a instrução de sistema ou alucinar formato.
/// </summary>
public enum ChatTemplate
{
    /// <summary>Llama 3 / 3.1 / 3.2 / 3.3 (Meta). Tokens <c>&lt;|begin_of_text|&gt;</c>, <c>&lt;|eot_id|&gt;</c>.</summary>
    Llama3,

    /// <summary>Gemma 2 / 3 (Google). Tokens <c>&lt;start_of_turn&gt;</c>, <c>&lt;end_of_turn&gt;</c>.</summary>
    Gemma,

    /// <summary>Qwen 2 / 2.5 (Alibaba). ChatML-like: <c>&lt;|im_start|&gt;</c>, <c>&lt;|im_end|&gt;</c>.</summary>
    Qwen,

    /// <summary>Phi 3 / 3.5 / 4 (Microsoft). Tokens <c>&lt;|system|&gt;</c>, <c>&lt;|user|&gt;</c>, <c>&lt;|assistant|&gt;</c>, <c>&lt;|end|&gt;</c>.</summary>
    Phi,

    /// <summary>Mistral / Mixtral. <c>[INST] ... [/INST]</c>.</summary>
    Mistral,
}

/// <summary>
/// Detecta o template chat correto a partir do nome do arquivo GGUF +
/// formata pares (system, user) no template escolhido.
/// </summary>
public static class ChatTemplates
{
    /// <summary>Adivinha o template pelo nome do arquivo GGUF.</summary>
    public static ChatTemplate Detect(string ggufFileName)
    {
        var name = Path.GetFileNameWithoutExtension(ggufFileName).ToLowerInvariant();
        if (name.Contains("gemma")) return ChatTemplate.Gemma;
        if (name.Contains("qwen")) return ChatTemplate.Qwen;
        if (name.Contains("phi")) return ChatTemplate.Phi;
        if (name.Contains("mistral") || name.Contains("mixtral")) return ChatTemplate.Mistral;
        // default: Llama 3 (também funciona razoavelmente bem em Llama 2)
        return ChatTemplate.Llama3;
    }

    /// <summary>Tokens usados como antiprompt (sinal de fim de turno do assistant).</summary>
    public static IReadOnlyList<string> StopTokens(ChatTemplate t) => t switch
    {
        ChatTemplate.Gemma => new[] { "<end_of_turn>", "<eos>" },
        ChatTemplate.Qwen => new[] { "<|im_end|>", "<|endoftext|>" },
        ChatTemplate.Phi => new[] { "<|end|>", "<|endoftext|>" },
        ChatTemplate.Mistral => new[] { "</s>", "[INST]" },
        _ => new[] { "<|eot_id|>", "<|end_of_text|>" },
    };

    /// <summary>Formata uma única troca (system + user) no template alvo.</summary>
    public static string Format(ChatTemplate t, string system, string user) => t switch
    {
        ChatTemplate.Gemma =>
            // Gemma não tem role "system" — concatena no primeiro turno do user.
            $"<start_of_turn>user\n{system}\n\n{user}<end_of_turn>\n<start_of_turn>model\n",

        ChatTemplate.Qwen =>
            $"<|im_start|>system\n{system}<|im_end|>\n" +
            $"<|im_start|>user\n{user}<|im_end|>\n" +
            $"<|im_start|>assistant\n",

        ChatTemplate.Phi =>
            $"<|system|>\n{system}<|end|>\n" +
            $"<|user|>\n{user}<|end|>\n" +
            $"<|assistant|>\n",

        ChatTemplate.Mistral =>
            // Mistral aceita system entre [INST] no primeiro turno
            $"[INST] {system}\n\n{user} [/INST]",

        _ /* Llama3 */ =>
            "<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n" +
            $"{system}<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n\n" +
            $"{user}<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n\n",
    };
}
