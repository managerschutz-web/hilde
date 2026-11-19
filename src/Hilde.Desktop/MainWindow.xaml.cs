using System.ComponentModel;
using System.Windows;
using Hilde.Core;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace Hilde.Desktop;

/// <summary>Arbeitsfenster: WebView2 mit lokaler HTML/CSS/TypeScript-Oberfläche (offline, kein Netz).</summary>
public partial class MainWindow : Window
{
    public const string Host = "app.hilde.example"; // reservierte Domain, verlässt nie den Rechner
    private readonly HildePaths _paths;
    private readonly UiBridge _bridge;
    private bool _allowClose;

    public MainWindow(HildePaths paths, UiBridge bridge)
    {
        InitializeComponent();
        _paths = paths;
        _bridge = bridge;
        Loaded += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _paths.WebViewData);
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            var uiDir = Path.Combine(AppContext.BaseDirectory, "ui");
            core.SetVirtualHostNameToFolderMapping(Host, uiDir, CoreWebView2HostResourceAccessKind.Deny);
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            // Nur die eigene Oberfläche – keine externen Seiten, keine neuen Fenster.
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) => _bridge.OnMessage(e.WebMessageAsJson);
            _bridge.Attach(json => Dispatcher.InvokeAsync(() =>
            {
                try { Web.CoreWebView2?.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Debug(ex, "Post"); }
            }));
            core.Navigate($"https://{Host}/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowFallback("Die Microsoft Edge WebView2-Laufzeit fehlt. Bitte HILDE-Setup.exe erneut ausführen – " +
                         "es richtet die Laufzeit automatisch ein. Diktieren funktioniert trotzdem.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WebView2 konnte nicht gestartet werden");
            ShowFallback("Die Oberfläche konnte nicht geladen werden. Diktieren funktioniert weiterhin. Details im Protokoll.");
        }
    }

    private void ShowFallback(string text)
    {
        Web.Visibility = Visibility.Collapsed;
        Fallback.Text = text;
        Fallback.Visibility = Visibility.Visible;
    }

    public void Navigate(string view) => _bridge.Emit("navigate", new { view });

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Schließen = in den Infobereich minimieren; Beenden über das Tray-Menü.
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
