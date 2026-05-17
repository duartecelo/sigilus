# Sigilus.Detection

Detecção de dados sensíveis baseada em regex + heurísticas. Implementa
`IEntityDetector` de `Sigilus.Core` e expõe as regras padrão para PDFs
jurídicos brasileiros.

## Para que serve

Recebe um `PageContext` (texto + char rects da página), aplica regex
para localizar CPF, CNPJ, CNJ, OAB, RG, e-mail, telefone, **valida via
checksum quando aplicável** (CPF, CNPJ), e devolve `DetectedEntity`s
com retângulos prontos para alimentar a UI ou o engine de redação.

Para detecção de pessoas/locais sem padrão estrito (nomes, endereços
livres), use `Sigilus.Detection.Onnx`.

## Dependências

Só `Sigilus.Core`. Nada além do BCL (`System.Text.RegularExpressions`).

## Layout

```
Sigilus.Detection/
├── BrazilianRegexRules.cs           ← regras padrão (CPF/CNPJ + variantes OCR, CNJ, OAB, RG, email, phone, endereço, instituição)
├── CompositeEntityDetector.cs       ← agrega múltiplos detectores + dedup IoU + contenção
├── Coordinates/
│   ├── CharCoordIndex.cs            ← span [start,len) → 1+ PdfRect (quebra linhas)
│   └── WordSnapper.cs               ← expande rect cobrindo a palavra/frase contígua cross-run
├── PublicEntityFilterDetector.cs    ← decorator que descarta cidades/estados/órgãos públicos
├── RegexEntityDetector.cs           ← detector genérico parametrizado por regras
├── SnappingEntityDetector.cs        ← decorator que aplica WordSnapper a cada entidade
└── Validation/
    ├── BrazilianIdValidators.cs     ← checksum CPF e CNPJ
    └── PublicEntityFilter.cs        ← lista de cidades/estados/órgãos públicos brasileiros
```

### [`PublicEntityFilter`](Validation/PublicEntityFilter.cs)

Banco de dados local de entidades que **NÃO** devem ser pseudonimizadas:

- **27 estados** + DF (siglas e nomes completos sem acento).
- **~150 cidades** (todas as capitais + cidades de >200k hab.).
- **~80 órgãos públicos** brasileiros (MP, TJ, OAB, INSS, Receita
  Federal, Polícia, Defensoria, Procuradorias, Tribunais, Agências
  reguladoras…).
- **Termos genéricos jurídicos**: "Inquérito Civil", "Vara Cível", etc.
- **Fragmentos comuns de OCR**: "PROMO", "TORIA", "O PÚBLICO" — match
  exato isolado pra evitar falsos positivos de NER/tokenizer.

A função `IsPublic(text)` aplica 4 estratégias em sequência:

1. **Match exato** após normalizar (lowercase, sem acento, sem
   pontuação): "Estado do Rio Grande do Sul" → "ESTADO DO RIO GRANDE
   DO SUL" → bate na lista expandida.
2. **Match exato em fragmentos** (`SmallExact`): "Promo" → "PROMO".
3. **Substring**: termos âncora como "RIO GRANDE DO SUL", "PROMOTORIA"
   marcam qualquer detecção que os contenha.
4. **Substring reversa**: detecção fragmentada por OCR (ex: "TO ALEGRE")
   é aceita se for substring de uma cidade conhecida e cobrir ao menos
   50% dela.

### [`PublicEntityFilterDetector`](PublicEntityFilterDetector.cs)

Decorator que aplica o filtro. Tipos estruturados (CPF, CNPJ, email,
telefone, RG, OAB, processo CNJ) **passam intactos** — são sempre
sensíveis. Tipos textuais (PersonName, Address, Other) são filtrados.
Também descarta detecções muito curtas (< 3 chars, ou < 5 sem espaço)
que costumam ser ruído de tokenizer.

**Use no topo do pipeline** (antes do `SnappingEntityDetector` para
evitar trabalho desperdiçado em rects que vão ser descartados):

```csharp
var detector = new SnappingEntityDetector(
    new PublicEntityFilterDetector(
        new CompositeEntityDetector(new IEntityDetector[]
        {
            new RegexEntityDetector(BrazilianRegexRules.Default),
            new NerEntityDetector(onnxProvider),
            // LlmEntityDetector também passa por aqui
        })));
```

