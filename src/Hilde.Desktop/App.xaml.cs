using System.Windows;
using System.Windows.Threading;
using Hilde.Core;
using Serilog;

namespace Hilde.Desktop;

public partial class App : Application
{
    private AppHost? _host;
    private SingleInstance? _single;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = new HildePaths();
        paths.EnsureCreated();
        Logging.Configure(paths);

        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            _single.SignalFirst();
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Fatal(a.ExceptionObject as Exception, "Unbehandelter Fehler");
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error(a.Exception, "Unbeobachtete Task-Ausnahme"); a.SetObserved(); };

        try
        {
            _host = new AppHost(paths, Dispatcher);
            await _host.StartAsync(startInTray: e.Args.Contains("--tray"));
            _single.ActivationRequested += () => Dispatcher.InvokeAsync(() => _host?.ShowMainWindow());
            _single.Listen();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Start fehlgeschlagen");
            MessageBox.Show("HILDE konnte nicht gestartet werden.\n\n" + ex.Message +
                            "\n\nDetails stehen im Protokoll unter " + paths.Logs,
                "HILDE", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // UI-Fehler sollen HILDE nicht beenden; Diktate laufen unabhängig weiter.
        Log.Error(e.Exception, "{ErrorId} UI-Fehler", HildeErrors.Unexpected.Id);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.DisposeUi();
            var host = _host;
            if (host != null) Task.Run(() => host.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex) { Log.Warning(ex, "Beenden"); }
        _single?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
