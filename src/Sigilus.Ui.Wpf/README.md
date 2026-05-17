# Sigilus.Ui.Wpf

Interface gráfica WPF (.NET 8 + WPF) para revisão humana das detecções
antes da redação destrutiva. Implementa o "Human-in-the-Loop" do plano
arquitetural.

## Para que serve

- Carregar um PDF, renderizar páginas via PDFium.
- Detectar entidades sensíveis com regex (+ OCR se disponível).
- Mostrar **retângulos sobrepostos** que o usuário pode:
  - **clicar** para alternar aprovado/rejeitado;
  - **arrastar no espaço vazio** para criar tarjas manuais;
- Salvar como PDF redigido (destrutivo).

## Dependências

| Pacote | Versão | Por quê |
|---|---|---|
| `ModernWpfUI` | 0.9.6 | Controles Fluent/WinUI 2 (NavigationView, ToggleSwitch, FontIcon), temas light/dark, estilos AccentButton. Licença MIT. |
| `CommunityToolkit.Mvvm` | 8.3.2 | MVVM source generators (preparado para crescer). |

E referências a `Sigilus.Core`, `Sigilus.Pdf`, `Sigilus.Detection`,
`Sigilus.Detection.Onnx`, `Sigilus.Ocr`. `TargetFramework=net8.0-windows`,
`<UseWPF>true</UseWPF>`.

### App.xaml — bootstrap do tema

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ui:ThemeResources />
            <ui:XamlControlsResources />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

Sem isso, controles ModernWpf não recebem styles e caem para o look
default do WPF (Windows 7-ish).

## Layout

```
Sigilus.Ui.Wpf/
├── App.xaml + App.xaml.cs    ← bootstrap WPF + ModernWpf ThemeResources
├── AssemblyInfo.cs
├── Controls/
│   └── RedactionOverlay.cs   ← Canvas custom (rects, drag-to-draw, resize handles)
├── MainWindow.xaml           ← NavigationView + DocumentPage + SettingsPage + StatusBar
└── MainWindow.xaml.cs        ← state + handlers (open/render/detect/redact/theme)
```

## Layout visual (Fluent)

A UI usa `ui:NavigationView` (sidebar à esquerda, recolhível) com 2 itens:

- **Documento**: viewer + card de ações (Abrir, Detectar, Cancelar, Salvar) + navegação de página + zoom.
- **Configurações**: tema, pseudonimização (ToggleSwitch), IA de nomes (ToggleSwitch), status do OCR, sobre.

No rodapé da sidebar há um botão **Tema claro / Tema escuro** com ícone
Sol/Lua. Atalho `Ctrl+T`. Também há combo "Sistema/Claro/Escuro" em
Configurações.

A **status bar** (rodapé) mostra:
- À esquerda: mensagem + ProgressBar fina (6px) que aparece só durante a detecção.
- À direita: chips "OCR" e "IA" com opacidade indicando ativo/inativo.

## Glyphs Segoe Fluent Icons usados

| Glyph | Onde | Significado |
|---|---|---|
| `E8A5` | Sidebar | Documento |
| `E713` | Sidebar | Configurações (engrenagem) |
| `E706` | Botão tema (dark) | Sol — clique vai pro claro |
| `E708` | Botão tema (light) | Lua — clique vai pro escuro |
| `E8E5` | Abrir | Pasta aberta |
| `E721` | Detectar | Lupa |
| `E711` | Cancelar | X |
| `E74E` | Salvar | Disquete |
| `E76B` / `E76C` | Nav. pág. | Setas ◀ ▶ |
| `E9A6` | Ajustar | Fit window |
| `E948` / `E949` | Zoom +/- | Lupa+/- |
| `E99A` | IA | Cérebro |

Fonte: `Segoe Fluent Icons` (Windows 11) com fallback `Segoe MDL2 Assets`
(Windows 10).

### [`Controls/RedactionOverlay`](Controls/RedactionOverlay.cs)