### [`WordSnapper`](Coordinates/WordSnapper.cs)

Expande um retângulo para cobrir a **palavra ou frase contígua** que
toca o seed. Crítico em OCR onde cada palavra vira um `TextRun` separado
(ex: um CNPJ partido em "88.254.", "875", "/0001-60" — três runs).

Algoritmo:

1. Acha todos os `TextRun.Bounds` que tocam o seed (com tolerância de
   meia altura de glyph).
2. Ordena por X dentro da mesma linha (Y baseline próximo).
3. Expande pra esquerda enquanto: gap horizontal &lt; 1.5× altura E
   **continuação semântica** (dígito↔dígito, letra↔letra, dígito↔hífen).
4. Idem pra direita.
5. Devolve a união dos rects dos runs selecionados.

### [`SnappingEntityDetector`](SnappingEntityDetector.cs)

Decorator de `IEntityDetector` que aplica `WordSnapper` a cada entidade
emitida pelo detector interno. **Use no topo da chain**:

```csharp
var detector = new SnappingEntityDetector(
    new CompositeEntityDetector(new IEntityDetector[]
    {
        new RegexEntityDetector(BrazilianRegexRules.Default),
        new NerEntityDetector(onnxProvider),
    }));
```

Sem ele, regex em OCR produz rects que cobrem só os "números do meio"
de CNPJs e similares.

### [`CompositeEntityDetector`](CompositeEntityDetector.cs)

Além do IoU > 0.5 (padrão), também descarta entidades **contidas** em
outra de maior confiança (cobertura > 80% da área interna). Isso elimina,
ex., um falso "telefone" cujo rect está inteiramente dentro de um "CNPJ"
da mesma página.

### [`CharCoordIndex`](Coordinates/CharCoordIndex.cs)

Mapeia offsets de caractere no `PageContext.ConcatenatedText` para
retângulos em PDF user-space. É o **único lugar** que sabe a relação
entre texto e geometria dentro da detecção.

```csharp
var index = new CharCoordIndex(page);
IReadOnlyList<PdfRect> rects = index.RectsFor(start: 245, length: 14);
```

- Aceita uma região que cruza linhas e devolve **múltiplos retângulos**
  (um por linha visualmente quebrada). Detecção: se `Y` do caractere
  atual difere do anterior em mais de 60% da altura de linha, abre novo
  retângulo.
- Char rects vazios (separadores, `\n`) são pulados na união, mas
  contam para os offsets — porque o offset vem do `ConcatenatedText`
  literal.

### [`Validation/BrazilianIdValidators`](Validation/BrazilianIdValidators.cs)

Estático, `ReadOnlySpan<char>`. Tira não-dígitos do span e roda os
algoritmos clássicos:

- **CPF**: 9 dígitos + dois DV (mod 11). Rejeita todos-iguais.
- **CNPJ**: 12 dígitos + dois DV (mod 11 com pesos `[5..2]` e `[6..2]`).
  Rejeita todos-iguais.

Sem alocação além do `stackalloc` de até 14 ints. Cobertura por
testes em `Sigilus.Detection.Tests/ValidatorTests.cs`.

### [`RegexEntityDetector`](RegexEntityDetector.cs)

Detector genérico. Aceita uma lista de `Rule`:

```csharp
public sealed record Rule(
    EntityType Type,
    Regex Pattern,
    float Confidence,
    Func<string, bool>? Validate = null);
```

Funciona assim:

1. Itera todas as `Rule` em ordem.
2. Para cada `Match`, opcionalmente roda `Validate(match.Value)` — útil
   para checksum. Se falhar, descarta.
3. Pede a `CharCoordIndex` os retângulos do match.
4. Emite um `DetectedEntity` **por retângulo** (porque um match pode
   cruzar linhas).

### [`BrazilianRegexRules.Default`](BrazilianRegexRules.cs)

