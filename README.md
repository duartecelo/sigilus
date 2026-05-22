# Sigilus 2.0

Redator destrutivo de PDFs jurídicos brasileiros, com pseudonimização
determinística local. **Sem cloud, sem internet em runtime** (apenas
para baixar componentes opcionais na primeira vez).

## Visão geral

Sigilus apaga **de verdade** dados sensíveis (CPF, CNPJ, nomes,
endereços, e-mails, telefones, processos) de PDFs e os substitui por
equivalentes fictícios coerentes ("João da Silva" → "Fulano",
mantendo o mesmo Fulano em todas as ocorrências do documento). Usa
`iText.pdfsweep` para redação destrutiva real (não overlay) e SkiaSharp
para apagar pixels em páginas escaneadas.

Detecção combina:

- **Regex** com checksum BR (CPF, CNPJ, CNJ, OAB, RG, e-mail, telefone, CEP).
- **NER** (LeNER-Br quantizado INT8, ~104 MB) — Named Entity Recognition.
- **LLM local** (Gemma 3 / Qwen 2.5 / Phi / Llama via LLamaSharp+llama.cpp)
  — opcional, mais lento, melhor cobertura de texto livre.

## Instalação rápida (Windows x64)

```cmd
git clone https://github.com/duartecelo/sigilus.git
cd sigilus
bootstrap.bat
```

O `bootstrap.bat`:

1. Verifica o .NET 8 SDK (instala via `aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe`
   se faltar).
2. `dotnet restore` + `dotnet publish` single-file self-contained.
3. Baixa o `tessdata` (português) do GitHub oficial do Tesseract.
4. Copia os artefatos versionados de `models/` para o publish.

Resultado: `publish\Sigilus\Sigilus.exe` rodando. Modelos NER e LLM
ficam para download via UI (Configurações → "Baixar / atualizar...").

> ⚠ **NÃO instale dentro de OneDrive/iCloud/Drive.** O `llama.cpp` usa
> `mmap` e arquivos sincronizados na nuvem ficam como placeholder —
> falha com `LoadWeightsFailedException`. O Sigilus detecta isso e
> avisa logo na primeira abertura.

## Arquitetura

```
Sigilus.sln
├── src/
│   ├── Sigilus.Core/             ← domínio + interfaces + AssetDownloader
│   ├── Sigilus.Pdf/              ← iText 9 + pdfsweep + Skia + PDFium
│   ├── Sigilus.Ocr/              ← Tesseract wrapper + TessdataCatalog
│   ├── Sigilus.Detection/        ← regex + filtros (Public/Hallucination)
│   ├── Sigilus.Detection.Onnx/   ← NER ONNX + NerModelCatalog
│   ├── Sigilus.Detection.Llm/    ← LLamaSharp + ChatTemplate + LlmModelCatalog
│   ├── Sigilus.Ui.Wpf/           ← WPF + ModernWpfUI + AssetsManagerWindow
│   └── Sigilus.Cli/              ← linha de comando (System.CommandLine)
└── tests/
    ├── Sigilus.Core.Tests/
    ├── Sigilus.Detection.Tests/
    ├── Sigilus.Pdf.Tests/
    └── Sigilus.E2E.Tests/
```

Cada projeto em `src/` tem um `README.md` próprio com decisões de
design, layout, snippets de uso.

## Componentes baixáveis

Tudo configurável via env vars ou edição dos arquivos `*Catalog.cs`:

| Componente | Default | Fonte | Catálogo |
|---|---|---|---|
| Tessdata PT | `por.traineddata` (rápido, ~3 MB) | `github.com/tesseract-ocr/tessdata_fast` | [`TessdataCatalog.cs`](src/Sigilus.Ocr/TessdataCatalog.cs) |
| NER PT-BR | LeNER-Br INT8 ONNX (~104 MB) | `SIGILUS_NER_BASE_URL` ou GitHub Release | [`NerModelCatalog.cs`](src/Sigilus.Detection.Onnx/NerModelCatalog.cs) |
| LLM | Gemma 3 4B IT Q4_K_M (~2.5 GB) | Hugging Face (bartowski) | [`LlmModelCatalog.cs`](src/Sigilus.Detection.Llm/LlmModelCatalog.cs) |

### Hospedando o NER em GitHub Release

O modelo NER (`ner-ptbr.onnx` + 3 metadata files) precisa estar
acessível por URL pública. Sugerido: criar um release no próprio repo:

```bash
gh release create models-v1 \
  models/ner-ptbr.onnx \
  models/vocab.txt \
  models/labels.json \
  models/tokenizer_config.json \
  --title "Modelos NER v1"
```

Depois ajuste `NerModelCatalog.ResolveBaseUrl()` ou exporte
`SIGILUS_NER_BASE_URL` apontando para o release.

## Build manual (sem bootstrap)

```cmd
dotnet restore
dotnet test
dotnet publish src/Sigilus.Ui.Wpf -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/Sigilus
```

## CLI (modo headless)

```cmd
dotnet run --project src/Sigilus.Cli -- ^
    --input arquivo.pdf ^
    --output redigido.pdf ^
    --audit auditoria.json
```

## Diagnóstico

Toda execução grava `Sigilus-log.txt` ao lado do `.exe`:

```
[INFO] [startup] OS/CPU/RAM
[INFO] [ocr] tessdata=...
[INFO] [ner] model=...
[INFO] [llm] ggufs encontrados: ...
[INFO] [detect-p1] pag    1/100 |   142 ms |   3 hits | cls=NativeText
[INFO] [detect-p2] pag    1/100 |   12.4 s | +  2 novos | LLM extraiu 5 candidatos
[ERR ] [detect-p2] pag   17/100 FALHOU após 4.2 s
  → System.OutOfMemoryException: ...
```

Tipo de problemas que o log captura: `OneDrive detectado`,
`ContextOverflowException` do LLM, falhas de download, OCR ausente,
exceções nativas (capturadas via `AppDomain.UnhandledException`).

## Licenças e atribuições

- iText 9 (AGPL — comercial para uso interno em escritórios jurídicos
  está sob revisão; entre em contato com iText para licença).
- LeNER-Br: Pierre Guillou, Apache-2.0.
- Modelos LLM: licenças de cada fornecedor (Google Gemma terms,
  Alibaba Qwen license, Meta Llama community license, MIT).
- Tesseract: Apache-2.0.

## Testes

`dotnet test` roda 33+ testes cobrindo:

- Validadores CPF/CNPJ (`BrazilianIdValidators`).
- Filtros (`PublicEntityFilter`, `LlmHallucinationFilter`).
- Inferência de gênero PT-BR (`GenderInference`).
- End-to-end de redação destrutiva (extrai → redige → re-extrai e
  asserta que dados sensíveis sumiram com 2 estratégias diferentes
  do iText).
