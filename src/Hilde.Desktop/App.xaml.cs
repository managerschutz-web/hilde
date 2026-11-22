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

        bool tray = e.Args.Contains("--tray");
        SplashWindow? splash = null;
        if (!tray)
        {
            splash = new SplashWindow();
            splash.Show();
        }
        try
        {
            _host = new AppHost(paths, Dispatcher);
            await _host.StartAsync(startInTray: tray, beforeShow: splash == null ? null : () => splash.WaitAndCloseAsync(TimeSpan.FromSeconds(15)));
            _single.ActivationRequested += () => Dispatcher.InvokeAsync(() => _host?.ShowMainWindow());
            _single.Listen();
        }
        catch (Exception ex)
        {
            splash?.Close();
            Log.Fatal(ex, "Start fehlgeschlagen");
            var text = ex.Message + "\n\nDetails stehen im Protokoll unter " + paths.Logs;
            try { HildeDialog.Inform(null, "HILDE konnte nicht starten", text, warning: true); }
            catch { MessageBox.Show("HILDE konnte nicht gestartet werden.\n\n" + text, "HILDE", MessageBoxButton.OK, MessageBoxImage.Error); }
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