| Tipo | Padrão | Confiança | Valida |
|---|---|---|---|
| Cpf | `\b\d{3}\.\d{3}\.\d{3}-\d{2}\b` | 0.99 | mod-11 |
| Cnpj | `\b\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}\b` | 0.99 | mod-11 |
| Cnpj (OCR raw) | `(?<![\d.\-/])\d{14}(?![\d.\-/])` | 0.95 | mod-11 |
| Cpf (OCR raw) | `(?<![\d.\-/])\d{11}(?![\d.\-/])` | 0.93 | mod-11 |
| ProcessoCnj | `\b\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}\b` | 0.98 | — |
| Oab | `\bOAB[/\s\-]?[A-Z]{2}[/\s\-]?\d{1,6}\b` | 0.92 | — |
| Rg | `\b\d{1,2}\.\d{3}\.\d{3}-[\dXx]\b` | 0.85 | — |
| Email | `\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b` | 0.97 | — |
| Phone | `\(\d{2}\)\s?9?\d{4}[\-\s]?\d{4}\b` ou `\b\d{2}\s9?\d{4}[\-\s]\d{4}\b` | 0.82–0.85 | — |
| Address | `\b(?:Rua\|R\.\|Av(?:enida)?\|...)\s+...,\s*\d+` | 0.90 | — |
| Address (CEP) | `\bCEP[\s:]*\d{5}-?\d{3}\b` | 0.93 | — |
| Other (instituição) | `\b(?:Escola\|Colégio\|Faculdade\|...\|Município\|Estado)\s+...` | 0.82 | — |

Todas as regex usam `RegexOptions.Compiled | CultureInvariant` e
timeout de 200ms (anti-ReDoS).

**Por que existem variantes "raw" de CPF/CNPJ**: OCR Tesseract frequentemente
perde a pontuação em IDs ("88254875000160" em vez de "88.254.875/0001-60").
As variantes raw aceitam 14/11 dígitos contínuos e **validam via checksum**
— falsos positivos são impossíveis porque um seqüencial de 14 dígitos
aleatórios tem chance < 1% de passar mod-11.

**Por que o regex de Phone foi apertado**: a versão anterior
(`\(?\d{2}\)?...`) casava 10 dígitos do meio de CNPJ. Hoje exige parêntese
OU espaço como separador, o que elimina o false positive sem perder
telefones reais que usam formato brasileiro padrão.

### [`CompositeEntityDetector`](CompositeEntityDetector.cs)

Agrega múltiplos `IEntityDetector` (ex: regex + NER ONNX) e **deduplica
por IoU**: ordena por confiança decrescente, mantém o primeiro de cada
cluster que sobreponha >50% (parametrizável).

```csharp
var detector = new CompositeEntityDetector(new IEntityDetector[]
{
    new RegexEntityDetector(BrazilianRegexRules.Default),
    new NerEntityDetector(onnxProvider),
}, iouThreshold: 0.5f);
```

Sem o composite, um nome detectado tanto por regex (heurística futura)
quanto por NER apareceria duas vezes na UI.

## Como adicionar uma nova regra

```csharp
var custom = new List<RegexEntityDetector.Rule>(BrazilianRegexRules.Default)
{
    new(EntityType.BankAccount,
        new Regex(@"\bAg(ência)?\s*\d{4}\s*C(onta)?\s*\d{5,8}-\d\b", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200)),
        Confidence: 0.90f,
        Validate: null),
};
var detector = new RegexEntityDetector(custom);
```

Recomendado: se a confiança for < 0.85, ela não auto-aprova no CLI
(threshold padrão), aparecendo só na UI para revisão humana.

## Como adicionar uma nova `EntityType`

1. Adicione o valor em `Sigilus.Core/Domain/Enums.cs`.
2. Crie a `Rule` correspondente.
3. Se for usado no NER, mapeie em `OnnxNerProvider.MapLabel`.

## Convenções

- **Cada regex tem timeout** — sem exceção. Documentos jurídicos podem
  ter parágrafos muito longos que matam regex backtracking ingênua.
- **Validação por checksum quando aplicável** — descarta false positives
  imediatamente, melhora muito a UX.
- **Detecções emitidas como `IAsyncEnumerable`** para streaming na UI.
  Hoje o detector é síncrono internamente, mas o contrato é assíncrono
  para permitir NER assíncrono no composite sem mudar a interface.
