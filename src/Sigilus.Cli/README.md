# Sigilus.Cli

Executável de linha de comando para uso em batch/headless. Monta o
grafo de serviços padrão, auto-aprova detecções acima de um threshold
e escreve o PDF redigido + opcional sidecar de auditoria.

## Para que serve

- Pipelines automatizados (ex: scanner de docs antes de upload pra
  sistema externo).
- Smoke test rápido durante desenvolvimento — você não precisa abrir a
  UI WPF para validar uma mudança no engine.
- Servidor batch: rodar em todos os PDFs de uma pasta via shell loop.

## Dependências

| Pacote | Versão | Por quê |
|---|---|---|
| `System.CommandLine` | 2.0.0-beta4 | Parser de opções com `--help` automático. |

E referências a `Sigilus.Core`, `Sigilus.Pdf`, `Sigilus.Detection`,
`Sigilus.Ocr`.

`<NoWarn>CA1416</NoWarn>` no csproj — `PdfiumPageRenderer` é
multi-plataforma anotado; o aviso de "compatibility" não se aplica.

## Uso

```bash
dotnet run --project src/Sigilus.Cli -- \
    --input pdfs/processo.pdf \
    --output redigido.pdf \
    --audit redigido.audit.json \
    --min-confidence 0.85
```

Opções:

- `--input | -i` (obrigatório) — PDF de entrada.
- `--output | -o` (obrigatório) — PDF de saída (sobrescreve).
- `--audit | -a` — sidecar JSON com SHA-256 da entrada + decisões.
- `--min-confidence` — float 0..1, default `0.85`. Detecções abaixo
  ficam no log mas não são aplicadas.

Saída típica:

```
[sigilus] entrada: S:\work\pdfs\processo.pdf
[sigilus] 100 página(s)
[sigilus] OCR ativo (tessdata=S:\work\sigilus2\tessdata)
[sigilus] página 1: 0 detecções, 0 aprovadas
[sigilus] página 2: 3 detecções, 1 aprovadas
...
[sigilus] saída: S:\work\redigido.pdf
[sigilus] audit: S:\work\redigido.audit.json
```

Se não houver `tessdata/`, OCR é desativado e o CLI avisa:

```
[sigilus] OCR desativado (nenhum tessdata encontrado; páginas escaneadas serão ignoradas)
```

## Layout

```
Sigilus.Cli/
└── Program.cs    ← monta grafo + auto-aprova + roda RedactionPipeline
```

## O que `Program.cs` faz

1. Parse de opções com `System.CommandLine`.
2. Lê o PDF inteiro pra memória **uma vez** (`File.ReadAllBytesAsync`)
   — o pipeline precisa fazer extract page-a-page e depois passar a
   entrada de novo para o engine, então memorystream reutilizável é mais
   simples que repeat-open.
3. Conta páginas via `PdfReader/PdfDocument` (rápido — não percorre
   content stream).
4. Resolve `tessdata` via `TessdataResolver.FindTessdata()`. Se achar,
   instancia `TesseractOcrEngine` + `PdfiumPageRenderer`. Senão fica
   `null` — `HybridExtractor` aceita ambos opcionais.
5. Monta:
   - `HeuristicPageClassifier`
   - `HybridExtractor(classifier, ocr?, renderer?)`
   - `RegexEntityDetector(BrazilianRegexRules.Default)`
   - `PdfSweepRedactionEngine(SkiaImageRedactor, ItextMetadataScrubber)`
   - `RedactionPipeline(extractor, detector, engine, pageCount)`
6. Roda `pipeline.RunAsync(...)` com um `review` delegate que
   auto-aprova qualquer entidade com `Confidence >= threshold`.
7. Se `--audit` foi passado, monta `AuditLog` (com hash da entrada) e
   serializa via `AuditWriter.WriteTo`.

## Como adicionar uma opção

`System.CommandLine` (beta4): cria um `Option<T>`, adiciona ao
`RootCommand`, lê em `ctx.ParseResult.GetValueForOption(...)` dentro
do `SetHandler`. Não precisa registrar handler explícito por opção —
o handler do root recebe o `InvocationContext`.

```csharp
var dryRun = new Option<bool>("--dry-run", () => false);
root.Add(dryRun);
root.SetHandler(async ctx =>
{
    var dry = ctx.ParseResult.GetValueForOption(dryRun);
    ...
});
```

## Como adicionar um subcomando (ex: `extract`)

```csharp
var extract = new Command("extract", "Extrai texto sem redigir.");
extract.Add(input);
extract.SetHandler(async ctx => { ... });
root.Add(extract);
```

## Convenções

- **Stdout = log human-friendly**, prefixado por `[sigilus]`. Para JSON
  estruturado adicione uma flag `--json`.
- **Exit code 0 = sucesso**, 1 = falha. Não definimos códigos
  específicos por categoria (ainda).
- **Auto-aprovação é a regra do CLI** — quem quer revisar usa a UI.
  Se precisar de modo interativo no CLI, criar um subcomando `review`
  com TUI (Spectre.Console).
