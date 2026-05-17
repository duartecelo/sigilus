# Sigilus.Detection.Llm

Detector de entidades sensíveis via **LLM local** (llama.cpp via
LLamaSharp). Roda Llama 3.2 / Gemma 3 / qualquer GGUF compatível
diretamente em CPU — sem internet, sem GPU, sem cloud.

## Para que serve

NER tradicional (`Sigilus.Detection.Onnx`) é rápido e preciso, mas
está limitado pelos rótulos de treino (PESSOA, LOCAL, ORG…). Quando o
PDF tem dados sensíveis em formato livre — "filho menor de idade
identificado como J.S.", "a vítima, sobrenome Silva, mora próximo ao
estádio", "vizinho conhecido como Seu Zé" — o NER passa batido.

O LLM lê o texto inteiro e raciocina. Em troca, é **bem mais lento**:
~5-30 segundos por página em CPU mediana (i5/Ryzen 5). Por isso é
**opcional, executado como segunda fase** após o NER rápido + regex.

## Dependências NuGet

| Pacote | Versão | Por quê |
|---|---|---|
| `LLamaSharp` | 0.20.0 | Bindings .NET para llama.cpp. |
| `LLamaSharp.Backend.Cpu` | 0.20.0 | Binários nativos x64 (AVX/AVX2/AVX-512 auto-detect). |

Para GPU NVIDIA, troque `Backend.Cpu` por `Backend.Cuda12`; para
DirectML, `Backend.DirectML`. **Não precisa** — em CPU já funciona em
qualquer máquina moderna.

## Layout

```
Sigilus.Detection.Llm/
├── ChatTemplate.cs        ← detecção e formatação de templates (Llama/Gemma/Qwen/Phi/Mistral)
├── LlmEntityDetector.cs   ← prompt + parsing + IEntityDetector
└── LlmModelResolver.cs    ← lista todos os .gguf em ./models/llm/
```

### [`ChatTemplate`](ChatTemplate.cs)

Cada família de LLM usa um formato de marcação diferente para distinguir
turno do sistema, do usuário e do assistente. Usar o template errado faz
o modelo ignorar a instrução de sistema (= alucinação garantida).

| Família | Tokens chave |
|---|---|
| Llama 3 / 3.1 / 3.2 / 3.3 | `<\|begin_of_text\|>`, `<\|start_header_id\|>system<\|end_header_id\|>`, `<\|eot_id\|>` |
| Gemma 2 / 3 | `<start_of_turn>user`, `<end_of_turn>`, `<start_of_turn>model` |
| Qwen 2 / 2.5 | `<\|im_start\|>system`, `<\|im_end\|>`, `<\|im_start\|>assistant` |
| Phi 3 / 3.5 / 4 | `<\|system\|>`, `<\|user\|>`, `<\|assistant\|>`, `<\|end\|>` |
| Mistral / Mixtral | `[INST] ... [/INST]` |

A função `ChatTemplates.Detect(filePath)` faz match pelo nome do arquivo:
arquivo com "gemma" no nome → Gemma, "qwen" → Qwen, etc. Default é
Llama 3.

### [`LlmModelResolver`](LlmModelResolver.cs)

Procura, em ordem:
1. Variável `SIGILUS_LLM_MODEL` (caminho completo do .gguf).
2. `./models/llm/*.gguf` ao lado do executável.
3. Mesmas pastas subindo até 5 níveis (útil em dev).

Retorna o **maior** `.gguf` encontrado (assume que é o modelo "real",
ignorando GGUFs auxiliares como tokenizers). `null` se não achar.

### [`LlmEntityDetector`](LlmEntityDetector.cs)

Implementa `IEntityDetector`. Carrega o modelo no construtor
(LLamaSharp `LLamaWeights.LoadFromFile`); cria 1 `LLamaContext` único e
serializa as chamadas com `SemaphoreSlim` (llama.cpp não é thread-safe
no mesmo contexto).

Para cada página:

1. **Chunking**: divide o texto em pedaços de até 1800 chars (cabem em
   ~512 tokens cada, deixando espaço pro prompt + resposta no contexto
   8k). Overlap de 100 chars entre chunks para não cortar entidades.
2. **Prompt** (formato chat template do Llama 3):
   ```
   <|begin_of_text|><|start_header_id|>system<|end_header_id|>
   Você é um assistente que extrai dados pessoais sensíveis...
   REGRAS:
   - Extraia apenas: nomes de PESSOAS FÍSICAS, endereços, empresas privadas.
   - NÃO extraia: órgãos públicos, cidades, estados, datas, valores.
   - Devolva uma linha JSON por entidade: {"text":"...","type":"..."}
   <|eot_id|><|start_header_id|>user<|end_header_id|>
   TEXTO: ...
   <|eot_id|><|start_header_id|>assistant<|end_header_id|>
   ```
