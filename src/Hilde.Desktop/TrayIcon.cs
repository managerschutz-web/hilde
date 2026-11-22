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
    public event Action? PauseRequested;
    private readonly WinForms.ToolStripMenuItem _pause;
    public event Action? TogglePillRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hilde.ico"))!.Stream;
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = true },
            BackColor = Color.FromArgb(0x16, 0x1A, 0x22),
            ForeColor = Color.FromArgb(0xE8, 0xED, 0xF4),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f),
            Padding = new WinForms.Padding(4)
        };
        menu.Items.Add("HILDE öffnen", null, (_, _) => OpenRequested?.Invoke());
        _toggle = new WinForms.ToolStripMenuItem("Freihand-Diktat starten", null, (_, _) => ToggleDictationRequested?.Invoke());
        menu.Items.Add(_toggle);
        _pause = new WinForms.ToolStripMenuItem("Pause / Fortsetzen", null, (_, _) => PauseRequested?.Invoke()) { Enabled = false };
        menu.Items.Add(_pause);
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
        _pause.Enabled = recording;
    }

    public void SetPillVisible(bool visible) => _pill.Text = visible ? "Pill ausblenden" : "Pill einblenden";

    /// <summary>Dunkles Menü passend zur Pill.</summary>
    private sealed class DarkColors : WinForms.ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x16, 0x1A, 0x22);
        private static readonly Color Hi = Color.FromArgb(0x2E, 0x6B, 0xFF);
        private static readonly Color Line = Color.FromArgb(0x33, 0x3A, 0x46);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hi;
        public override Color MenuItemSelected => Hi;
        public override Color MenuItemSelectedGradientBegin => Hi;
        public override Color MenuItemSelectedGradientEnd => Hi;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Hi;
        public override Color CheckSelectedBackground => Hi;
        public override Color CheckPressedBackground => Hi;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
