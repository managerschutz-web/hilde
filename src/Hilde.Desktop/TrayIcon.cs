using System.Drawing;
using WinForms = System.Windows.Forms;

namespace Hilde.Desktop;

/// <summary>Symbol im Infobereich mit Menü.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _toggle;
    private readonly WinForms.ToolStripMenuItem _pill;

    public event Action? OpenRequested;
    public event Action? ToggleDictationRequested;
    public event Action? TogglePillRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hilde.ico"))!.Stream;
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("HILDE öffnen", null, (_, _) => OpenRequested?.Invoke());
        _toggle = new WinForms.ToolStripMenuItem("Freihand-Diktat starten", null, (_, _) => ToggleDictationRequested?.Invoke());
        menu.Items.Add(_toggle);
        _pill = new WinForms.ToolStripMenuItem("Pill anzeigen", null, (_, _) => TogglePillRequested?.Invoke()) { CheckOnClick = false };
        menu.Items.Add(_pill);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Icon = new Icon(stream),
            Text = "HILDE – bereit",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
    }

    public void SetStatus(string text, bool recording)
    {
        var t = "HILDE – " + text;
        _icon.Text = t.Length > 63 ? t[..63] : t;
        _toggle.Text = recording ? "Diktat beenden" : "Freihand-Diktat starten";
    }

    public void SetPillVisible(bool visible) => _pill.Checked = visible;

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
