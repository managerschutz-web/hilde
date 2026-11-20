using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hilde.Core;

namespace Hilde.Desktop;

/// <summary>
/// Schwebende Glass-Pill. Stiehlt nie den Fokus (WS_EX_NOACTIVATE), damit das Zielfeld aktiv bleibt.
/// </summary>
public partial class PillWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static readonly Brush Neutral = Freeze(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private Brush Green = Freeze(Color.FromRgb(0x34, 0xD3, 0x99));
    private Color _accent = Color.FromRgb(0x34, 0xD3, 0x99);
    private static readonly Brush Yellow = Freeze(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush Blue = Freeze(Color.FromRgb(0x60, 0xA5, 0xFA));
    private static readonly Brush Red = Freeze(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush BarIdle = Freeze(Color.FromArgb(0x55, 0x9C, 0xA3, 0xAF));

    private readonly DispatcherTimer _provisionalTimer;
    private readonly DispatcherTimer _savePosTimer;
    private StatusEventArgs? _pending;
    private HildeState _state = HildeState.Ready;
    private readonly Rectangle[] _bars;
    private Storyboard? _spin;

    public event Action? MicClicked;
    public event Action? ExpandClicked;
    public event Action<double, double>? PositionChanged;
    public event Action? HideRequested;
    public event Action? PauseClicked;
    public event Action? QuitRequested;

    public PillWindow()
    {
        InitializeComponent();
        _bars = LevelBars.Children.OfType<Rectangle>().ToArray();
        _provisionalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _provisionalTimer.Tick += (_, _) => { _provisionalTimer.Stop(); if (_pending != null) Apply(_pending); };
        _savePosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _savePosTimer.Tick += (_, _) => { _savePosTimer.Stop(); PositionChanged?.Invoke(Left, Top); };
        LocationChanged += (_, _) => { if (_autoMove) return; _savePosTimer.Stop(); _savePosTimer.Start(); };
        SizeChanged += OnSizeChanged;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
        Apply(new StatusEventArgs(HildeState.Ready));
    }

    private bool _autoMove;
    private bool _anchorBottom = true;

    /// <summary>Wächst die Pill durch die Vorschau, bleibt die Unterkante stehen (Pill sitzt meist unten).</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.HeightChanged || e.PreviousSize.Height <= 0) return;
        if (e.NewSize.Height > e.PreviousSize.Height)
            _anchorBottom = Top + e.PreviousSize.Height > SystemParameters.WorkArea.Top + 220;
        if (!_anchorBottom) return;
        _autoMove = true;
        Top -= e.NewSize.Height - e.PreviousSize.Height;
        Dispatcher.InvokeAsync(() => _autoMove = false, DispatcherPriority.Background);
    }

    /// <summary>Frei wählbare Farben (#RRGGBB).</summary>
    public void ApplyColors(string? background, string? accent)
    {
        var bg = ParseColor(background, Color.FromRgb(0x16, 0x1A, 0x22));
        _accent = ParseColor(accent, Color.FromRgb(0x34, 0xD3, 0x99));
        Root.Background = new SolidColorBrush(Color.FromArgb(0xD9, bg.R, bg.G, bg.B));
        Green = Freeze(_accent);
        // Text hell oder dunkel je nach Hintergrund
        double lum = 0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B;
        var fg = lum > 150 ? Color.FromRgb(0x1B, 0x1F, 0x27) : Color.FromRgb(0xF0, 0xF4, 0xFA);
        StatusText.Foreground = new SolidColorBrush(fg);
        PreviewText.Foreground = new SolidColorBrush(fg);
        MicIcon.Fill = new SolidColorBrush(fg);
        PauseIcon.Fill = new SolidColorBrush(fg);
        PlayIcon.Fill = new SolidColorBrush(fg);
        Apply(new StatusEventArgs(_state));
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        try { return hex is { Length: 7 } && hex[0] == '#' ? (Color)ColorConverter.ConvertFromString(hex) : fallback; }
        catch { return fallback; }
    }

    /// <summary>Live-Text anzeigen (leer = ausblenden). Zeigt nur das Ende langer Diktate.</summary>
    public void SetPreview(string fixedText, string provisional)
    {
        if (string.IsNullOrWhiteSpace(fixedText) && string.IsNullOrWhiteSpace(provisional))
        {
            PreviewText.Visibility = Visibility.Collapsed;
            PreviewText.Inlines.Clear();
            return;
        }
        const int max = 260;
        string f = fixedText.Trim(), p = provisional.Trim();
        int total = f.Length + p.Length + 1;
        if (total > max)
        {
            int cut = total - max;
            if (cut < f.Length) f = "… " + f[cut..].TrimStart();
            else { p = "… " + p[Math.Min(p.Length, cut - f.Length)..]; f = ""; }
        }
        PreviewText.Inlines.Clear();
        if (f.Length > 0) PreviewText.Inlines.Add(new System.Windows.Documents.Run(f + " "));
        if (p.Length > 0)
            PreviewText.Inlines.Add(new System.Windows.Documents.Run(p) { FontStyle = FontStyles.Italic, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xC7, 0xCD, 0xD6)) });
        PreviewText.Visibility = Visibility.Visible;
    }

    private static Brush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public void PlaceDefault()
    {
        var area = SystemParameters.WorkArea;
        UpdateLayout();
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 16;
    }

    public void EnsureOnScreen()
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.Contains(new Point(Left + 20, Top + 20))) PlaceDefault();
    }

    public void SetStatus(StatusEventArgs e)
    {
        // Kurze Tipps auf die Taste sollen keine Aufnahme-Anzeige aufblitzen lassen.
        if (e.State == HildeState.Recording && e.Mode == RecordingMode.PushToTalk && _state != HildeState.Recording)
        {
            _pending = e;
            _provisionalTimer.Stop();
            _provisionalTimer.Start();
            return;
        }
        _provisionalTimer.Stop();
        _pending = null;
        Apply(e);
    }

    private void Apply(StatusEventArgs e)
    {
        _state = e.State;
        var (brush, text) = e.State switch
        {
            HildeState.Recording => (Green, e.Message ?? "Aufnahme …"),
            HildeState.Paused => (Yellow, e.Message ?? "Pausiert"),
            HildeState.Processing => (Blue, e.Message ?? "Verarbeite …"),
            HildeState.Error => (Red, e.Message ?? "Fehler"),
            HildeState.Done => (Green, e.Message ?? "Fertig"),
            _ => (Neutral, "Bereit"),
        };
        StatusDot.Fill = brush;
        StatusText.Text = text;
        ToolTip = e.ErrorId != null ? $"{text} ({e.ErrorId})" : "HILDE – Strg rechts halten und sprechen";
        MicButton.Background = e.State == HildeState.Recording
            ? new SolidColorBrush(Color.FromArgb(0x55, _accent.R, _accent.G, _accent.B))
            : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        bool active = e.State is HildeState.Recording or HildeState.Paused;
        PauseButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PauseIcon.Visibility = e.State == HildeState.Paused ? Visibility.Collapsed : Visibility.Visible;
        PlayIcon.Visibility = e.State == HildeState.Paused ? Visibility.Visible : Visibility.Collapsed;
        bool processing = e.State == HildeState.Processing;
        Spinner.Visibility = processing ? Visibility.Visible : Visibility.Collapsed;
        MicIcon.Opacity = processing ? 0.35 : 1;
        if (processing) StartSpinner(); else StopSpinner();
        if (e.State != HildeState.Recording) SetLevel(0);
    }

    public void SetLevel(float level)
    {
        bool rec = _state == HildeState.Recording;
        double[] weights = [0.55, 0.85, 1.0, 0.8, 0.5];
        for (int i = 0; i < _bars.Length; i++)
        {
            double h = rec ? 4 + 16 * Math.Clamp(level * weights[i] * 1.4, 0, 1) : 4;
            _bars[i].Height = h;
            _bars[i].Fill = rec ? Green : BarIdle;
        }
    }

    private void StartSpinner()
    {
        if (_spin != null) return;
        var anim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
        _spin = new Storyboard();
    }

    private void StopSpinner()
    {
        if (_spin == null) return;
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _spin = null;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // kein Ziehen über Schaltflächen

    private void OnMicClick(object sender, MouseButtonEventArgs e) { e.Handled = true; MicClicked?.Invoke(); }

    private void OnPauseClick(object sender, MouseButtonEventArgs e) { e.Handled = true; PauseClicked?.Invoke(); }

    private void OnMenuOpen(object sender, RoutedEventArgs e) => ExpandClicked?.Invoke();
    private void OnMenuHide(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
    private void OnMenuQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void OnExpandClick(object sender, MouseButtonEventArgs e) { e.Handled = true; ExpandClicked?.Invoke(); }
}
