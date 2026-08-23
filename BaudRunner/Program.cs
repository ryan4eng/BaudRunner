using Avalonia;

namespace BaudRunner;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Dispatcher.UIThread must not be touched here: creating it before Avalonia
        // sets up a platform leaves it without one and Dispatcher.MainLoop then throws
        // PlatformNotSupportedException. That handler is installed in App instead.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Report("unhandled", e.ExceptionObject as Exception, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { CrashLog.Report("unobserved task", e.Exception, fatal: false); e.SetObserved(); };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Report("startup", ex, fatal: true);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
