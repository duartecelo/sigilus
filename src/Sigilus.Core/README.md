# Sigilus.Core

Núcleo de domínio, interfaces e orquestração do Sigilus 2.0. **Não depende
de bibliotecas de terceiros** (sem iText, SkiaSharp, Tesseract, ONNX).
Tudo cruza a fronteira via tipos primitivos e `Stream`/`ReadOnlyMemory<byte>`.

## Para que serve

É a peça que define **o que** o Sigilus faz, sem dizer **como**. Os outros
projetos (`Sigilus.Pdf`, `Sigilus.Ocr`, `Sigilus.Detection`,
`Sigilus.Detection.Onnx`) implementam as interfaces daqui; CLI e UI montam
um grafo concreto e chamam `RedactionPipeline.RunAsync`.

Se uma classe não tiver razão clara para mexer em PDF/imagem/regex, ela
mora aqui.

## Layout

```
Sigilus.Core/
├── Abstractions/        ← interfaces estáveis (contratos públicos)
├── Audit/               ← log de auditoria (SHA-256 + decisões)
├── Domain/              ← tipos imutáveis: rects, runs, decisões, contexto
├── Pipeline/            ← orquestrador que junta tudo
└── Pseudonymization/    ← IPseudonymizer + tabelas determinísticas + gender
```

### `Domain/`