3. **Inferência**: temperatura 0 (determinístico), max 800 tokens.
4. **Parsing**: split por `\n`, parse de cada linha como JSON. Tipos
   válidos: `PersonName`, `Address`, `Other`. Linhas inválidas
   silenciosamente descartadas.
5. **Validação anti-alucinação**: pra cada entidade extraída, faz
   `text.IndexOf(value, chunkStart, OrdinalIgnoreCase)`. Se não achar
   → descarta (o LLM inventou). Se achar, usa o offset real para
   converter em `DetectedEntity` via `CharCoordIndex`.

### Por que prompt em formato Llama 3 chat?

O template `<|begin_of_text|>...<|eot_id|>` é o oficial do Llama 3 e
funciona com Llama 3.1, 3.2, 3.3. Gemma e Mistral aceitam (parsing
mais permissivo) mas o ideal seria detectar o modelo e mudar template.
Em prática, `InteractiveExecutor` consome raw text e o modelo
"entende" o role mesmo com template diferente — perda mínima de
qualidade.

## Como obter um modelo

Pasta destino: `S:\work\sigilus2\models\llm\`. A UI lista TODOS os
`.gguf` da pasta — você escolhe qual ativar no ComboBox de Configurações.

### 🥇 Gemma 3 4B IT — **padrão recomendado**

- ~2.5 GB, Q4_K_M, **melhor PT-BR** entre modelos pequenos (Google
  treinou em 140 idiomas).
- Excelente em seguir formato JSON. Latência ~3-8s/página em CPU
  mediana, RAM ~4 GB livre.
- Hugging Face: `bartowski/google_gemma-3-4b-it-GGUF`
- Arquivo: `google_gemma-3-4b-it-Q4_K_M.gguf`

### 🥈 Qwen 2.5 7B Instruct — mais preciso, exige mais RAM

- ~4.5 GB, Q4_K_M. Em testes, melhor extração estruturada PT-BR da
  faixa pequena (Alibaba).
- Precisa ~6 GB RAM livre. Latência ~8-20s/página.
- HF: `bartowski/Qwen2.5-7B-Instruct-GGUF`
- Arquivo: `Qwen2.5-7B-Instruct-Q4_K_M.gguf`

### 🥉 Phi 3.5 Mini Instruct — mais rápido, PT-BR limitado

- ~2 GB, Microsoft, ótimo em JSON mas treinado mais em inglês.
- HF: `bartowski/Phi-3.5-mini-instruct-GGUF`
- Arquivo: `Phi-3.5-mini-instruct-Q4_K_M.gguf`

### Llama 3.2 3B Instruct — fallback

- ~2 GB. Funciona, mas Gemma 3 supera em PT-BR.
- HF: `bartowski/Llama-3.2-3B-Instruct-GGUF`
- Arquivo: `Llama-3.2-3B-Instruct-Q4_K_M.gguf`

Após colocar o arquivo na pasta, abra Configurações → "IA inteligente
(LLM local)" → clique **Atualizar lista** se necessário → escolha o
modelo no ComboBox → ligue o interruptor. O template chat é detectado
automaticamente pelo nome (palavras-chave: gemma, qwen, phi, mistral,
llama).

### Por que não Llama 4?

Llama 4 (Scout/Maverick, 2025) é arquitetura MoE com 109B parâmetros.
Mesmo Q4 ocupa ~60 GB e exige GPU. Inviável pro perfil "qualquer
computador". Para extração estruturada pequena, Gemma 3 4B supera.

### Por que não Gemma 4?

Não existe ainda. Gemma 3 é a versão mais recente do Google (lançada
em março/2025).

## Quanto tempo demora

| CPU | Páginas/min | Notas |
|---|---|---|
| i7-13700 / Ryzen 7 7700X | ~6-10 | AVX-512, 16 threads |
| i5-12400 / Ryzen 5 5600 | ~3-5 | AVX2 |
| i3 / Ryzen 3 antigo | ~1-2 | Sem AVX2 |

Pode parecer lento, mas é **opcional**. O NER rápido cobre 80%+ dos
casos; o LLM entra como rede de segurança em documentos complexos.

## Limites conhecidos

- **RAM**: modelos de 3-4B Q4 ocupam ~3 GB em uso. Verifique antes de
  carregar em máquinas com 4 GB de RAM total.
- **Quality vs. quantization**: Q4_K_M é o sweet spot. Q3 economiza
  ~25% RAM mas qualidade cai notavelmente. Q5/Q6 ganham qualidade
  marginal mas dobra de tamanho.
- **Alucinação**: o validador `text.IndexOf` mata 95% das alucinações,
  mas o LLM pode ainda emitir items que existem no texto mas não são
  sensíveis (ex: marca a palavra "consoante" como nome). Aparecem na
  UI em laranja (não-aprovado por baixa confiança), revisão humana
  decide.
- **Determinismo**: temperatura 0 + mesma seed produz mesma saída.
  Modelos diferentes produzem extrações diferentes — comparação justa
  exige fixar a versão do GGUF.
