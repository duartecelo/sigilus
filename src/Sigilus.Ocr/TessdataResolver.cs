namespace Sigilus.Ocr;

/// <summary>
/// Tenta localizar uma pasta <c>tessdata</c> com <c>por.traineddata</c>.
/// Procura, em ordem:
/// <list type="number">
///   <item>variável de ambiente <c>TESSDATA_PREFIX</c>;</item>
///   <item><c>./tessdata</c> ao lado do executável;</item>
///   <item>subindo até 5 níveis a partir do executável (útil em dev/teste).</item>
/// </list>
/// Devolve <c>null</c> se não achar — o chamador decide se segue sem OCR.
/// </summary>
public static class TessdataResolver
{
    private const string Required = "por.traineddata";

    public static string? FindTessdata()
    {
        var env = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrWhiteSpace(env) && HasRequired(env)) return env;

        var baseDir = AppContext.BaseDirectory;
        var local = Path.Combine(baseDir, "tessdata");
        if (HasRequired(local)) return local;

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tessdata");
            if (HasRequired(candidate)) return candidate;
        }
        return null;
    }

    private static bool HasRequired(string dir)
        => !string.IsNullOrWhiteSpace(dir)
           && Directory.Exists(dir)
           && File.Exists(Path.Combine(dir, Required));
}
