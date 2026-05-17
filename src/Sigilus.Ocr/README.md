# Sigilus.Ocr

Wrapper Tesseract 5.x para OCR de páginas escaneadas. Implementa
`IOcrEngine` de `Sigilus.Core`.

## Para que serve

PDFs jurídicos brasileiros frequentemente misturam páginas nativas
(petições) com páginas escaneadas (anexos digitalizados). Sem OCR, todo
o conteúdo escaneado é invisível ao pipeline. Este projeto preenche a
lacuna.

## Dependências NuGet

| Pacote | Versão | Por quê |
|---|---|---|
| `Tesseract` | 5.2.0 | Wrapper .NET do binário libtesseract. Vem com nativo Win64. |
| `SkiaSharp` | 2.88.8 | Compartilhado com `Sigilus.Pdf`. Aqui não é usado diretamente, mas é necessário transitivamente. |

## Layout

```
Sigilus.Ocr/
├── TessdataResolver.cs       ← localiza por.traineddata no filesystem
├── TesseractEnginePool.cs    ← pool N-worker para paralelismo real
└── TesseractOcrEngine.cs     ← implementa IOcrEngine (single instance, lock interno)
```

### [`TessdataResolver.FindTessdata()`](TessdataResolver.cs)

Busca em ordem:

1. Variável de ambiente `TESSDATA_PREFIX` (padrão Tesseract).
2. `./tessdata` ao lado do executável (`AppContext.BaseDirectory`).
3. Subindo até 5 níveis a partir do executável — útil em dev (o
   executável fica em `bin/Debug/net8.0` mas a pasta `tessdata` está
   na raiz do repo).

Retorna `null` se não achar. CLI e UI tratam o `null` desativando OCR
silenciosamente e avisando no status.

**Convenção**: a pasta deve conter no mínimo `por.traineddata`.
Adicione `eng.traineddata` se quiser inglês — o engine aceita string
de idioma combinada via construtor.

### [`TesseractOcrEngine`](TesseractOcrEngine.cs)

Implementa `IOcrEngine`. **Single-instance**, thread-safe via `lock`.

```csharp
using var ocr = new TesseractOcrEngine(
    tessdataPath: "S:/work/sigilus2/tessdata",
    language: "por");

var png = renderer.RenderPng(pdfStream, pageIndex: 5, dpi: 300);
var pageSize = page.GetPageSizeWithRotation();
IReadOnlyList<TextRun> runs = ocr.Recognize(
    png, pageIndex: 5,
    pageWidthPts: pageSize.GetWidth(),
    pageHeightPts: pageSize.GetHeight(),
    ct: token);
```

**O que faz:**

1. Carrega o PNG via `Pix.LoadFromMemory`.
2. Roda o engine em `PageSegMode.Auto` (faz tudo: detectar orientação,
   linhas, palavras).
3. Itera no nível de **palavra** (`PageIteratorLevel.Word`) — granularidade
   suficiente para redação. Char-level (`Symbol`) seria 5–10× mais
   lento e impreciso.
4. Para cada palavra:
   - Lê `Rect` em **pixel-space top-left**.
   - Converte para pontos bottom-left:
     ```
     y_pts = pageHeight - rect.Y2 * (pageHeight / bmpHeight)
     ```
   - Sintetiza `CharBounds` subdividindo a caixa linearmente pelo
     número de caracteres (`SubdivideForChars`). Não é perfeito —
     glifos largos vs estreitos têm a mesma fatia — mas a redação
     de texto inteiro funciona bem porque o regex casa a palavra
     inteira e o `CharCoordIndex` faz a união.
   - Pega `Confidence` do iterator (0–100 → divide por 100).
   - Emite `TextRun { Source = Ner, Confidence = ... }`.

### Engine mode

Usa `EngineMode.LstmOnly` — modo neural moderno, melhor para texto em
português jurídico. Modo legado fica disponível trocando o argumento
no construtor.

## Como adicionar

### Suporte multi-idioma

```csharp
new TesseractOcrEngine(tessdataPath, language: "por+eng+spa")
```

Garanta os `*.traineddata` correspondentes em `tessdata/`.

### Pré-processamento (deskew, denoise)

Faça em `SkiaSharp` antes de passar para `Pix.LoadFromMemory`. Não
mexa no `TesseractOcrEngine` — crie um decorator que recebe
`ReadOnlyMemory<byte> png` e devolve PNG processado.

### Paralelismo

Cada `TesseractOcrEngine` faz `lock` interno (uma página por vez por
engine). Para paralelismo real, use o **[`TesseractEnginePool`](TesseractEnginePool.cs)**:
ele implementa `IOcrEngine` mas mantém até N engines internamente,
alocando sob demanda e reusando via `ConcurrentQueue`. A UI já faz isso
no detect paralelo:

```csharp
using var pool = new TesseractEnginePool(tessdataPath, maxConcurrency: 8);
// agora `pool` pode ser injetado em N extractors rodando em paralelo.
```

`SemaphoreSlim` interno garante que nunca exceda o limite mesmo sob
contenção. `Dispose` libera todos os engines criados.

## Convenções

- **Single instance, lock global**: Tesseract aloca caches internos
  grandes; criar/destruir engine por página é proibitivo.
- **Confiança normalizada para 0–1**: Tesseract devolve 0–100 mas o
  resto do pipeline trabalha em 0–1 (mesma escala do regex).
- **Char bounds sintetizados são "bons o suficiente"**: para precisão
  por glifo, trocar `PageIteratorLevel.Word` por `Symbol` em uma
  variante do método. Mantenha a interface `IOcrEngine` igual.
