using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace BaudRunner;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Safe here: the dispatcher already has its platform implementation. An
        // unhandled UI-thread exception is logged and swallowed so one bad event
        // handler cannot close the window with an open capture in progress.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Report("UI thread", e.Exception, fatal: false);
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
