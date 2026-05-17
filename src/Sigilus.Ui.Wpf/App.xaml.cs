using System.Windows;
using System.Windows.Threading;
using Sigilus.Ui.Wpf.Diagnostics;

namespace Sigilus.Ui.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Captura QUALQUER exceção não tratada — incluindo crashes do
        // llama.cpp / SkiaSharp / iText. Sem isso o processo morria
        // silenciosamente em alguns casos.
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            AppLog.Error("appdomain", $"UnhandledException (terminating={args.IsTerminating})", ex);
            if (ex is not null)
            {
                try
                {
                    MessageBox.Show(
                        "Ocorreu um erro fatal não esperado.\n\n" +
                        $"Tipo: {ex.GetType().Name}\n" +
                        $"Mensagem: {ex.Message}\n\n" +
                        $"Detalhes em: {AppLog.LogPath}",
                        "Sigilus — erro fatal",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            }
        };

        // Exceções vindas de await sem await aguardado (fire-and-forget).
        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            AppLog.Error("task-scheduler", "UnobservedTaskException", args.Exception);
            args.SetObserved();   // evita crash do processo
        };

        // Exceções na UI thread do WPF.
        DispatcherUnhandledException += (s, args) =>
        {
            AppLog.Error("dispatcher", "DispatcherUnhandledException", args.Exception);
            try
            {
                MessageBox.Show(
                    "Erro inesperado na interface.\n\n" +
                    $"Tipo: {args.Exception.GetType().Name}\n" +
                    $"Mensagem: {args.Exception.Message}\n\n" +
                    $"Detalhes em: {AppLog.LogPath}\n\n" +
                    "O aplicativo vai tentar continuar.",
                    "Sigilus — erro",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;   // não derruba o app
            }
            catch { }
        };

        base.OnStartup(e);
    }
}
