using System.Text;

namespace BaudRunner;

/// <summary>
/// Last-resort diagnostics. Without this an exception on the UI thread, inside an
/// async void handler, or in a discarded Task takes the process down leaving the
/// user nothing to report.
/// </summary>
public static class CrashLog
{
    private static readonly object _gate = new();

    public static string Path => System.IO.Path.Combine(AppConfig.Directory, "errors.log");

    public static void Report(string origin, Exception? exception, bool fatal)
    {
        if (exception is null) return;
        try
        {
            Directory.CreateDirectory(AppConfig.Directory);
            var text = new StringBuilder()
                .AppendLine()
                .AppendLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {origin}{(fatal ? " (fatal)" : "")} ====")
                .AppendLine(exception.ToString())
                .ToString();
            lock (_gate) File.AppendAllText(Path, text);
        }
        catch (Exception) { }
    }
}
