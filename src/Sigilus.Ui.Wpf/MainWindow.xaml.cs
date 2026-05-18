using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using iText.Kernel.Pdf;
using Microsoft.Win32;
using ModernWpf;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Core.Pseudonymization;
using Sigilus.Detection;
using Sigilus.Detection.Coordinates;
using Sigilus.Detection.Llm;
using Sigilus.Detection.Onnx;
using Sigilus.Ocr;
using Sigilus.Pdf.Abstractions;
using Sigilus.Pdf.Extraction;
using Sigilus.Pdf.Redaction;
using Sigilus.Pdf.Rendering;
using Sigilus.Ui.Wpf.Controls;

namespace Sigilus.Ui.Wpf;

public partial class MainWindow : Window
{
    // ---- documento ----
    private byte[]? _pdfBytes;
    private string? _pdfPath;
    private int _pageIndex;
    private int _pageCount;
    private readonly PdfiumPageRenderer _renderer = new();
    private readonly RedactionOverlay _overlay = new();
    private string? _tessdata;

    // ---- zoom ----
    private const int RenderDpi = 150;
    private const double MinZoom = 0.10, MaxZoom = 6.00, ZoomStep = 1.20;
    private double _zoom = 1.0;
    private bool _autoFit = true;
    private int _bitmapPixelWidth, _bitmapPixelHeight;

    // ---- detecção ----
    private CancellationTokenSource? _detectCts;
    private readonly ConcurrentDictionary<int, List<DetectedEntity>> _detectionsByPage = new();
    private PageContext? _visiblePageContext;

    // ---- NER ----
    private OnnxNerProvider? _nerProvider;

    // ---- LLM ----
    private LlmEntityDetector? _llmDetector;
    private string? _lastLlmError;

    public MainWindow()
    {
        InitializeComponent();
        PageGrid.Children.Add(_overlay);
        _overlay.SnapFn = SnapManualRect;

        // Log de inicialização (vai pro Sigilus-log.txt ao lado do exe).
        Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("startup", Sigilus.Ui.Wpf.Diagnostics.AppLog.EnvironmentReport());

        // Aviso preventivo na 1ª abertura se o app está em pasta OneDrive/Drive.
        if (LlmEntityDetector.LooksLikeCloudSyncedPath(AppContext.BaseDirectory))
        {
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Warn("startup",
                $"Aplicativo está em pasta sincronizada na nuvem: {AppContext.BaseDirectory}. " +
                "Modelos grandes (GGUF) podem falhar ao carregar por causa do mmap em arquivos placeholder. " +
                "Recomendado mover para uma pasta local como C:\\Sigilus\\.");
            Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show(this,
                    "Atenção: o Sigilus está instalado em pasta sincronizada na nuvem\n" +
                    "(OneDrive, iCloud, Google Drive ou similar):\n\n" +
                    $"{AppContext.BaseDirectory}\n\n" +
                    "Isso pode causar erro ao carregar modelos de IA grandes — esses\n" +
                    "serviços guardam arquivos como \"placeholder\" e o llama.cpp\n" +
                    "precisa do arquivo totalmente local.\n\n" +
                    "Recomendado:\n" +
                    "  1. Feche o Sigilus.\n" +
                    "  2. Mova a pasta inteira para um diretório local (ex: C:\\Sigilus\\).\n" +
                    "  3. Abra o Sigilus.exe da nova localização.\n\n" +
                    "Você pode continuar usando aqui, mas se o LLM falhar é por isso.",
                    "Sigilus — pasta sincronizada detectada",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        _tessdata = TessdataResolver.FindTessdata();
        Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("ocr", $"tessdata={_tessdata ?? "(não encontrado)"}");
        UpdateOcrStatus();

        var nerPaths = NerModelResolver.Find();
        Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("ner", $"model={(nerPaths is null ? "(não encontrado)" : nerPaths.ModelPath)}");
        UpdateNerStatus(canLoad: nerPaths is not null, loaded: false);

        var ggufList = LlmModelResolver.ListGgufs();
        Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("llm", $"ggufs encontrados: {ggufList.Count} → {string.Join(", ", ggufList.Select(System.IO.Path.GetFileName))}");
        RefreshLlmModelList();

        // Tema inicial: Dark (combo já vem com Dark selecionado).
        ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
        UpdateThemeIcon();

        UpdatePageIndicator();
        UpdateNavButtons();
    }

    // ===================== NAVEGAÇÃO ENTRE PÁGINAS DA UI =====================

    private void OnNavSelectionChanged(ModernWpf.Controls.NavigationView sender,
                                       ModernWpf.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem == NavSettings)
        {
            DocumentPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsPage.Visibility = Visibility.Collapsed;
            DocumentPage.Visibility = Visibility.Visible;
        }
    }

    // ===================== TEMA =====================

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        var current = ThemeManager.Current.ApplicationTheme ?? ApplicationTheme.Dark;
        var next = current == ApplicationTheme.Dark ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ThemeManager.Current.ApplicationTheme = next;
        UpdateThemeIcon();
        SyncThemeCombo();
    }

