# Sigilus.Detection.Onnx

NER (Named Entity Recognition) PT-BR via modelo ONNX. Detecta nomes de
pessoas e locais que regex não cobre. **Projeto opt-in** — o runtime
ONNX adiciona ~200 MB ao publish; só referencie de CLI/UI quando o
recurso for ativado.

## Para que serve

Regex pega CPF/CNPJ/CNJ/email/telefone com precisão cirúrgica. Mas em
um processo judicial, o **maior risco LGPD são os nomes** ("João da
Silva"), endereços ("Rua Almirante Tamandaré 233") e razões sociais —
texto livre que não tem padrão estrito. NER baseado em transformer
resolve isso.

O design separa três coisas:

- **`INerProvider`** (em `Sigilus.Core.Abstractions`): contrato puro —
  texto entra, lista de `NerSpan` sai.
- **`OnnxNerProvider`** (aqui): implementa o provider rodando um modelo
  ONNX no `Microsoft.ML.OnnxRuntime`.
- **`NerEntityDetector`** (aqui): implementa `IEntityDetector` chamando
  o provider e convertendo os spans em `DetectedEntity` via `CharCoordIndex`.

Assim você pode trocar o provider (HuggingFace API, gRPC para um
serviço Python, etc.) sem mexer no detector.

## Dependências NuGet

| Pacote | Versão | Por quê |
|---|---|---|
| `Microsoft.ML.OnnxRuntime` | 1.20.1 | Runtime ONNX CPU. Para GPU, troque por `Microsoft.ML.OnnxRuntime.DirectML` (Windows). |

E referência a projeto `Sigilus.Detection` para usar `CharCoordIndex`.

## Layout

```
Sigilus.Detection.Onnx/
├── NerEntityDetector.cs   ← consome INerProvider + mapeia spans para retângulos
├── NerModelResolver.cs    ← localiza ner-ptbr.onnx + vocab.txt + labels.json
├── OnnxNerProvider.cs     ← roda inference + decodifica BIO
└── WordPieceTokenizer.cs  ← tokenizer BERT minimalista (vocab.txt + WordPiece)
```

### [`NerModelResolver`](NerModelResolver.cs)

Análogo ao `TessdataResolver`: procura `./models/ner-ptbr.onnx` +
`vocab.txt` + `labels.json` em `SIGILUS_NER_MODELS` → `AppContext.BaseDirectory/models` →
até 5 níveis acima. Retorna `NerModelPaths` ou `null`. **Nunca lança** —
falha silenciosa permite que UI/CLI tratem como "NER off" sem crash em
máquinas que não baixaram o modelo.

### [`WordPieceTokenizer`](WordPieceTokenizer.cs)

Tokenizer BERT WordPiece sem libs externas. Carrega `vocab.txt`
(1 token por linha, índice = id). Output:

```csharp
public sealed record Encoding(int[] Ids, int[] Mask, (int CharStart, int CharLen)[] Offsets);
```

**`Offsets[t]`** é a propriedade crítica: para cada token, diz qual
intervalo `[CharStart, CharStart+CharLen)` do texto **original**
representa. Permite ir de tokens → caracteres → retângulos.

Algoritmo:

1. **Basic tokenize**: separa por whitespace + pontuação. Mantém
   pontuação como token próprio.
2. **WordPiece**: para cada word, tenta achar o maior prefix em
   vocab; se achou, próximo prefix vira `##suffix`; se nada casar,
   o word inteiro vira `[UNK]`.
3. Adiciona `[CLS]` no início, `[SEP]` no fim, pad com `[PAD]` até
   `maxLen` (default 512).
4. `Mask` é 1 para token real, 0 para pad.

Lowercase opcional (padrão `true` — BERT base uncased).

### [`OnnxNerProvider`](OnnxNerProvider.cs)

Implementa `INerProvider`. Construtor:

```csharp
new OnnxNerProvider(
    modelPath: "ner-ptbr-int8.onnx",
    vocabPath: "vocab.txt",
    labels: new[] { "O", "B-PER", "I-PER", "B-LOC", "I-LOC", "B-ORG", "I-ORG" });
```

Inference:

1. Tokeniza o texto.
2. Cria três tensores `[1, seq]` long: `input_ids`, `attention_mask`,
   `token_type_ids` (zeros).
3. Roda `_session.Run(...)`. Espera output `[1, seq, numLabels]` de logits.
4. **Decodifica BIO**: para cada token, pega o label de max logit. Se
   for `B-X`, abre novo span; se `I-X` continuando o mesmo tipo,
   estende; senão fecha o atual. Score do span é média sigmoid dos
   logits.
5. Maps labels para `EntityType` via `MapLabel`:
   - `PER`/`PESSOA` → `PersonName`
   - `LOC`/`LOCAL` → `Address`
   - `ORG` → `Other`

### [`NerEntityDetector`](NerEntityDetector.cs)

Implementa `IEntityDetector`. Chama o provider e converte:

```csharp
var spans = await _ner.InferAsync(page.ConcatenatedText, ct);
var index = new CharCoordIndex(page);
foreach (var span in spans)
{
    foreach (var rect in index.RectsFor(span.CharStart, span.CharLength))
        yield return new DetectedEntity(span.Type, text, span.Score, rect, ...);
}
```

## Como obter um modelo

O Sigilus já vem com **LeNER-Br** (pierreguillou/ner-bert-base-cased-pt-lenerbr)
quantizado INT8, ~104 MB, em `S:\work\sigilus2\models\`:

- `ner-ptbr.onnx` (modelo)
- `vocab.txt` (vocab BERT cased)
- `labels.json` (13 labels BIO: PESSOA/LOCAL/ORGANIZACAO/TEMPO/LEGISLACAO/JURISPRUDENCIA)
- `tokenizer_config.json` (config — usado pra detectar `do_lower_case`)

Para regenerar ou trocar por outro modelo HuggingFace:

```python
# requirements: optimum[onnxruntime] transformers
from optimum.onnxruntime import ORTModelForTokenClassification, ORTQuantizer
from optimum.onnxruntime.configuration import AutoQuantizationConfig
from transformers import AutoTokenizer, AutoConfig
import shutil, json
from pathlib import Path

MODEL_ID = "pierreguillou/ner-bert-base-cased-pt-lenerbr"
OUT = Path("S:/work/sigilus2/models")

# Export FP32 ONNX
m = ORTModelForTokenClassification.from_pretrained(MODEL_ID, export=True)
tok = AutoTokenizer.from_pretrained(MODEL_ID)
cfg = AutoConfig.from_pretrained(MODEL_ID)
m.save_pretrained(str(OUT / "_fp32"))
tok.save_pretrained(str(OUT / "_fp32"))

# Quantize INT8 (avx2 = compatível com qualquer CPU x64 moderno)
q = ORTQuantizer.from_pretrained(str(OUT / "_fp32"))
q.quantize(save_dir=str(OUT / "_int8"),
           quantization_config=AutoQuantizationConfig.avx2(is_static=False, per_channel=False))

# Copia artefatos
shutil.copy(sorted((OUT / "_int8").glob("*.onnx"), key=lambda p: p.stat().st_size)[-1],
            OUT / "ner-ptbr.onnx")
shutil.copy(OUT / "_fp32" / "vocab.txt", OUT / "vocab.txt")
shutil.copy(OUT / "_fp32" / "tokenizer_config.json", OUT / "tokenizer_config.json")
(OUT / "labels.json").write_text(
    json.dumps([cfg.id2label[i] for i in sorted(cfg.id2label)], ensure_ascii=False, indent=2),
    encoding="utf-8")
shutil.rmtree(OUT / "_fp32"); shutil.rmtree(OUT / "_int8")
```

Tamanhos típicos: FP32 ~440 MB, INT8 ~104 MB. Latência CPU AVX2
~150-250 ms/página.

### `do_lower_case` (suporte a modelos cased)

Modelos cased (LeNER-Br, BERTimbau cased) precisam de
`tokenizer_config.json` com `"do_lower_case": false`. O
`NerModelResolver` lê esse arquivo automaticamente; sem ele, assume
`true` (uncased). Errar isso destrói as previsões — tokens viram UNK e
o modelo emite spans aleatórios.

## Como adicionar uma nova label

1. Adicione `EntityType.SuaCoisa` em `Sigilus.Core/Domain/Enums.cs`.
2. Adicione case em `OnnxNerProvider.MapLabel`.
3. Re-treine o modelo com a tag BIO correspondente.

## Convenções

- **Não embute o modelo no instalador.** Baixe lazy no primeiro uso.
- **Dispose** do `OnnxNerProvider` (libera `InferenceSession`).
- **`IntraOpNumThreads = Environment.ProcessorCount / 2`** evita
  thrashing quando a UI/CLI também usa o pool.
