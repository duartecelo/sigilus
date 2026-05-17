# Sigilus.Pdf

Toda manipulação de PDF do Sigilus 2.0: extração, classificação,
renderização e **redação destrutiva** (texto, vetores e imagens).
Wrappeia iText 9 + SkiaSharp + PDFium.

## Para que serve

Implementa as interfaces `ITextExtractor`, `IPageClassifier`,
`IRedactionEngine` de `Sigilus.Core`, e expõe interfaces próprias
(`IImageRedactor`, `IMetadataScrubber`, `IPdfPageRenderer`) que
quem precisar de iText pode consumir — mas que o `Sigilus.Core`
nunca conhece.

## Dependências NuGet

| Pacote | Versão | Por quê |
|---|---|---|
| `itext` | 9.6.0 | Núcleo iText 8/9. `PdfReader`, `PdfDocument`, `LocationTextExtractionStrategy`. |
| `itext.bouncy-castle-adapter` | 9.6.0 | Provider de cripto requerido pelo iText em runtime. Sem ele, abrir PDF com assinatura falha. |
| `itext.pdfsweep` | 5.0.6 | `PdfCleaner.CleanUp` — apaga texto + vetores destrutivamente nos retângulos. |
| `SkiaSharp` | 2.88.8 | Manipulação de pixels para `SkiaImageRedactor`. |
| `PDFtoImage` | 4.1.1 | Bind PDFium → `SKBitmap`. Suporta Windows/Linux/macOS/Android 31+. |

### `NU1701` suprimido no csproj

`itext.pdfsweep` ainda é publicado como `net461` (não tem build `net8.0`).
Funciona em .NET 8 via NetFx compatibility shim. O warning é silenciado
em `Sigilus.Pdf.csproj` via `<NoWarn>$(NoWarn);NU1701</NoWarn>`.

## Layout

```
Sigilus.Pdf/
├── Abstractions/      ← interfaces que usam tipos iText (não cabem em Core)
├── Extraction/        ← classifier + char-coord strategy + extractor híbrido
├── Redaction/         ← engine pdfSweep + image redactor + metadata scrubber
└── Rendering/         ← PDFium renderer
```

### `Abstractions/`

- [`IImageRedactor`](Abstractions/IImageRedactor.cs) — redação em bitmaps
  dentro do `PdfDocument`. Implementações: `NoopImageRedactor` (placeholder),
  `SkiaImageRedactor` (real).
- [`IMetadataScrubber`](Abstractions/IMetadataScrubber.cs) — limpa Info dict,
  XMP, structure tree conforme `MetadataPolicy`.
- [`IPdfPageRenderer`](Abstractions/IPdfPageRenderer.cs) — rasteriza uma
  página para PNG (`ReadOnlyMemory<byte>`). Usado por OCR e pela UI.

### `Extraction/`

#### [`CharCoordExtractionStrategy`](Extraction/CharCoordExtractionStrategy.cs)

Coletor `IEventListener` (não herda `LocationTextExtractionStrategy`!)
que captura cada `TextRenderInfo` + seus `CharacterRenderInfo`s. Na
materialização (lazy, em `Text`/`CharRects`):

1. Agrupa chunks por `baselineY` arredondada.
2. Ordena top-down (Y decrescente em PDF user-space).
3. Em cada linha, ordena left-to-right.
4. **Insere espaço entre chunks vizinhos se o gap horizontal > metade
   do `CharSpaceWidth`** — essa é a heurística que evita
   `"mpcivelnh@mprs.mp.brDocumento"` em um único token.
5. Insere `\n` entre linhas.

`CharRects` é alinhado 1:1 com `Text` (inclusive os espaços inseridos e
quebras de linha, que recebem `default(PdfRect)` = vazio). Quem consome
deve checar `IsEmpty`.

**Por que não herdar `LocationTextExtractionStrategy`?** Porque ela não
expõe os char rects sincronizados com o texto resultante. A primeira
versão herdou e dava texto colado (palavras grudadas), pois a herança
só sobrescrevia `EventOccurred` sem reagrupar.

#### [`HeuristicPageClassifier`](Extraction/HeuristicPageClassifier.cs)

Classifica uma página em `NativeText | Scanned | Hybrid | Empty`:

- Roda a `CharCoordExtractionStrategy` e conta chars não-vazios (`>= 40`
  → tem texto).
- Roda um `IEventListener` que soma área de XObjects de imagem via
  determinante da CTM (`|a*d - b*c|`). Se a cobertura ≥ 40% da página →
  tem imagem grande.
- Cruza: texto+imagem = Hybrid; só texto = NativeText; só imagem grande
  = Scanned; nada = Empty.

#### [`HybridExtractor`](Extraction/HybridExtractor.cs)

Implementa `ITextExtractor`. Recebe no construtor o classifier e,
opcionalmente, um `IOcrEngine` + `IPdfPageRenderer`. Sem OCR, páginas
`Scanned` produzem `PageContext` com 0 runs e `ConcatenatedText` vazio
— o resto do pipeline silenciosamente as ignora.

```csharp
var classifier = new HeuristicPageClassifier();
var ocr = new TesseractOcrEngine(TessdataResolver.FindTessdata()!);
var renderer = new PdfiumPageRenderer();
var extractor = new HybridExtractor(classifier, ocr, renderer);
var ctx = extractor.Extract(pdfStream, pageIndex: 0, ct);
```

Para páginas `Hybrid` roda **os dois** caminhos e concatena os runs;
detectores e a UI tratam os runs como uniformes.

### `Rendering/PdfiumPageRenderer.cs`

Wrapper `PDFtoImage.Conversion.ToImage(...)`. Marca o método com
`[SupportedOSPlatform("windows"/"linux"/"macos"/"android31.0")]` para
informar o analyzer CA1416.

