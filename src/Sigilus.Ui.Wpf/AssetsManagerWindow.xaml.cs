using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Sigilus.Core;
using Sigilus.Detection.Llm;
using Sigilus.Detection.Onnx;
using Sigilus.Ocr;
using Sigilus.Ui.Wpf.Diagnostics;

namespace Sigilus.Ui.Wpf;

/// <summary>
/// Janela única para gerenciar todos os componentes baixáveis: OCR
/// (tessdata), NER (LeNER-Br) e LLM (Gemma/Qwen/etc).
/// </summary>
public partial class AssetsManagerWindow : Window
{
    private CancellationTokenSource? _cts;
    private readonly string _appDir;
    public bool AnythingDownloaded { get; private set; }

    public AssetsManagerWindow(string appDir)
    {
        InitializeComponent();
        _appDir = appDir;
        Directory.CreateDirectory(Path.Combine(_appDir, "tessdata"));
        Directory.CreateDirectory(Path.Combine(_appDir, "models"));
        Directory.CreateDirectory(Path.Combine(_appDir, "models", "llm"));

        OcrList.ItemsSource = TessdataCatalog.Available
            .Select(a => new OcrRow(a, IsTessdataInstalled(a))).ToList();
        OcrList.SelectedIndex = 0;

        NerList.ItemsSource = NerModelCatalog.Available
            .Select(a => new NerRow(a, IsNerInstalled(a))).ToList();
        NerList.SelectedIndex = 0;

        LlmList.ItemsSource = LlmModelCatalog.Available
            .Select(m => new LlmRow(m, IsLlmInstalled(m))).ToList();
        LlmList.SelectedIndex = 0;
    }

    // -------- detecções de instalação --------
    private bool IsTessdataInstalled(TessdataAsset a)
    {
        var p = Path.Combine(_appDir, "tessdata", a.FileName);
        return File.Exists(p) && new FileInfo(p).Length > 100_000;
    }
    private bool IsNerInstalled(NerModelAsset _)
    {
        var dir = Path.Combine(_appDir, "models");
        return File.Exists(Path.Combine(dir, "ner-ptbr.onnx"))
            && File.Exists(Path.Combine(dir, "vocab.txt"))
            && File.Exists(Path.Combine(dir, "labels.json"));
    }
    private bool IsLlmInstalled(LlmModelInfo m)
    {
        var p = Path.Combine(_appDir, "models", "llm", m.FileName);
        return File.Exists(p) && new FileInfo(p).Length == m.SizeBytes;
    }

    // -------- selection changed --------
    private void OnOcrSelectionChanged(object s, SelectionChangedEventArgs e) => UpdateButton();
    private void OnNerSelectionChanged(object s, SelectionChangedEventArgs e) => UpdateButton();
    private void OnLlmSelectionChanged(object s, SelectionChangedEventArgs e) => UpdateButton();

    private void UpdateButton()
    {
        DownloadButton.IsEnabled = GetActiveRow() is { IsInstalled: false };
        if (GetActiveRow() is { } row)
            ProgressTitle.Text = row.IsInstalled
                ? $"{row.DisplayName} — já instalado."
                : $"Pronto para baixar: {row.DisplayName} ({row.SizeHuman}).";
    }