`Canvas` que mantém uma `ObservableCollection<RedactionDecision>` e a
re-desenha. **Único lugar da UI** que converte PDF user-space → DIPs.

DPs:

- `PageWidthPts`, `PageHeightPts` (double): tamanho da página em pontos
  (do `PageContext`).

Comportamento:

- Cada decisão vira um `Rectangle`:
  - Aprovada: borda vermelha, fill `Color.FromArgb(96, 255, 0, 0)`.
  - Rejeitada: borda laranja, fill semitransparente claro.
- Click no retângulo → flipa `Approved` no item da coleção (e
  redesenha porque é `ObservableCollection`).
- MouseDown no fundo → começa drag. MouseMove desenha rect provisório.
  MouseUp grava nova decisão `Origin=null`, `Reason="manual"`.

Conversão (escala uniforme baseada em `ActualWidth / PageWidthPts`):

```csharp
// pt (bottom-left) → DIP (top-left)
xDip = pdf.X * scale;
yDip = (PageHeightPts - (pdf.Y + pdf.Height)) * scale;

// inverse: DIP → pt
pdfX = xDip / scale;
pdfY = PageHeightPts - yDip/scale - pdfH;
```

### [`MainWindow.xaml`](MainWindow.xaml)

Layout DockPanel:

- `ToolBarTray IsLocked="True"` + `ToolBar` com `Loaded="OnToolBarLoaded"`.
  - **`IsLocked="True"`** remove o gripper de arrasto à esquerda do
    ToolBar (aquele pontinho vertical que faz o cursor virar
    "arrastar").
  - O handler `OnToolBarLoaded` esconde o **overflow chevron** à
    direita via `tb.Template.FindName("OverflowGrid", tb)`.
- `ScrollViewer > Grid x:Name=PageGrid`:
  - `Image x:Name=PageImage` (bitmap da página).
  - `RedactionOverlay` (criado em code-behind e adicionado ao Grid).
- Status text no fim do ToolBar.

### [`MainWindow.xaml.cs`](MainWindow.xaml.cs)

Estado:

- `_pdfBytes`: PDF carregado inteiro (escolha consciente; cada operação
  rebobina).
- `_pageIndex` / `_pageCount`: navegação.
- `_renderer`: `PdfiumPageRenderer`.
- `_overlay`: instância da `RedactionOverlay`.
- `_tessdata`: caminho do tessdata se resolvido — usado para criar o
  pool sob demanda.
- `_detectCts`: `CancellationTokenSource` da detecção em curso (`null`
  se nenhuma).
- `_detectionsByPage`: `ConcurrentDictionary<int, List<DetectedEntity>>`
  com tudo que foi achado.

Fluxo:

1. **Construtor**: `InitializeComponent`, adiciona o overlay ao grid,
   tenta resolver tessdata, atualiza status.
2. **OnOpenClick**: lê bytes, conta páginas, renderiza página 0, limpa
   estado de detecção anterior.
3. **RenderCurrentPage**: PDFium @ 150 DPI → `BitmapImage` (Freeze).
   Sincroniza tamanho do overlay.
4. **OnDetectClick**: cria `CancellationTokenSource`, desabilita
   "Detectar" e "Redigir", habilita "Cancelar". Cria
   `TesseractEnginePool` se houver tessdata. Dispara
   `Task.Run(() => RunDetectionAsync(...))`.
5. **RunDetectionAsync** (background): roda `Parallel.ForEachAsync`
   sobre `[0..pageCount)` com `MaxDegreeOfParallelism =
   Environment.ProcessorCount - 1`. Cada worker:
   - cria seu próprio `HybridExtractor` (stateless aceita ser
     instanciado por chamada);
   - extrai a página, roda detector regex;
   - acumula em `_detectionsByPage[p]`;
   - reporta progresso via `IProgress<DetectProgressReport>` →
     `Progress<>` faz o marshalling pra UI thread automaticamente.
6. **OnProgress** (UI thread): atualiza `ProgressBar.Value` e
   `ProgressText`.
