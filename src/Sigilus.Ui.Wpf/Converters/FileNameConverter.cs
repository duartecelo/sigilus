using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace Sigilus.Ui.Wpf.Converters;

/// <summary>
/// Mostra só o nome do arquivo (sem diretório) a partir de um caminho
/// completo. Usado pelo ComboBox de modelos LLM para exibir
/// "gemma-3-4b-it-Q4_K_M.gguf" em vez do caminho inteiro.
/// </summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrEmpty(s) ? Path.GetFileName(s) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
