using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Hilde.Core;
using Hilde.Insertion;
using Hilde.Language;

namespace Hilde.Desktop;

/// <summary>
/// Diktierfenster: Text erscheint live, ist jederzeit bearbeitbar, kann gekürzt oder sachlich gefasst werden
/// (Vorher/Nachher nebeneinander). „Einfügen ↗“ hängt ihn an den Mauszeiger.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly Brush Gray = Freeze(0x9C, 0xA3, 0xAF);
    private static readonly Brush Blue = Freeze(0x60, 0xA5, 0xFA);
    private static readonly Brush Red = Freeze(0xF8, 0x71, 0x71);
    private Brush _accent = Freeze(0x34, 0xD3, 0x99);

    private CancellationTokenSource? _rewriteCts;
    private bool _recording;
    private bool _busy;

    /// <summary>Text umformulieren (Kürzen bzw. Sachlich &amp; klar) – liefert den neuen Text.</summary>
    public Func<string, RewriteKind, CancellationToken, Task<(string Text, string? Warning)>>? Rewrite { get; set; }
    public event Action<string>? InsertAtCursorRequested;
    public event Action<string, InsertionTarget>? InsertToOriginRequested;
    public event Action? DictateClicked;
    /// <summary>Fenster geschlossen, Text verworfen.</summary>
    public event Action? Discarded;

    public InsertionTarget? Origin { get; private set; }
    public List<string> RecordIds { get; } = [];
    public string HotkeyName { get; set; } = "Strg rechts";

    public EditorWindow()
    {
        InitializeComponent();
        Editor.TextChanged += (_, _) => UpdateButtons();
        Closing += (_, e) => { if (_force) return; e.Cancel = true; RequestClose(); };
        SetStatus(new StatusEventArgs(HildeState.Ready));
        UpdateButtons();
    }

    public string Text => Editor.Text;

    private bool _force;
    public void ForceClose() { _force = true; Close(); }

    public void ApplyAccent(string hex)
    {
        try { _accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); _accent.Freeze(); }
        catch { }
        LevelBar.Background = _accent;
    }

    /// <summary>Neu öffnen: neben dem Mauszeiger bzw. zuletzt genutzte Position.</summary>
    public void Open(bool activate)
    {
        ShowActivated = activate;
        if (!IsVisible)
        {
            if (!_placed) { WindowStartupLocation = WindowStartupLocation.CenterScreen; _placed = true; }
            Show();
        }
        if (activate) { Activate(); Editor.Focus(); }
    }
    private bool _placed;

    /// <summary>Fertig erkanntes Diktat an der Cursorposition einfügen (Strg+Z macht es rückgängig).</summary>
    public void AppendDictation(string text, InsertionTarget target, string recordId)
    {
        RemoveLive();
        RecordIds.Add(recordId);
        if (target is Win32InsertionTarget w && w.ProcessId != (uint)Environment.ProcessId && w.Root != IntPtr.Zero
            && !Win32TextInsertionService.IsShellWindow(w.Root))
        {
            Origin = target;
            OriginLabel.Text = "In " + FriendlyName(target.ProcessName);
        }
        int caret = Editor.SelectionLength > 0 ? Editor.SelectionStart + Editor.SelectionLength : Editor.CaretIndex;
        var (newText, newCaret) = EditorText.InsertAt(Editor.Text, caret, text);
        var piece = newText.Substring(caret, newText.Length - Editor.Text.Length);
        Editor.Select(caret, 0);
        Editor.SelectedText = piece;
        Editor.CaretIndex = newCaret;
        Editor.Focus();
        UpdateButtons();
    }

    // Live-Text steht direkt im Textfeld (an der Cursorposition) und wird am Ende durch die Endfassung ersetzt.
    private int _liveStart = -1;
    private int _liveLength;

    public void SetPreview(string fixedText, string provisional)
    {
        var live = (fixedText + " " + provisional).Trim();
        if (live.Length == 0) { RemoveLive(); return; }
        if (_liveStart < 0)
        {
            _liveStart = Editor.SelectionLength > 0 ? Editor.SelectionStart + Editor.SelectionLength : Editor.CaretIndex;
            _liveLength = 0;
            Editor.IsUndoEnabled = false;      // Zwischenstände sollen nicht im Rückgängig-Verlauf landen
            Editor.BorderBrush = _accent;
        }
        Editor.IsReadOnly = false;
        var baseText = Editor.Text.Remove(_liveStart, Math.Min(_liveLength, Editor.Text.Length - _liveStart));
        var (full, _) = EditorText.InsertAt(baseText, _liveStart, live);
        var piece = full.Substring(_liveStart, full.Length - baseText.Length);
        Editor.Select(_liveStart, _liveLength);
        Editor.SelectedText = piece;
        _liveLength = piece.Length;
        Editor.Select(_liveStart + _liveLength, 0);
        Editor.IsReadOnly = true;              // während HILDE schreibt, nicht dazwischentippen
        Editor.ScrollToLine(Math.Max(0, Editor.GetLineIndexFromCharacterIndex(Math.Max(0, Editor.CaretIndex - 1))));
        UpdateButtons();
    }

    private void RemoveLive()
    {
        if (_liveStart < 0) return;
        Editor.IsReadOnly = false;
        _liveStart = Math.Min(_liveStart, Editor.Text.Length);
        int len = Math.Min(_liveLength, Editor.Text.Length - _liveStart);
        Editor.Select(_liveStart, len);
        Editor.SelectedText = "";
        Editor.CaretIndex = _liveStart;
        _liveStart = -1;
        _liveLength = 0;
        Editor.IsReadOnly = false;
        Editor.IsUndoEnabled = true;
        Editor.ClearValue(BorderBrushProperty);
        UpdateButtons();
    }

    public void SetLevel(float level) => LevelBar.Width = 60 * Math.Clamp(level * 3, 0, 1);

    public void SetStatus(StatusEventArgs e)
    {
        _recording = e.State is HildeState.Recording or HildeState.Paused;
        LevelTrack.Visibility = e.State == HildeState.Recording ? Visibility.Visible : Visibility.Collapsed;
        switch (e.State)
        {
            case HildeState.Recording:
                StatusDot.Fill = Red;
                StatusText.Text = "Ich höre zu – " + HotkeyName + " oder ■ beendet";
                break;
            case HildeState.Paused:
                StatusDot.Fill = Gray;
                StatusText.Text = "Pausiert";
                break;
            case HildeState.Processing:
                StatusDot.Fill = Blue;
                StatusText.Text = "Verarbeite …";
                break;
            case HildeState.Error:
                StatusDot.Fill = Red;
                StatusText.Text = "Fehler";
                ShowNotice(e.Message);
                break;
            default:
                StatusDot.Fill = _accent;
                StatusText.Text = "Bereit – " + HotkeyName + " halten oder 🎙 klicken, um weiterzudiktieren";
                break;
        }
        MicGlyph.Visibility = _recording ? Visibility.Collapsed : Visibility.Visible;
        StopGlyph.Visibility = _recording ? Visibility.Visible : Visibility.Collapsed;
        DictateButton.Background = (Brush)FindResource(_recording ? "MicLive" : "MicIdle");
        DictateLabel.Text = e.State switch
        {
            HildeState.Recording => "Ich höre zu …",
            HildeState.Paused => "Pausiert",
            HildeState.Processing => "Schreibe …",
            _ => "Diktieren"
        };
        DictateHint.Text = _recording ? "Klick oder " + HotkeyName + " = fertig" : HotkeyName + " 2×";
        SetPulse(e.State == HildeState.Recording);
        if (e.State is HildeState.Ready or HildeState.Done) SetPreview("", "");
        UpdateButtons();
    }

    private Storyboard? _pulse;

    private void SetPulse(bool on)
    {
        if (on && _pulse == null)
        {
            _pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var dur = TimeSpan.FromSeconds(1.4);
            void Add(DependencyObject target, string path, double from, double to)
            {
                var a = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(a, target);
                Storyboard.SetTargetProperty(a, new PropertyPath(path));
                _pulse!.Children.Add(a);
            }
            Add(Pulse, "Opacity", 0.45, 0);
            Add(Pulse, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", 1, 1.6);
            Add(Pulse, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", 1, 1.6);
            _pulse.Begin(this, true);
        }
        else if (!on && _pulse != null)
        {
            _pulse.Stop(this);
            _pulse = null;
            Pulse.Opacity = 0;
        }
    }

    public void ShowNotice(string? message)
    {
        Notice.Text = message ?? "";
        Notice.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Nach erfolgreichem Einfügen oder Verwerfen: leeren und ausblenden.</summary>
    public void Reset()
    {
        _rewriteCts?.Cancel();
        HideCompare();
        RemoveLive();
        Editor.Clear();
        RecordIds.Clear();
        Origin = null;
        OriginButton.Visibility = Visibility.Collapsed;
        ShowNotice(null);
        Hide();
    }

    private void UpdateButtons()
    {
        bool hasText = Editor.Text.Trim().Length > 0 && _liveStart < 0; // während HILDE live schreibt: abwarten
        Placeholder.Visibility = Editor.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShortenButton.IsEnabled = ClarifyButton.IsEnabled = hasText && !_busy && Rewrite != null;
        CopyButton.IsEnabled = InsertButton.IsEnabled = hasText && !_busy;
        OriginButton.Visibility = Origin != null ? Visibility.Visible : Visibility.Collapsed;
        OriginButton.IsEnabled = hasText && !_busy;
        DictateButton.IsEnabled = !_busy;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void OnClose(object sender, RoutedEventArgs e) => RequestClose();

    private void RequestClose()
    {
        if (Editor.Text.Trim().Length > 0)
        {
            if (!HildeDialog.Ask(this, "Text verwerfen?", "Der Text im Diktierfenster geht verloren. Die einzelnen Diktate bleiben im Archiv.",
                    "Verwerfen", "Behalten", warning: true)) return;
        }
        Reset();
        Discarded?.Invoke();
    }

    private void OnDictate(object sender, RoutedEventArgs e) => DictateClicked?.Invoke();

    private void OnEditorKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) OnOrigin(this, new RoutedEventArgs());
        else OnInsert(this, new RoutedEventArgs());
    }

    private void OnInsert(object sender, RoutedEventArgs e)
    {
        if (!InsertButton.IsEnabled) return;
        HideCompare();
        ShowNotice(null);
        InsertAtCursorRequested?.Invoke(Editor.Text.Trim());
    }

    private void OnOrigin(object sender, RoutedEventArgs e)
    {
        if (Origin == null || !OriginButton.IsEnabled) return;
        HideCompare();
        ShowNotice(null);
        InsertToOriginRequested?.Invoke(Editor.Text.Trim(), Origin);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Editor.SelectionLength > 0 ? Editor.SelectedText : Editor.Text.Trim());
            CopyGlyph.Visibility = Visibility.Collapsed;
            CheckGlyph.Visibility = Visibility.Visible;
            _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                CheckGlyph.Visibility = Visibility.Collapsed;
                CopyGlyph.Visibility = Visibility.Visible;
            }));
        }
        catch { ShowNotice("Die Zwischenablage ist gerade belegt – bitte noch einmal versuchen."); }
    }

    private void OnShorten(object sender, RoutedEventArgs e) => _ = RunRewriteAsync(RewriteKind.Shorten);
    private void OnClarify(object sender, RoutedEventArgs e) => _ = RunRewriteAsync(RewriteKind.Clarify);

    private async Task RunRewriteAsync(RewriteKind kind)
    {
        if (Rewrite == null || _busy) return;
        bool selection = Editor.SelectionLength > 0;
        int selStart = Editor.SelectionStart, selLen = Editor.SelectionLength;
        var source = selection ? Editor.SelectedText : Editor.Text;
        if (string.IsNullOrWhiteSpace(source)) return;

        _rewriteCts?.Cancel();
        var cts = _rewriteCts = new CancellationTokenSource();
        _busy = true;
        UpdateButtons();
        ShowNotice(null);
        var oldStatus = StatusText.Text;
        StatusDot.Fill = Blue;
        StatusText.Text = kind == RewriteKind.Shorten ? "HILDE kürzt …" : "HILDE formuliert sachlich & klar …";
        try
        {
            var (rewritten, warning) = await Rewrite(source, kind, cts.Token);
            var result = rewritten.Trim();
            if (cts.IsCancellationRequested) return;
            var before = Editor.Text;
            if (selection)
            {
                Editor.Select(selStart, selLen);
                Editor.SelectedText = result;
            }
            else
            {
                Editor.SelectAll();
                Editor.SelectedText = result;
            }
            Editor.CaretIndex = selection ? selStart + result.Length : Editor.Text.Length;
            ShowCompare(before);
            if (warning != null) ShowNotice("Bitte prüfen: " + warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ShowNotice(ex is HildeException he ? he.Error.Message : "Das hat nicht geklappt – der Text ist unverändert.");
        }
        finally
        {
            _busy = false;
            StatusText.Text = oldStatus;
            StatusDot.Fill = _recording ? Red : _accent;
            UpdateButtons();
        }
    }

    private double _widthBeforeCompare;

    private void ShowCompare(string before)
    {
        BeforeBox.Text = before;
        if (ComparePane.Visibility != Visibility.Visible)
        {
            _widthBeforeCompare = Width;
            var maxWidth = SystemParameters.WorkArea.Width - 40;
            var wanted = Math.Min(Math.Max(Width, 1000), maxWidth);
            if (Left + wanted > SystemParameters.WorkArea.Right) Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - wanted);
            Width = wanted;
        }
        CompareCol.Width = new GridLength(1, GridUnitType.Star);
        ComparePane.Visibility = Visibility.Visible;
        AfterLabel.Visibility = Visibility.Visible;
        ShortenButton.Visibility = ClarifyButton.Visibility = Visibility.Collapsed;
        AcceptButton.Visibility = RevertButton.Visibility = Visibility.Visible;
    }

    private void HideCompare()
    {
        if (ComparePane.Visibility == Visibility.Visible && _widthBeforeCompare > 0) Width = _widthBeforeCompare;
        CompareCol.Width = new GridLength(0);
        ComparePane.Visibility = Visibility.Collapsed;
        AfterLabel.Visibility = Visibility.Collapsed;
        ShortenButton.Visibility = ClarifyButton.Visibility = Visibility.Visible;
        AcceptButton.Visibility = RevertButton.Visibility = Visibility.Collapsed;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        HideCompare();
        Editor.Focus();
    }

    private void OnRevert(object sender, RoutedEventArgs e)
    {
        var before = BeforeBox.Text;
        HideCompare();
        Editor.SelectAll();
        Editor.SelectedText = before;
        Editor.CaretIndex = Editor.Text.Length;
        Editor.Focus();
    }

    public static string FriendlyName(string? process) => process?.ToLowerInvariant() switch
    {
        null or "" => "Ausgangsfeld",
        "outlook" or "olk" => "Outlook",
        "winword" => "Word",
        "excel" => "Excel",
        "powerpnt" => "PowerPoint",
        "msedge" => "Edge",
        "chrome" => "Chrome",
        "firefox" => "Firefox",
        "notepad" => "Editor",
        "teams" or "ms-teams" => "Teams",
        "mstsc" or "msrdc" or "msrdcw" => "Remotedesktop",
        _ => char.ToUpper(process[0]) + process[1..]
    };

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