    private AssetRowBase? GetActiveRow()
    {
        return Pivot.SelectedIndex switch
        {
            0 => OcrList.SelectedItem as AssetRowBase,
            1 => NerList.SelectedItem as AssetRowBase,
            2 => LlmList.SelectedItem as AssetRowBase,
            _ => null,
        };
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var row = GetActiveRow();
        if (row is null) return;

        DownloadButton.IsEnabled = false;
        OcrList.IsEnabled = NerList.IsEnabled = LlmList.IsEnabled = false;
        CancelButton.Content = "Cancelar";
        _cts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(OnProgress);

        try
        {
            AppLog.Info("download", $"Iniciando: {row.DisplayName}");
            switch (row)
            {
                case OcrRow ocr:
                    await Task.Run(() => new TessdataDownloader().DownloadAsync(
                        ocr.Asset, Path.Combine(_appDir, "tessdata"), progress, _cts.Token));
                    break;
                case NerRow ner:
                    await Task.Run(() => new NerModelDownloader().DownloadAsync(
                        ner.Asset, Path.Combine(_appDir, "models"), progress, _cts.Token));
                    break;
                case LlmRow llm:
                    using (var dl = new LlmModelDownloader())
                        await Task.Run(() => dl.DownloadAsync(
                            llm.Model, Path.Combine(_appDir, "models", "llm"), progress, _cts.Token));
                    break;
            }
            AppLog.Info("download", $"OK: {row.DisplayName}");
            AnythingDownloaded = true;
            ProgressTitle.Text = $"✓ {row.DisplayName} baixado com sucesso.";
            ProgressEta.Text = "";
            ProgressBar.Value = 100;
            // Recarrega listas (item agora marcado como instalado).
            RefreshLists();
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("download", $"Cancelado: {row.DisplayName}");
            ProgressTitle.Text = "Download cancelado. Tentar de novo retoma de onde parou.";
            ProgressEta.Text = "";
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("download", $"Falha rede: {row.DisplayName}", ex);
            ShowError(row.DisplayName, ex,
                "Verifique sua conexão. Se já havia parte baixada, ela é preservada.");
        }
        catch (Exception ex)
        {
            AppLog.Error("download", $"Erro: {row.DisplayName}", ex);
            ShowError(row.DisplayName, ex,
                "Para o NER: se você está usando um repositório novo, configure a variável " +
                "de ambiente SIGILUS_NER_BASE_URL ou edite NerModelCatalog.cs com a URL correta.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            OcrList.IsEnabled = NerList.IsEnabled = LlmList.IsEnabled = true;
            UpdateButton();
            CancelButton.Content = "Fechar";
        }
    }

    private void RefreshLists()
    {
        OcrList.ItemsSource = TessdataCatalog.Available
            .Select(a => new OcrRow(a, IsTessdataInstalled(a))).ToList();
        NerList.ItemsSource = NerModelCatalog.Available
            .Select(a => new NerRow(a, IsNerInstalled(a))).ToList();
        LlmList.ItemsSource = LlmModelCatalog.Available
            .Select(m => new LlmRow(m, IsLlmInstalled(m))).ToList();
    }

    private void ShowError(string item, Exception ex, string tip)
    {
        ProgressTitle.Text = $"Erro: {ex.Message}";
        MessageBox.Show(this,
            $"Não foi possível baixar {item}.\n\n" +
            $"Erro: {ex.Message}\n\n{tip}\n\n" +
            $"Detalhes em {AppLog.LogPath}",
            "Sigilus — erro no download", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnProgress(DownloadProgress p)
    {
        ProgressTitle.Text = $"Baixando {p.ItemName} — {p.ReceivedMb} de {p.TotalMb} ({p.Percent:F1}%) · {p.SpeedHuman}";
        ProgressEta.Text = p.Eta == "—" ? "" : $"restam {p.Eta}";
        ProgressBar.Value = p.Percent;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); return; }
        Close();
    }

    // -------- view models --------
    public abstract class AssetRowBase
    {
        public bool IsInstalled { get; protected set; }
        public string DisplayName { get; protected set; } = "";
        public string Description { get; protected set; } = "";
        public string SizeHuman { get; protected set; } = "";
    }

    public sealed class OcrRow : AssetRowBase
    {
        public TessdataAsset Asset { get; }
        public OcrRow(TessdataAsset a, bool installed)
        {
            Asset = a;
            IsInstalled = installed;
            DisplayName = installed ? $"✓ {a.DisplayName} (instalado)" : a.DisplayName;
            Description = a.Description;
            SizeHuman = FormatBytes(a.SizeBytes);
        }
    }

    public sealed class NerRow : AssetRowBase
    {
        public NerModelAsset Asset { get; }
        public NerRow(NerModelAsset a, bool installed)
        {
            Asset = a;
            IsInstalled = installed;
            DisplayName = installed ? $"✓ {a.DisplayName} (instalado)" : a.DisplayName;
            Description = a.Description;
            SizeHuman = FormatBytes(a.ModelSizeBytes);
        }
    }

    public sealed class LlmRow : AssetRowBase
    {
        public LlmModelInfo Model { get; }
        public LlmRow(LlmModelInfo m, bool installed)
        {
            Model = m;
            IsInstalled = installed;
            DisplayName = installed ? $"✓ {m.DisplayName} (instalado)" : m.DisplayName;
            Description = m.Description;
            SizeHuman = FormatBytes(m.SizeBytes);
        }
    }

    private static string FormatBytes(long b)
    {
        return b switch
        {
            >= 1_073_741_824 => $"{b / 1024.0 / 1024.0 / 1024.0:F2} GB",
            >= 1_048_576 => $"{b / 1024.0 / 1024.0:F1} MB",
            _ => $"{b / 1024.0:F1} KB",
        };
    }
}