    private void OnThemeComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        switch (tag)
        {
            case "Light": ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light; break;
            case "Dark": ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark; break;
            case "System": ThemeManager.Current.ApplicationTheme = null; break;
        }
        UpdateThemeIcon();
    }

    private void SyncThemeCombo()
    {
        var t = ThemeManager.Current.ApplicationTheme;
        var tag = t switch
        {
            ApplicationTheme.Light => "Light",
            ApplicationTheme.Dark => "Dark",
            _ => "System",
        };
        foreach (var item in ThemeCombo.Items)
            if (item is ComboBoxItem c && (string?)c.Tag == tag) { ThemeCombo.SelectedItem = c; break; }
    }

    private void UpdateThemeIcon()
    {
        var dark = ThemeManager.Current.ApplicationTheme == ApplicationTheme.Dark
                   || (ThemeManager.Current.ApplicationTheme is null && ThemeManager.Current.ActualApplicationTheme == ApplicationTheme.Dark);
        // Glyphs Segoe Fluent Icons: E706 = Sol/Brilho, E708 = Lua. No tema escuro
        // mostramos Sol (clique vai pra claro). No tema claro mostramos Lua.
        ThemeIcon.Text = dark ? "" : "";
        ThemeLabel.Text = dark ? "Tema claro" : "Tema escuro";
    }

    // ===================== NER (CONFIG) =====================

    private async void OnNerToggled(object sender, RoutedEventArgs e)
    {
        if (NerToggle.IsOn != true)
        {
            _nerProvider?.Dispose();
            _nerProvider = null;
            UpdateNerStatus(canLoad: NerModelResolver.Find() is not null, loaded: false);
            return;
        }

        var paths = NerModelResolver.Find();
        if (paths is null)
        {
            NerToggle.IsOn = false;
            UpdateNerStatus(canLoad: false, loaded: false);
            MessageBox.Show(this,
                "Nenhum modelo de IA foi encontrado.\n\n" +
                "Para ativar, coloque os arquivos abaixo na pasta 'models' (ao lado do programa):\n" +
                "  • ner-ptbr.onnx\n  • vocab.txt\n  • labels.json",
                "Sigilus — IA indisponível", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        NerToggle.IsEnabled = false;
        UpdateNerStatus(canLoad: true, loaded: false, loading: true);
        try
        {
            var loaded = await Task.Run<OnnxNerProvider?>(() =>
            {
                try { return new OnnxNerProvider(paths.ModelPath, paths.VocabPath, paths.Labels, paths.LowerCase); }
                catch { return null; }
            });
            _nerProvider = loaded;
            if (loaded is null)
            {
                NerToggle.IsOn = false;
                UpdateNerStatus(canLoad: true, loaded: false, error: true);
            }
            else
            {
                UpdateNerStatus(canLoad: true, loaded: true);
            }
        }
        finally
        {
            NerToggle.IsEnabled = true;
        }
    }

    private void UpdateNerStatus(bool canLoad, bool loaded, bool loading = false, bool error = false)
    {
        if (loading) NerStatusText.Text = "Estado: carregando modelo (pode levar alguns segundos)…";
        else if (error) NerStatusText.Text = "Estado: falha ao carregar o modelo. Verifique os arquivos.";
        else if (loaded) NerStatusText.Text = "Estado: ativo. Detecções de nomes e endereços vão aparecer junto com as regras padrão.";
        else if (canLoad) NerStatusText.Text = "Estado: modelo disponível, não carregado. Ative o interruptor para usar.";
        else NerStatusText.Text = "Estado: nenhum modelo na pasta 'models'. Veja instruções acima.";

        StatusNerChip.Opacity = loaded ? 1.0 : 0.4;
    }

    // ===================== LLM (CONFIG) =====================

    private async void OnLlmToggled(object sender, RoutedEventArgs e)
    {
        if (LlmToggle.IsOn != true)
        {
            _llmDetector?.Dispose();
            _llmDetector = null;
            UpdateLlmStatus(canLoad: LlmModelResolver.ListGgufs().Count > 0, loaded: false);
            return;
        }

        var gguf = LlmModelCombo.SelectedItem as string ?? LlmModelResolver.FindGguf();
        if (gguf is null || !File.Exists(gguf))
        {
            LlmToggle.IsOn = false;
            UpdateLlmStatus(canLoad: false, loaded: false);
            MessageBox.Show(this,
                "Nenhum modelo LLM (.gguf) foi encontrado.\n\n" +
                "Para ativar, baixe um modelo GGUF compatível e coloque na pasta 'models/llm/' ao lado do programa.\n" +
                "Modelos recomendados (Hugging Face):\n" +
                "  • Gemma 3 4B IT  (~2.5 GB, ótimo PT-BR)\n" +
                "  • Qwen 2.5 7B Instruct  (~4.5 GB, mais preciso)\n" +
                "  • Phi 3.5 Mini  (~2 GB, mais rápido)\n\n" +
                "Após adicionar arquivos, use o botão 'Atualizar lista'.",
                "Sigilus — LLM indisponível", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Mantém o toggle utilizável: o usuário pode desligar pra cancelar.
        // Combo trava porque trocar de modelo no meio do load corrompe estado.
        LlmModelCombo.IsEnabled = false;
        UpdateLlmStatus(canLoad: true, loaded: false, loading: true, modelPath: gguf);

        var fileInfo = new FileInfo(gguf);
        Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("llm-load",
            $"Iniciando load: {Path.GetFileName(gguf)} ({fileInfo.Length / 1024 / 1024} MB)");

        // Aviso preventivo: pasta sincronizada na nuvem (OneDrive/iCloud/etc)
        // intercepta I/O e o arquivo pode estar como "placeholder" — quebra
        // o mmap do llama.cpp.
        if (LlmEntityDetector.LooksLikeCloudSyncedPath(gguf))
        {
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Warn("llm-load", "Caminho parece estar em pasta sincronizada (OneDrive/iCloud/Drive). Vou tentar hidratar antes; se falhar, mova para diretório local.");
            UpdateLlmStatus(canLoad: true, loaded: false, loading: true, modelPath: gguf,
                loadingNote: "Detectado OneDrive/Drive — baixando arquivo localmente primeiro…");
        }
        try
        {
            // LoadAsync usa TaskCreationOptions.LongRunning → thread dedicado,
            // UI fica 100% responsiva (você pode navegar abas, scroll, etc.).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var loaded = await LlmEntityDetector.LoadAsync(gguf).ConfigureAwait(true);
            sw.Stop();
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("llm-load",
                $"OK em {sw.Elapsed.TotalSeconds:F1}s | template={loaded.Template}");

            // Se o usuário desligou o toggle enquanto carregava → descarta.
            if (LlmToggle.IsOn != true)
            {
                loaded.Dispose();
                _llmDetector = null;
                UpdateLlmStatus(canLoad: true, loaded: false);
                return;
            }

            _llmDetector = loaded;
            UpdateLlmStatus(canLoad: true, loaded: true, modelPath: gguf, template: loaded.Template);
        }
        catch (Exception ex)
        {
            // NÃO engolir — mostra a causa real no status + popup com detalhe
            // + grava tudo no Sigilus-log.txt pra debug remoto.
            LlmToggle.IsOn = false;
            _llmDetector = null;
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Error("llm-load",
                $"Falha ao carregar {Path.GetFileName(gguf)}", ex);

            var rootMsg = ExtractRootMessage(ex);
            var rootType = ExtractRootType(ex);
            UpdateLlmStatus(canLoad: true, loaded: false, errorMessage: rootMsg);

            var isCloud = LlmEntityDetector.LooksLikeCloudSyncedPath(gguf);
            var cloudHint = isCloud
                ? "\n⚠ DETECTADO: O Sigilus está em pasta sincronizada (OneDrive/iCloud/Drive).\n" +
                  "   Essa é a causa MAIS PROVÁVEL do erro.\n" +
                  "   SOLUÇÃO: Mova a pasta inteira do Sigilus para um diretório\n" +
                  "   local fora do OneDrive (ex: C:\\Sigilus\\) e tente de novo.\n"
                : string.Empty;

            var popupText =
                $"Não foi possível carregar o modelo.\n\n" +
                $"Arquivo: {Path.GetFileName(gguf)} ({fileInfo.Length / 1024 / 1024} MB)\n" +
                $"Tipo do erro: {rootType}\n" +
                $"Mensagem: {rootMsg}\n" +
                cloudHint +
                $"\nDetalhes técnicos foram gravados em:\n{Sigilus.Ui.Wpf.Diagnostics.AppLog.LogPath}\n\n" +
                "Causas comuns:\n" +
                "  • Sigilus dentro de OneDrive/iCloud/Drive (mais comum).\n" +
                "  • GGUF mais novo que a versão do llama.cpp embutida.\n" +
                "  • Arquivo corrompido ou download incompleto.\n" +
                "  • RAM insuficiente para o tamanho do modelo.\n" +
                "  • CPU sem AVX/AVX2 (PCs muito antigos).\n" +
                "  • Antivírus bloqueando as DLLs nativas.\n\n" +
                "Envie o arquivo Sigilus-log.txt para o suporte.";
            MessageBox.Show(this, popupText,
                "Sigilus — Falha ao carregar LLM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            LlmModelCombo.IsEnabled = true;
        }
    }

    private static string ExtractRootType(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null) cur = cur.InnerException;
        return cur.GetType().Name;
    }

    /// <summary>Extrai a mensagem da exceção mais interna (causa raiz).</summary>
    private static string ExtractRootMessage(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null) cur = cur.InnerException;
        return cur.Message ?? ex.GetType().Name;
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        // Se o usuário trocou de modelo enquanto o LLM já estava carregado,
        // descarrega — o próximo toggle "On" vai recarregar o novo.
        if (_llmDetector is not null)
        {
            _llmDetector.Dispose();
            _llmDetector = null;
            if (LlmToggle.IsOn) LlmToggle.IsOn = false;
            UpdateLlmStatus(canLoad: true, loaded: false);
        }
    }

    private void OnLlmRefreshClick(object sender, RoutedEventArgs e) => RefreshLlmModelList();

    private void OnLlmDownloadClick(object sender, RoutedEventArgs e)
    {
        // Resolve diretório destino: prefere models/llm/ ao lado do exe, mesmo
        // que vazio (cria se precisar).
        var dest = Path.Combine(AppContext.BaseDirectory, "models", "llm");
        try
        {
            var dlg = new DownloadModelWindow(dest) { Owner = this };
            var ok = dlg.ShowDialog();
            // Se baixou algum modelo, atualiza a lista do combo.
            if (dlg.DownloadedPath is not null)
            {
                RefreshLlmModelList();
                // Auto-seleciona o recém-baixado pra facilitar.
                if (LlmModelCombo.Items.Contains(dlg.DownloadedPath))
                    LlmModelCombo.SelectedItem = dlg.DownloadedPath;
            }
        }
        catch (Exception ex)
        {
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Error("download-win", "Falha ao abrir janela de download", ex);
            MessageBox.Show(this,
                $"Não foi possível abrir a janela de download.\n\nErro: {ex.Message}",
                "Sigilus", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshLlmModelList()
    {
        var prev = LlmModelCombo.SelectedItem as string;
        LlmModelCombo.Items.Clear();
        foreach (var path in LlmModelResolver.ListGgufs())
            LlmModelCombo.Items.Add(path);

        if (prev is not null && LlmModelCombo.Items.Contains(prev))
            LlmModelCombo.SelectedItem = prev;
        else if (LlmModelCombo.Items.Count > 0)
            LlmModelCombo.SelectedIndex = 0;

        // Status reflete disponibilidade (sem carregar)
        UpdateLlmStatus(canLoad: LlmModelCombo.Items.Count > 0, loaded: _llmDetector is not null);
    }

    private void UpdateLlmStatus(bool canLoad, bool loaded, bool loading = false,
                                 string? errorMessage = null,
                                 string? modelPath = null,
                                 Sigilus.Detection.Llm.ChatTemplate? template = null,
                                 string? loadingNote = null)
    {
        var name = modelPath is null ? null : Path.GetFileName(modelPath);
        if (loading)
        {
            var note = loadingNote is null ? string.Empty : $" — {loadingNote}";
            LlmStatusText.Text = $"Estado: carregando modelo (pode levar 1–3 minutos na primeira vez)… [{name}]{note}";
        }
        else if (errorMessage is not null) LlmStatusText.Text = $"Estado: falha — {errorMessage}";
        else if (loaded) LlmStatusText.Text = $"Estado: ativo. Modelo: {name} · Template: {template ?? Sigilus.Detection.Llm.ChatTemplate.Llama3}";
        else if (canLoad) LlmStatusText.Text = "Estado: modelo .gguf disponível, não carregado. Ative o interruptor para usar.";
        else LlmStatusText.Text = "Estado: nenhum modelo na pasta 'models/llm/'. Veja instruções acima.";

        StatusLlmChip.Opacity = loaded ? 1.0 : 0.4;
    }

    private void UpdateOcrStatus()
    {
        if (_tessdata is null)
        {
            OcrStatusText.Text = "Inativo. Páginas escaneadas serão ignoradas pela detecção.";
            StatusOcrChip.Opacity = 0.4;
        }
        else
        {
            OcrStatusText.Text = $"Ativo. Modelo: {_tessdata}";
            StatusOcrChip.Opacity = 1.0;
        }
    }

    // ===================== ABRIR PDF =====================

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf" };
        if (dlg.ShowDialog(this) != true) return;
        _pdfBytes = File.ReadAllBytes(dlg.FileName);
        _pdfPath = dlg.FileName;

        using (var ms = new MemoryStream(_pdfBytes, writable: false))
        using (var reader = new PdfReader(ms).SetUnethicalReading(true))
        using (var doc = new PdfDocument(reader))
        {
            _pageCount = doc.GetNumberOfPages();
        }
        _pageIndex = 0;
        _detectionsByPage.Clear();
        _overlay.Decisions.Clear();
        ResetProgress();
        _autoFit = true;

        RenderCurrentPage();
        UpdatePageIndicator();
        UpdateNavButtons();
        UpdateDocHeader();

        DetectButton.IsEnabled = true;
        SaveButton.IsEnabled = true;
        StatusText.Text = "Documento aberto. Clique em 'Detectar' para encontrar dados sensíveis.";
    }

    private void UpdateDocHeader()
    {
        if (_pdfPath is null)
        {
            DocTitle.Text = "Nenhum documento aberto";
            DocSubtitle.Text = "Clique em Abrir para começar";
            return;
        }
        DocTitle.Text = Path.GetFileName(_pdfPath);
        var ocrTag = _tessdata is null ? "OCR off" : "OCR";
        var nerTag = _nerProvider is not null ? " · IA" : string.Empty;
        DocSubtitle.Text = $"{_pageCount} {(_pageCount == 1 ? "página" : "páginas")} · {ocrTag}{nerTag}";
    }

    // ===================== RENDER + NAV PÁGINAS DO PDF =====================

    private void RenderCurrentPage()
    {
        if (_pdfBytes is null) return;
        using var ms = new MemoryStream(_pdfBytes, writable: false);
        var png = _renderer.RenderPng(ms, pageIndex: _pageIndex, dpi: RenderDpi);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(png.ToArray());
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        PageImage.Source = bmp;
        PageImage.Width = bmp.PixelWidth;
        PageImage.Height = bmp.PixelHeight;
        _bitmapPixelWidth = bmp.PixelWidth;
        _bitmapPixelHeight = bmp.PixelHeight;

        ms.Position = 0;
        using var reader = new PdfReader(ms).SetUnethicalReading(true);
        using var doc = new PdfDocument(reader);
        var size = doc.GetPage(_pageIndex + 1).GetPageSizeWithRotation();
        _overlay.Width = bmp.PixelWidth;
        _overlay.Height = bmp.PixelHeight;
        _overlay.PageWidthPts = size.GetWidth();
        _overlay.PageHeightPts = size.GetHeight();

        // cache do PageContext da página visível (snap manual)
        Task.Run(() =>
        {
            try
            {
                var classifier = new HeuristicPageClassifier();
                IOcrEngine? ocr = null; IPdfPageRenderer? renderer = null;
                if (_tessdata is not null)
                {
                    ocr = new TesseractEnginePool(_tessdata, 1);
                    renderer = _renderer;
                }
                var extractor = new HybridExtractor(classifier, ocr, renderer);
                using var ms2 = new MemoryStream(_pdfBytes!, writable: false);
                var ctx = extractor.Extract(ms2, _pageIndex, CancellationToken.None);
                (ocr as IDisposable)?.Dispose();
                Dispatcher.Invoke(() => _visiblePageContext = ctx);
            }
            catch { /* silencioso */ }
        });

        PopulateOverlayForVisiblePage();
        if (_autoFit) ApplyFitZoom(); else ApplyZoom(_zoom);
    }

    private void OnPrevPageClick(object sender, RoutedEventArgs e) => GoToPage(_pageIndex - 1);
    private void OnNextPageClick(object sender, RoutedEventArgs e) => GoToPage(_pageIndex + 1);

    private void GoToPage(int newIndex)
    {
        if (_pdfBytes is null) return;
        if (newIndex < 0 || newIndex >= _pageCount) return;
        _pageIndex = newIndex;
        RenderCurrentPage();
        UpdatePageIndicator();
        UpdateNavButtons();
    }

    private void UpdatePageIndicator()
        => PageIndicator.Text = _pageCount == 0 ? "—" : $"pág. {_pageIndex + 1} / {_pageCount}";

    private void UpdateNavButtons()
    {
        PrevButton.IsEnabled = _pdfBytes is not null && _pageIndex > 0;
        NextButton.IsEnabled = _pdfBytes is not null && _pageIndex < _pageCount - 1;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        if (ctrl)
        {
            if (e.Key == Key.OemPlus || e.Key == Key.Add) { OnZoomInClick(this, new()); e.Handled = true; }
            else if (e.Key == Key.OemMinus || e.Key == Key.Subtract) { OnZoomOutClick(this, new()); e.Handled = true; }
            else if (e.Key == Key.D0 || e.Key == Key.NumPad0) { OnZoomResetClick(this, new()); e.Handled = true; }
            else if (e.Key == Key.T) { OnThemeToggleClick(this, new()); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.PageUp: case Key.Left: OnPrevPageClick(this, new()); e.Handled = true; break;
            case Key.PageDown: case Key.Right: OnNextPageClick(this, new()); e.Handled = true; break;
            case Key.Home: GoToPage(0); e.Handled = true; break;
            case Key.End: GoToPage(_pageCount - 1); e.Handled = true; break;
        }
    }

    // ===================== ZOOM =====================

    private void OnFitClick(object sender, RoutedEventArgs e) { _autoFit = true; ApplyFitZoom(); }
    private void OnZoomInClick(object sender, RoutedEventArgs e) { _autoFit = false; ApplyZoom(_zoom * ZoomStep); }
    private void OnZoomOutClick(object sender, RoutedEventArgs e) { _autoFit = false; ApplyZoom(_zoom / ZoomStep); }
    private void OnZoomResetClick(object sender, RoutedEventArgs e) { _autoFit = false; ApplyZoom(1.0); }

    private void OnScrollerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;
        _autoFit = false;
        ApplyZoom(_zoom * (e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep));
        e.Handled = true;
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_autoFit) ApplyFitZoom();
    }

    private void ApplyFitZoom()
    {
        if (_bitmapPixelWidth <= 0 || _bitmapPixelHeight <= 0) return;
        var viewW = PageScroller.ViewportWidth;
        var viewH = PageScroller.ViewportHeight;
        if (viewW <= 0 || viewH <= 0) return;
        var pad = 16.0;
        var scale = Math.Min((viewW - pad) / _bitmapPixelWidth, (viewH - pad) / _bitmapPixelHeight);
        if (scale <= 0 || double.IsInfinity(scale) || double.IsNaN(scale)) return;
        ApplyZoom(scale);
    }

    private void ApplyZoom(double z)
    {
        _zoom = Math.Clamp(z, MinZoom, MaxZoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        ZoomIndicator.Text = $"{_zoom * 100:F0}%";
    }

    // ===================== DETECÇÃO =====================

    private async void OnDetectClick(object sender, RoutedEventArgs e)
    {
        if (_pdfBytes is null) return;
        if (_detectCts is not null) return;

        var pdfSnapshot = _pdfBytes;
        var pageCount = _pageCount;
        var tessdata = _tessdata;
        var ner = _nerProvider;
        var llm = _llmDetector;

        _detectionsByPage.Clear();
        _overlay.Decisions.Clear();
        _lastLlmError = null;
        ResetProgress();
        DetectProgress.Visibility = Visibility.Visible;

        DetectButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        OpenButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        _detectCts = new CancellationTokenSource();
        var ct = _detectCts.Token;

        var workerCount = Math.Max(2, Environment.ProcessorCount - 1);
        TesseractEnginePool? ocrPool = tessdata is null
            ? null : new TesseractEnginePool(tessdata, maxConcurrency: workerCount);

        Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("detect",
            $"INÍCIO | pages={pageCount} | workers={workerCount} | " +
            $"ocr={(ocrPool is not null)} | ner={(ner is not null)} | llm={(llm is not null ? llm.ModelName : "off")}");

        var progress = new Progress<DetectProgressReport>(OnProgress);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var totalEntities = 0;
        var fatalError = string.Empty;
        var wasCancelled = false;

        try
        {
            totalEntities = await Task.Run(() => RunDetectionAsync(
                pdfSnapshot, pageCount, ocrPool, ner, llm, workerCount, progress, ct), ct);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("detect", "CANCELADO pelo usuário");
        }
        catch (Exception ex)
        {
            fatalError = ExtractRootMessage(ex);
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Error("detect", "Pipeline ABORTOU com exceção não tratada", ex);
        }
        finally
        {
            try { ocrPool?.Dispose(); } catch { }
            try { _detectCts?.Dispose(); } catch { }
            _detectCts = null;
            DetectButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            OpenButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            DetectProgress.Visibility = Visibility.Collapsed;
        }

        sw.Stop();
        PopulateOverlayForVisiblePage();

        // Sumário ao terminar — sempre logado, sempre visível.
        var pagesWithHits = _detectionsByPage.Count;
        var summary =
            $"Concluído em {sw.Elapsed.TotalSeconds:F1}s | " +
            $"páginas com detecções: {pagesWithHits}/{pageCount} | " +
            $"total de entidades: {totalEntities}";
        Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("detect", "FIM | " + summary +
            (wasCancelled ? " | CANCELADO" : "") +
            (fatalError.Length > 0 ? $" | ERRO FATAL: {fatalError}" : "") +
            (_lastLlmError is not null ? $" | LLM: {_lastLlmError}" : ""));

        // Status visível na UI — prioriza erros.
        if (fatalError.Length > 0)
        {
            StatusText.Text = $"❌ Erro: {fatalError} (ver Sigilus-log.txt)";
            MessageBox.Show(this,
                "A análise foi interrompida por um erro inesperado.\n\n" +
                $"Erro: {fatalError}\n\n" +
                $"Detalhes técnicos foram gravados em:\n{Sigilus.Ui.Wpf.Diagnostics.AppLog.LogPath}\n\n" +
                $"O que foi processado antes do erro continua válido na tela " +
                $"({totalEntities} detecções em {pagesWithHits} páginas).",
                "Sigilus — erro na análise", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else if (wasCancelled)
        {
            StatusText.Text = $"⏹ Cancelado — {totalEntities} detecções em {pagesWithHits} pág. (parcial).";
        }
        else
        {
            var errSuffix = _lastLlmError is null ? string.Empty : $" · ⚠ {_lastLlmError}";
            StatusText.Text = $"✓ {totalEntities} detecções em {pageCount} pág. ({pagesWithHits} com hits) em {sw.Elapsed.TotalSeconds:F1}s · Pág. {_pageIndex + 1}: {_overlay.Decisions.Count} marcações.{errSuffix}";
        }
    }

    private async Task<int> RunDetectionAsync(
        byte[] pdfBytes, int pageCount,
        TesseractEnginePool? ocrPool, INerProvider? ner,
        LlmEntityDetector? llm,
        int workerCount, IProgress<DetectProgressReport> progress, CancellationToken ct)
    {
        var classifier = new HeuristicPageClassifier();
        var renderer = new PdfiumPageRenderer();
        var rules = BrazilianRegexRules.Default;

        var done = 0;
        var totalEntities = 0;

        var parallelOpts = new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = ct,
        };

        var phase1Errors = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, pageCount), parallelOpts, async (p, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var pageSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var ms = new MemoryStream(pdfBytes, writable: false);
                var extractor = new HybridExtractor(classifier, ocrPool, ocrPool is null ? null : renderer);
                var ctx = extractor.Extract(ms, p, innerCt);

                var detectors = new List<IEntityDetector> { new RegexEntityDetector(rules) };
                if (ner is not null) detectors.Add(new NerEntityDetector(ner));
                var composite = new CompositeEntityDetector(detectors);
                var filtered = new PublicEntityFilterDetector(composite);
                var detector = new SnappingEntityDetector(filtered);

                var list = new List<DetectedEntity>();
                await foreach (var ent in detector.DetectAsync(ctx, innerCt).WithCancellation(innerCt))
                    list.Add(ent);

                if (list.Count > 0)
                {
                    _detectionsByPage[p] = list;
                    Interlocked.Add(ref totalEntities, list.Count);
                }
                pageSw.Stop();
                Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("detect-p1",
                    $"pag {p + 1,4}/{pageCount} | {pageSw.ElapsedMilliseconds,5} ms | {list.Count,3} hits | cls={ctx.Classification}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref phase1Errors);
                Sigilus.Ui.Wpf.Diagnostics.AppLog.Error("detect-p1",
                    $"pag {p + 1}/{pageCount} FALHOU após {pageSw.ElapsedMilliseconds} ms", ex);
            }
            finally
            {
                var current = Interlocked.Increment(ref done);
                progress.Report(new DetectProgressReport(current, pageCount, totalEntities));
            }
        });
        Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("detect-p1",
            $"Fase 1 (regex+NER) terminada | falhas: {phase1Errors}/{pageCount}");

        // Fase 2: LLM sequencial (lento, single-thread).
        if (llm is not null)
        {
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("detect-p2",
                $"Fase 2 (LLM '{llm.ModelName}') iniciando — {pageCount} páginas, sequencial");
            done = 0;
            var llmErrors = 0;
            Exception? lastErr = null;
            for (var p = 0; p < pageCount; p++)
            {
                ct.ThrowIfCancellationRequested();
                var pageSw = System.Diagnostics.Stopwatch.StartNew();
                var added = 0;
                try
                {
                    using var ms = new MemoryStream(pdfBytes, writable: false);
                    var extractor = new HybridExtractor(classifier, ocrPool, ocrPool is null ? null : renderer);
                    var ctx = extractor.Extract(ms, p, ct);

                    var llmChain = new SnappingEntityDetector(
                        new PublicEntityFilterDetector(llm));

                    var existing = _detectionsByPage.TryGetValue(p, out var prev) ? prev : new List<DetectedEntity>();
                    var newOnes = new List<DetectedEntity>();
                    await foreach (var ent in llmChain.DetectAsync(ctx, ct).WithCancellation(ct))
                        newOnes.Add(ent);

                    if (newOnes.Count > 0)
                    {
                        var merged = new List<DetectedEntity>(existing);
                        foreach (var ne in newOnes)
                        {
                            if (existing.Any(o => o.PageIndex == ne.PageIndex && IouRect(o.Bounds, ne.Bounds) > 0.5f))
                                continue;
                            merged.Add(ne);
                        }
                        added = merged.Count - existing.Count;
                        _detectionsByPage[p] = merged;
                        Interlocked.Add(ref totalEntities, added);
                    }
                    pageSw.Stop();
                    Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("detect-p2",
                        $"pag {p + 1,4}/{pageCount} | {pageSw.Elapsed.TotalSeconds,6:F1} s | +{added,3} novos | LLM extraiu {newOnes.Count} candidatos");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    llmErrors++;
                    lastErr = ex;
                    Sigilus.Ui.Wpf.Diagnostics.AppLog.Error("detect-p2",
                        $"pag {p + 1}/{pageCount} FALHOU após {pageSw.Elapsed.TotalSeconds:F1} s", ex);
                }
                finally
                {
                    done++;
                    progress.Report(new DetectProgressReport(done, pageCount, totalEntities, isLlm: true, llmErrors: llmErrors));
                }
            }
            Sigilus.Ui.Wpf.Diagnostics.AppLog.Info("detect-p2",
                $"Fase 2 terminada | falhas: {llmErrors}/{pageCount}");
            if (llmErrors > 0 && lastErr is not null)
            {
                _lastLlmError = $"{llmErrors} pág. falharam no LLM (último: {ExtractRootMessage(lastErr)})";
            }
        }

        return totalEntities;
    }

    private static float IouRect(PdfRect a, PdfRect b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        var inter = ix * iy;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    private void OnProgress(DetectProgressReport r)
    {
        var pct = r.Total == 0 ? 0 : 100.0 * r.Completed / r.Total;
        DetectProgress.Value = pct;
        var fase = r.isLlm ? "IA inteligente" : "Analisando";
        var errTxt = r.llmErrors > 0 ? $" · {r.llmErrors} falhas" : string.Empty;
        StatusText.Text = $"{fase}… {r.Completed} de {r.Total} págs · {r.EntitiesSoFar} detecções{errTxt} · {pct:F0}%";
    }

    private void ResetProgress()
    {
        DetectProgress.Value = 0;
        StatusText.Text = "Pronto";
    }

    private void PopulateOverlayForVisiblePage()
    {
        _overlay.Decisions.Clear();
        if (!_detectionsByPage.TryGetValue(_pageIndex, out var entities)) return;
        foreach (var ent in entities)
        {
            _overlay.Decisions.Add(new RedactionDecision(
                Bounds: ent.Bounds,
                PageIndex: ent.PageIndex,
                Approved: ent.Confidence >= 0.85f,
                Reason: $"{ent.Type} ({ent.Confidence:F2})",
                Origin: ent));
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _detectCts?.Cancel();

    // ===================== SNAP =====================

    private PdfRect SnapManualRect(PdfRect proposed)
    {
        var ctx = _visiblePageContext;
        if (ctx is null) return proposed;
        if (_detectionsByPage.TryGetValue(_pageIndex, out var ents))
        {
            DetectedEntity? best = null;
            float bestIou = 0;
            foreach (var ent in ents)
            {
                var iou = Iou(proposed, ent.Bounds);
                if (iou > bestIou) { bestIou = iou; best = ent; }
            }
            if (best is not null && bestIou > 0.1f) return best.Bounds;
        }
        return new WordSnapper(ctx).Snap(proposed);
    }

    private static float Iou(PdfRect a, PdfRect b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X));
        var iy = Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Y, b.Y));
        var inter = ix * iy;
        var union = a.Width * a.Height + b.Width * b.Height - inter;
        return union <= 0 ? 0 : inter / union;
    }

    // ===================== APLICAR & SALVAR =====================

    private async void OnRedactClick(object sender, RoutedEventArgs e)
    {
        if (_pdfBytes is null) return;
        var dlg = new SaveFileDialog { Filter = "PDF (*.pdf)|*.pdf", FileName = "redigido.pdf" };
        if (dlg.ShowDialog(this) != true) return;

        var all = new List<RedactionDecision>();
        foreach (var kv in _detectionsByPage)
            foreach (var ent in kv.Value)
                all.Add(new RedactionDecision(ent.Bounds, ent.PageIndex,
                    Approved: ent.Confidence >= 0.85f,
                    Reason: $"{ent.Type} ({ent.Confidence:F2})", Origin: ent));
        foreach (var d in _overlay.Decisions)
            if (d.Origin is null && d.Approved) all.Add(d);

        var pseudonymize = PseudonymizeToggle.IsOn;
        StatusText.Text = $"{(pseudonymize ? "Pseudonimizando" : "Redigindo")} {all.Count(d => d.Approved)} marcações…";
        SaveButton.IsEnabled = false;

        try
        {
            await Task.Run(async () =>
            {
                using var inMs = new MemoryStream(_pdfBytes, writable: false);
                await using var outFs = File.Create(dlg.FileName);
                if (pseudonymize)
                {
                    var pseudo = new BrazilianPseudonymizer();
                    var ctx = new PseudonymContext();   // único contexto compartilhado
                    var engine = new PseudonymizationRedactionEngine(
                        pseudo,
                        ctx,
                        new SkiaPseudonymizingImageRedactor(pseudo, ctx),
                        new ItextMetadataScrubber());
                    await engine.RedactAsync(inMs, outFs, all, CancellationToken.None);
                }
                else
                {
                    var engine = new PdfSweepRedactionEngine(new SkiaImageRedactor(), new ItextMetadataScrubber());
                    await engine.RedactAsync(inMs, outFs, all, CancellationToken.None);
                }
            });
            StatusText.Text = $"Salvo em {dlg.FileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro ao salvar: {ex.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private readonly record struct DetectProgressReport(int Completed, int Total, int EntitiesSoFar, bool isLlm = false, int llmErrors = 0);
}