```csharp
var renderer = new PdfiumPageRenderer();
ReadOnlyMemory<byte> png = renderer.RenderPng(pdfStream, pageIndex: 3, dpi: 300);
```

DPI 150 é bom para UI; 300 para OCR.

### `Redaction/`

#### [`PseudonymizationRedactionEngine`](Redaction/PseudonymizationRedactionEngine.cs)

Engine que **substitui** o valor por um pseudônimo determinístico em vez
de cobrir com tarja preta. Sequência por execução:

1. `PdfCleaner.CleanUp` apaga o texto/vetor original (destrutivo) e
   pinta o rect de **BRANCO** (não preto — vamos escrever em cima).
2. Para cada decisão aprovada: pega `Origin.MatchedText`, chama
   `IPseudonymizer.Substitute(...)`, e desenha o texto com
   `PdfCanvas.ShowText` no mesmo rect (font size auto-fit por altura +
   redução se largura estourar).
3. `SkiaPseudonymizingImageRedactor` faz o equivalente para XObjects
   raster (pinta branco + desenha texto preto via SkiaSharp).
4. `IMetadataScrubber.Scrub`.

**Diferença em relação ao `PdfSweepRedactionEngine`**: o texto original
ainda é destruído com a mesma força (impossível recuperar — passa nos
testes E2E de re-extração com duas estratégias). O que muda é o que
aparece no lugar: um pseudônimo legível em vez de uma tarja preta.

#### [`SkiaPseudonymizingImageRedactor`](Redaction/SkiaPseudonymizingImageRedactor.cs)

Variante do `SkiaImageRedactor` para o caminho de pseudonimização.
Mesmo algoritmo de manipulação de XObject (clone-on-write, CTM⁻¹,
re-encode PNG), mas pinta retângulo branco em vez de preto e desenha
o texto pseudônimo via `SKCanvas.DrawText`.

#### [`PdfSweepRedactionEngine`](Redaction/PdfSweepRedactionEngine.cs)

Implementação principal de `IRedactionEngine`. Sequência por execução:

1. Abre o `PdfDocument` em modo read+write.
2. Mapeia cada `RedactionDecision` aprovada em um `PdfCleanUpLocation`
   (page#1-based, `Rectangle` em pts, cor preta).
3. Chama `PdfCleaner.CleanUp(doc, locations)` — **destrutivo**, apaga
   texto e vetores do content stream e desenha um retângulo preto.
4. Chama `_imageRedactor.RedactImagesAsync(doc, decisions, ct)` no
   mesmo `PdfDocument` (pdfSweep não toca em pixels de raster).
5. Chama `_scrubber.Scrub(doc, metadataPolicy)`.
6. `doc.Close()` via `using`, flush do `PdfWriter`.

#### [`SkiaImageRedactor`](Redaction/SkiaImageRedactor.cs)

Implementa `IImageRedactor`. Para cada página com decisões:

1. **Conta referências globais** de cada XObject de imagem (`CountXObjectReferences`).
2. Para cada placement (xobj, CTM) encontrado via `PdfCanvasProcessor`
   com listener `RENDER_IMAGE`:
   - Se `globalRefCount > 1` ou já foi tocado nesta página, **clona o
     stream** com `(PdfStream)src.Clone().MakeIndirect(doc)` — clone-on-write.
   - Inverte a CTM (própria: ver `TryInvert`).
   - Decodifica o bitmap com `SKBitmap.Decode`.
   - Para cada decisão dessa página, projeta o retângulo de user-space →
     unit square via CTM⁻¹ → pixel-space (top-left, com flip vertical
     `1 - v`). Pinta `SKColors.Black` opaco.
   - Re-codifica como PNG, substitui o stream e ajusta os keys
     `/Filter /Width /Height /BitsPerComponent /ColorSpace`. Remove
     `/DecodeParms` e `/SMask` para evitar inconsistências.

#### [`NoopImageRedactor`](Redaction/NoopImageRedactor.cs)

Placeholder no-op. Usado em testes que isolam o caminho de texto.

#### [`ItextMetadataScrubber`](Redaction/ItextMetadataScrubber.cs)

Implementa `IMetadataScrubber`:

- `ClearInfoDict`: zera Author/Title/Subject/Keywords/Creator (e Producer
  se `ClearProducer`).
- `ClearXmp`: remove a entrada `/Metadata` do catálogo (iText não
  regrava XMP se nada chamar `SetXmpMetadata` depois).
- `StripStructureTree`: remove `/StructTreeRoot` e `/MarkInfo` se o doc
  for tagged.

## Como adicionar

### Um novo formato de detecção de imagem (ex: blur em vez de retângulo)

Crie outra implementação de `IImageRedactor`. O `SkiaImageRedactor`
serve como referência: o `using var paint = new SKPaint { ... }` é o
ponto onde trocar `Fill` por `BlurMaskFilter`.

### Um novo classifier (ex: ML)

Implemente `IPageClassifier` e injete em `HybridExtractor`. Mantenha a
semântica de 4 estados para não quebrar o classifier-aware do extractor.

## Convenções

- **Coordenadas canônicas em pontos, bottom-left.** Conversões ocorrem
  só em `SkiaImageRedactor.MapRectToImagePixels` e em `TesseractOcrEngine`.
- **`SetUnethicalReading(true)`** em todo abrir — Sigilus precisa ler
  PDFs com permissões restritivas pois é o usuário operando sobre seus
  próprios arquivos.
- **PdfWriter com `FullCompressionMode`** — saída compacta.
- **Imagens compartilhadas** são SEMPRE clonadas antes de mutar (clone-on-write).
