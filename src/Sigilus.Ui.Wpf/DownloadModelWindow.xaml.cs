using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Sigilus.Detection.Llm;
using Sigilus.Ui.Wpf.Diagnostics;

namespace Sigilus.Ui.Wpf;

/// <summary>
/// Janela modal para baixar modelos LLM diretamente do Hugging Face.
/// Sem dependências externas — usa <see cref="LlmModelDownloader"/> com
/// HttpClient puro.
/// </summary>
public partial class DownloadModelWindow : Window
{
    private CancellationTokenSource? _cts;
    private readonly string _destDir;

    /// <summary>Caminho do último GGUF baixado com sucesso (null se nenhum).</summary>
    public string? DownloadedPath { get; private set; }

    public DownloadModelWindow(string destDir)
    {
        InitializeComponent();
        _destDir = destDir;
        Directory.CreateDirectory(_destDir);

        // Popula a lista (encapsula `LlmModelInfo` num view-model com tamanho human + status).
        var items = LlmModelCatalog.Available
            .Select(m => new ModelRow(m, IsInstalled(m), _destDir))
            .ToList();
        ModelList.ItemsSource = items;
        if (items.Count > 0) ModelList.SelectedIndex = 0;
    }

    private bool IsInstalled(LlmModelInfo m)
    {
        var path = Path.Combine(_destDir, m.FileName);
        return File.Exists(path) && new FileInfo(path).Length == m.SizeBytes;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        DownloadButton.IsEnabled = ModelList.SelectedItem is ModelRow row && !row.IsInstalled;
        if (ModelList.SelectedItem is ModelRow r)
        {
            ProgressTitle.Text = r.IsInstalled
                ? $"{r.DisplayName} — já está instalado em {_destDir}."
                : $"Pronto para baixar {r.DisplayName} ({r.SizeHuman}).";
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelRow row) return;

        DownloadButton.IsEnabled = false;
        ModelList.IsEnabled = false;
        CancelButton.Content = "Cancelar";
        _cts = new CancellationTokenSource();

        var progress = new Progress<DownloadProgress>(OnProgress);
        using var downloader = new LlmModelDownloader();

        try
        {
            AppLog.Info("download", $"Iniciando download: {row.Model.FileName} de {row.Model.Url}");
            var path = await Task.Run(() =>
                downloader.DownloadAsync(row.Model, _destDir, progress, _cts.Token));

            AppLog.Info("download", $"OK: {path}");
            DownloadedPath = path;
            ProgressTitle.Text = $"✓ Download concluído: {row.Model.FileName}";
            ProgressBar.Value = 100;
            ProgressEta.Text = "";

            // Atualiza estado visual: marca como instalado.
            var items = LlmModelCatalog.Available
                .Select(m => new ModelRow(m, IsInstalled(m), _destDir)).ToList();
            ModelList.ItemsSource = items;

            MessageBox.Show(this,
                $"O modelo foi baixado com sucesso para:\n{path}\n\n" +
                "Agora você pode voltar para as Configurações do Sigilus e ativar a IA inteligente — " +
                "o modelo recém-baixado aparecerá na lista de modelos.",
                "Sigilus — download concluído", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("download", $"Cancelado pelo usuário: {row.Model.FileName}");
            ProgressTitle.Text = "Download cancelado. Você pode tentar de novo — o arquivo parcial é reaproveitado.";
            ProgressEta.Text = "";
        }
        catch (HttpRequestException ex)
        {
            AppLog.Error("download", $"Falha de rede: {row.Model.FileName}", ex);
            ProgressTitle.Text = "Erro de conexão.";
            MessageBox.Show(this,
                $"Não foi possível baixar o modelo.\n\nErro: {ex.Message}\n\n" +
                "Verifique sua conexão com a internet e tente novamente. " +
                "Se já havia parte do arquivo baixada, ela foi mantida — o próximo download continua de onde parou.",
                "Sigilus — falha no download", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLog.Error("download", $"Erro inesperado: {row.Model.FileName}", ex);
            ProgressTitle.Text = $"Erro: {ex.Message}";
            MessageBox.Show(this,
                $"Erro inesperado durante o download:\n\n{ex.Message}\n\n" +
                $"Detalhes técnicos em {AppLog.LogPath}",
                "Sigilus — erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            ModelList.IsEnabled = true;
            DownloadButton.IsEnabled = ModelList.SelectedItem is ModelRow r && !r.IsInstalled;
            CancelButton.Content = "Fechar";
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        ProgressTitle.Text = $"Baixando {p.Model.FileName} — {p.ReceivedMb} de {p.TotalMb} ({p.Percent:F1}%) · {p.SpeedHuman}";
        ProgressEta.Text = p.Eta == "—" ? string.Empty : $"restam {p.Eta}";
        ProgressBar.Value = p.Percent;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }
        Close();
    }

    /// <summary>View-model do ListBox.</summary>
    public sealed class ModelRow
    {
        public LlmModelInfo Model { get; }
        public bool IsInstalled { get; }
        public string DisplayName { get; }
        public string Description => Model.Description;
        public string SizeHuman { get; }

        public ModelRow(LlmModelInfo model, bool installed, string destDir)
        {
            Model = model;
            IsInstalled = installed;
            SizeHuman = $"{model.SizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
            DisplayName = installed ? $"✓ {model.DisplayName} (instalado)" : model.DisplayName;
        }
    }
}