Records e enums imutáveis. **Todas as coordenadas neste namespace estão em
PDF user-space** (origem inferior-esquerda, unidade em pontos = 1/72").

| Tipo | Função |
|---|---|
| [`PdfRect`](Domain/PdfRect.cs) | `readonly record struct (X, Y, Width, Height)`. Tem `Right`, `Top`, `IsEmpty`, `Union(a,b)`. Esta é a única representação de retângulo que atravessa fronteira pública. |
| [`Enums`](Domain/Enums.cs) | `EntityType` (Cpf, Cnpj, Rg, Oab, Email, Phone, PersonName, Address, BankAccount, ProcessoCnj, Other), `DetectionSource` (Regex, Ner, Manual), `PageClassification` (NativeText, Scanned, Hybrid, Empty). |
| [`TextRun`](Domain/TextRun.cs) | Pedaço de texto + retângulo + lista de char rects (mesmo length de `Text`). Tem `Source` e `Confidence` (1.0 para nativo, <1 para OCR). |
| [`PageElement`](Domain/PageElement.cs) | Hierarquia `abstract record`: `TextPageElement(Run)` ou `ImagePageElement(XObjectRef)`. Usado quando alguém precisa enumerar conteúdo de uma página de forma uniforme. |
| [`DetectedEntity`](Domain/DetectedEntity.cs) | Match de detector: tipo, texto, confiança, bounds, página, fonte, offsets `CharStart`/`CharLength` no texto concatenado da página. |
| [`RedactionDecision`](Domain/RedactionDecision.cs) | Decisão final (humana ou automática): bounds + `Approved` + `Reason` + `Origin?` (a `DetectedEntity` que originou — `null` se manual). |
| [`PageContext`](Domain/PageContext.cs) | Saída de `ITextExtractor`: classificação, texto concatenado, runs, elements, tamanho em pts, rotação. |
| [`NerSpan`](Domain/NerSpan.cs) | Saída crua do NER: tipo + offsets char + score. Convertido em `DetectedEntity` pelo `NerEntityDetector`. |
| [`MetadataPolicy`](Domain/MetadataPolicy.cs) | Política de scrub: flags `ClearInfoDict`, `ClearXmp`, `ClearProducer`, `StripStructureTree`. `MetadataPolicy.Default` zera tudo exceto structure tree. |

**Exemplo** — criando uma decisão manual:

```csharp
var manual = new RedactionDecision(
    Bounds: new PdfRect(72, 720, 100, 12),
    PageIndex: 0,
    Approved: true,
    Reason: "manual draw",
    Origin: null);
```

### `Abstractions/`

Cinco contratos consumidos pelo pipeline. Todos usam `Stream` ou tipos
primitivos para não vazar dependências:

| Interface | Implementador concreto | O que retorna |
|---|---|---|
| `IPageClassifier` | `Sigilus.Pdf.Extraction.HeuristicPageClassifier` | `PageClassification` para uma página. |
| `ITextExtractor` | `Sigilus.Pdf.Extraction.HybridExtractor` | `PageContext` completo. |
| `IOcrEngine` | `Sigilus.Ocr.TesseractOcrEngine` | Lista de `TextRun` a partir de PNG (`ReadOnlyMemory<byte>`). |
| `IEntityDetector` | `Sigilus.Detection.RegexEntityDetector`, `Sigilus.Detection.CompositeEntityDetector`, `Sigilus.Detection.Onnx.NerEntityDetector` | `IAsyncEnumerable<DetectedEntity>`. |
| `INerProvider` | `Sigilus.Detection.Onnx.OnnxNerProvider` | `IReadOnlyList<NerSpan>` para um texto. Interface interna do `NerEntityDetector`. |
| `IRedactionEngine` | `Sigilus.Pdf.Redaction.PdfSweepRedactionEngine` | Escreve o PDF redigido em `Stream output`. |

### `Pipeline/RedactionPipeline.cs`

Orquestra extração → detecção → revisão → redação. Recebe os 3 serviços
+ `pageCount` no construtor; `RunAsync` aceita um `ReviewFn` delegate que
**é o ponto de entrada da UI**. No CLI esse delegate é uma auto-aprovação
por threshold; na UI WPF é uma `TaskCompletionSource` que espera o clique
do usuário.

```csharp
var pipeline = new RedactionPipeline(extractor, detector, engine, pageCount);
var decisions = await pipeline.RunAsync(
    inputStream, outputStream,
    review: (pageIdx, ctx, entities) => entities
        .Where(e => e.Confidence >= 0.85f)
        .Select(e => new RedactionDecision(e.Bounds, e.PageIndex, true, "auto", e))
        .ToList(),
    ct: token);
```

O pipeline percorre **todas as páginas em ordem**, acumula decisões e só
no fim chama `engine.RedactAsync` uma única vez — porque pdfSweep é mais
eficiente quando recebe todas as locations de uma vez.

### `Pseudonymization/`

Contratos e implementação default de pseudonimização **determinística por
documento** — mesmo input no mesmo `PseudonymContext` sempre devolve o
mesmo output. Usado pelo `PseudonymizationRedactionEngine` quando o
usuário escolhe "substituir" em vez de "tarjar".

| Tipo | Função |
|---|---|
| [`IPseudonymizer`](Pseudonymization/IPseudonymizer.cs) | Interface 1-método: `Substitute(EntityType, original, ctx)`. Implementações são puras; estado mora no `ctx`. |
| [`PseudonymContext`](Pseudonymization/PseudonymContext.cs) | Cache thread-safe `(EntityType, normalizedKey) → substituto` + contador por tipo. Chama `factory(idx)` com índice 1-based só na primeira ocorrência. |
| [`GenderInference`](Pseudonymization/GenderInference.cs) | `Gender.Infer(fullName)` — sufixo `-a/-e` → feminino, resto → masculino, com listas curtas de exceções (Beatriz, Inês, Luca…). |
| [`BrazilianPseudonymizer`](Pseudonymization/BrazilianPseudonymizer.cs) | Implementação padrão. Nomes viram `Fulano`/`Beltrano`/`Ciclano`… (`Fulana`/`Beltrana`… se fem); CPF/CNPJ ganham checksum válido falso; e-mail vira `pessoa001@redacted.local`; telefone `(11) 9NNNN-NNNN`; resto vira `[[TIPO_NNN]]`. |

**Exemplo**:

```csharp
var pseudo = new BrazilianPseudonymizer();
var ctx = new PseudonymContext();
pseudo.Substitute(EntityType.PersonName, "Manoel Luiz Prates", ctx);  // "Fulano"
pseudo.Substitute(EntityType.PersonName, "Manoel Luiz Prates", ctx);  // "Fulano" (consistente)
pseudo.Substitute(EntityType.PersonName, "Maria Silva", ctx);         // "Fulana"
pseudo.Substitute(EntityType.PersonName, "João Pereira", ctx);        // "Beltrano"
pseudo.Substitute(EntityType.Cpf, "390.533.447-05", ctx);             // ex: "001.000.030-29" (DV válido)
```

A consistência garante: o leitor do PDF redigido consegue acompanhar
"Fulano" no documento inteiro como um personagem coerente, mesmo sem
saber quem é o original.

### `Audit/AuditLog.cs`

Sidecar JSON para conformidade jurídica.

```csharp
var log = AuditWriter.Build(inputStream, pageCount, decisions);
AuditWriter.WriteTo(log, "redigido.audit.json");
```

Conteúdo:

- `InputSha256`: hash da entrada (provando que o audit é desse arquivo).
- `Timestamp` UTC.
- `TotalPages`, `TotalDecisions`, `ApprovedDecisions`.
- `Entries[]`: cada decisão com tipo/texto/score/source/bounds/approved/reason.

Reproduz-se com `JsonSerializer` + `JsonStringEnumConverter` — sem libs
externas.

## Convenções

- **Tudo é imutável**: `record`/`readonly record struct`. Modifica-se via
  `with`. Pipelines não devem mutar `PageContext`/`DetectedEntity` —
  geram listas novas.
- **Nullable enable** em todo o assembly. `?` é parte do contrato, não
  documentação informal.
- **Coordenadas em PDF user-space** (origem bottom-left, pontos). Quem
  cruza fronteira para imagem/UI faz a conversão **no implementador**,
  não aqui (ver `SkiaImageRedactor.MapRectToImagePixels` e
  `RedactionOverlay.ToDips`).

## Como adicionar uma nova interface

1. Crie a interface em `Abstractions/`, com tipos só de `Core`.
2. Implemente em um projeto downstream (`Sigilus.Pdf` etc.).
3. Refatore `RedactionPipeline` (ou crie um novo) se for parte do fluxo.

## Por que não usar DI container aqui

`RedactionPipeline` toma o grafo via construtor explícito. CLI e UI
fazem `new` manual. Adicionar `Microsoft.Extensions.DependencyInjection`
no Core forçaria toda subida a se preocupar com lifetime — não há ganho.