7. **OnCancelClick**: `_detectCts.Cancel()`. O `Parallel.ForEachAsync`
   propaga e lança `OperationCanceledException`, que o handler
   captura silenciosamente atualizando o status.
8. **PopulateOverlayForVisiblePage**: depois do detect terminar,
   despeja as detecções da página visível no overlay (outras páginas
   ficam guardadas em `_detectionsByPage`).
9. **OnRedactClick**: agrega TODAS as detecções aprovadas de TODAS as
   páginas + manuais do overlay atual, executa `RedactAsync` em
   `Task.Run` (escrita também é assíncrona para não travar a UI).

## Por que tem "uma zona pequena que vira cursor de arrastar"?

Era o **ToolBar drag gripper** do WPF (padrão visual). Fix:
`ToolBarTray IsLocked="True"` no XAML — agora ele some.

## Por que a UI não trava mais durante o Detectar?

A detecção corre em `Task.Run` (fora do UI thread) com
`Parallel.ForEachAsync(MaxDegreeOfParallelism = ProcessorCount - 1)`.
PDFium e o pool de Tesseract são consumidos por N workers em
paralelo. A UI fica livre para clicar Cancelar, navegar etc.

Updates de progresso usam `IProgress<T>` (a classe `System.Progress<T>`
faz o marshalling pra UI thread automaticamente via
`SynchronizationContext` capturado no construtor).

## Por que ProgressBar fica em "%" e contagens

`OnProgress` recebe um `DetectProgressReport(Completed, Total,
EntitiesSoFar)` por página finalizada. O `ProgressBar.Value` é a `%` e
o `TextBlock` sobreposto mostra `"X / Y pág.  ·  N detecções  ·  P%"`.

## Navegação entre páginas

A toolbar tem `◀ pág. X / Y ▶`. Os atalhos:

| Tecla | Ação |
|---|---|
| `PageDown` / `→` | próxima página |
| `PageUp` / `←` | página anterior |
| `Home` | primeira página |
| `End` | última página |

Quando você navega, **a página é re-renderizada** (PDFium @ 150 DPI),
o overlay é repopulado a partir de `_detectionsByPage[_pageIndex]`
(sem re-detectar) e o zoom auto-ajusta se estiver em modo "Ajustar".

## Zoom e Ajustar à janela

Botões na toolbar: `Ajustar  −  100%  +  1:1`.

| Atalho | Ação |
|---|---|
| `Ctrl + Roda do mouse` | zoom in/out sobre o cursor |
| `Ctrl + +` / `Ctrl + -` | zoom in/out |
| `Ctrl + 0` | reset 100% |

**Implementação**: a página é renderizada uma vez a 150 DPI e o zoom
aplica `ScaleTransform` no `LayoutTransform` do `PageGrid` —
**não re-renderiza**, é fluido até 600% sem perda perceptível em
documentos normais. Para qualidade tipográfica em zoom altíssimo,
trocar `LayoutTransform` por uma re-renderização no DPI requerido
(custa mais, vale só se for ler diagramas finos).

Modo **Ajustar**: ligado por padrão ao abrir um PDF. Calcula
`scale = min(viewportW/bmpW, viewportH/bmpH)` e re-ajusta quando a
janela é redimensionada. Qualquer interação manual de zoom desliga o
auto-fit; clicar "Ajustar" liga de novo.

Limites: `[10%, 600%]`. Step multiplicativo `×1.20`.

## Resize handles e snap

Quando o mouse paira sobre um retângulo, 4 cantos brancos com borda
preta aparecem (TL, TR, BL, BR). Arrastar qualquer canto redimensiona
o retângulo em tempo real — a `RedactionDecision` é substituída via
`Decisions[idx] = dec with { Bounds = ... }` e o overlay redesenha.

**Snap ao soltar tarja manual** (`RedactionOverlay.SnapFn`):

A `MainWindow.SnapManualRect` é registrada como `SnapFn` do overlay.
Quando você desenha um retângulo arrastando o mouse e solta:

1. **NER detection match**: se já houve `Detectar` e existe uma
   `DetectedEntity` na página com IoU > 0.1 com o rect proposto,
   adota o `Bounds` dela. Isso pega o nome inteiro quando o NER acertou
   mas você só agarrou parte.
2. **Snap geométrico** (sempre disponível): percorre os `TextRun` da
   página visível (cache de `PageContext`), encontra os `CharBounds` que
   tocam o rect proposto (com tolerância de meia altura), expande pra
   esquerda/direita enquanto for "mesma palavra" (mesma linha, char
   vizinho não-vazio), e devolve a união. Funciona em texto nativo e OCR
   — em ambos os casos `CharBounds` está sincronizado.
3. **Fallback**: se nada tocar, devolve o rect como desenhado.

## Pseudonimização vs Tarja (checkbox)

A toolbar tem `☑ Pseudonimizar`. Marcado: `Aplicar & salvar` usa o
`PseudonymizationRedactionEngine` (substitui valor por pseudônimo
determinístico). Desmarcado: usa o `PdfSweepRedactionEngine` clássico
(tarja preta).

## NER (BERTimbau) opt-in

A checkbox `☐ NER (nomes)` carrega lazy o modelo ONNX local. Procura
em `./models/` (ou `SIGILUS_NER_MODELS` env var):

- `ner-ptbr.onnx` (modelo BIO em INT8)
- `vocab.txt` (BERT WordPiece)
- `labels.json` (array de strings `["O","B-PER","I-PER",...]`)

Se faltar qualquer um, exibe MessageBox informativa e desmarca a
checkbox — **nunca crasha**. Carregamento ocorre em `Task.Run` para
não travar a UI; o status mostra "Carregando modelo NER…".

Quando ativo, `RunDetectionAsync` adiciona um `NerEntityDetector` ao
`CompositeEntityDetector` ao lado do `RegexEntityDetector`. As detecções
NER (nomes/endereços) aparecem no overlay junto com as regex.

## Por que coordenadas do overlay continuam corretas com zoom

A `ScaleTransform` está em `PageGrid.LayoutTransform`, que envolve
**Image + Overlay juntos**. Como o overlay já mede em pixels do bitmap
(mesma escala que o `Image`), a transformação atinge ambos
igualmente — não precisa de matemática extra. Coordenadas de PDF
user-space → pixels do bitmap continuam acontecendo dentro do overlay
como antes.

## Como adicionar navegação multi-página

Hoje o overlay só desenha a página visível. Para adicionar Previous/Next:

```csharp
private void OnPrevPage(object s, RoutedEventArgs e)
{
    if (_pageIndex == 0) return;
    _pageIndex--;
    RenderCurrentPage();
    _overlay.Decisions.Clear();
    // se já detectou, refiltrar _allDetections por pageIndex aqui
}
```

Armazene as detecções de todas as páginas em `Dictionary<int, List<DetectedEntity>>`
no detect, e refiltre por `_pageIndex` ao navegar.

## Como adicionar zoom

Coloque o `PageImage` + `Overlay` dentro de um `Viewbox` ou aplique
`LayoutTransform=new ScaleTransform(zoom, zoom)` no `PageGrid`. O
overlay é em DIPs ligadas à `ActualWidth` — recalcula sozinho ao
re-layout.

## Convenções

- **Sem MVVM pesado**: code-behind direto. O projeto carrega
  `CommunityToolkit.Mvvm` mas não usa porque o app é pequeno. Quando
  crescer (lista de páginas, settings, batch), refatorar para
  `ObservableObject` com `[ObservableProperty]`/`[RelayCommand]`.
- **Renderiza a 150 DPI** — suficiente para tela; OCR usa 300 DPI
  separadamente.
- **`InitializeComponent` chama o XAML gerado** — não tente criar
  controles custom *no XAML* a partir de um `clr-namespace` do próprio
  assembly se evitar; cria em code-behind para evitar problemas de
  build (o gerador roda antes do C# compilar).
