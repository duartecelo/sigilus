using System.IO;
using System.Text;

namespace Sigilus.Ui.Wpf.Diagnostics;

/// <summary>
/// Log simples em arquivo texto ao lado do executável.
/// Usado para diagnosticar problemas em máquinas de usuário final
/// (quando o popup não basta). Append-only; um arquivo por dia.
/// </summary>
public static class AppLog
{
    private static readonly object _gate = new();
    private static readonly string _logPath = ResolvePath();

    public static string LogPath => _logPath;

    /// <summary>Log informativo.</summary>
    public static void Info(string component, string message) => Write("INFO", component, message);

    /// <summary>Log de aviso.</summary>
    public static void Warn(string component, string message) => Write("WARN", component, message);

    /// <summary>Log de erro com stack trace completa.</summary>
    public static void Error(string component, string message, Exception? ex = null)
    {
        var full = message;
        if (ex is not null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(message);
            var cur = ex;
            var depth = 0;
            while (cur is not null && depth < 10)
            {
                sb.Append(' ', depth * 2);
                sb.AppendLine($"→ {cur.GetType().FullName}: {cur.Message}");
                if (cur.StackTrace is not null)
                {
                    foreach (var line in cur.StackTrace.Split('\n'))
                    {
                        sb.Append(' ', depth * 2 + 2);
                        sb.AppendLine(line.Trim());
                    }
                }
                cur = cur.InnerException;
                depth++;
            }
            full = sb.ToString();
        }
        Write("ERR ", component, full);
    }

    private static void Write(string level, string component, string message)
    {
        try
        {
            lock (_gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{component}] {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line);
            }
        }
        catch { /* nunca lançar do logger */ }
    }

    private static string ResolvePath()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            return Path.Combine(dir, "Sigilus-log.txt");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "Sigilus-log.txt");
        }
    }

    /// <summary>Snapshot de ambiente (chamar no início do app).</summary>
    public static string EnvironmentReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("════════ Sigilus iniciado ════════");
        sb.AppendLine($"Versão app:       {typeof(AppLog).Assembly.GetName().Version}");
        sb.AppendLine($"Diretório:        {AppContext.BaseDirectory}");
        sb.AppendLine($"OS:               {Environment.OSVersion}");
        sb.AppendLine($"64-bit OS:        {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"64-bit Process:   {Environment.Is64BitProcess}");
        sb.AppendLine($"CPU cores:        {Environment.ProcessorCount}");
        sb.AppendLine($"CPU arch:         {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"CPU AVX:          {System.Runtime.Intrinsics.X86.Avx.IsSupported}");
        sb.AppendLine($"CPU AVX2:         {System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
        sb.AppendLine($"CPU AVX-512 F:    {System.Runtime.Intrinsics.X86.Avx512F.IsSupported}");
        sb.AppendLine($"Working memory:   {Environment.WorkingSet / 1024 / 1024} MB");
        sb.AppendLine($"GC mem:           {GC.GetTotalMemory(false) / 1024 / 1024} MB");

        // RAM total via API nativa do Windows. Essencial para diagnóstico
        // remoto: o LLM precisa de RAM equivalente ao tamanho do GGUF.
        try
        {
            var ms = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(ms))
            {
                sb.AppendLine($"RAM total:        {ms.ullTotalPhys / 1024 / 1024} MB");
                sb.AppendLine($"RAM disponível:   {ms.ullAvailPhys / 1024 / 1024} MB");
            }
        }
        catch { /* não-Windows: ignora */ }

        return sb.ToString();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX lpBuffer);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX() { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
    }
}
