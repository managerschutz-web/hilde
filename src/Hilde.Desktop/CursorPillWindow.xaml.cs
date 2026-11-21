using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Hilde.Desktop;

/// <summary>Kleine Pill, die dem Mauszeiger folgt. Durchklickbar – der Klick landet im Programm darunter.</summary>
public partial class CursorPillWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private readonly DispatcherTimer _follow;
    private IntPtr _hwnd;
    private POINT _last = new() { X = int.MinValue };

    public CursorPillWindow(string text)
    {
        InitializeComponent();
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        Snippet.Text = oneLine.Length > 60 ? oneLine[..60] + " …" : oneLine;
        _follow = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        _follow.Tick += (_, _) => Follow();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE)
                | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            Follow();
        };
        Loaded += (_, _) => _follow.Start();
        Closed += (_, _) => _follow.Stop();
    }

    private void Follow()
    {
        if (_hwnd == IntPtr.Zero || !GetCursorPos(out var p)) return;
        if (p.X == _last.X && p.Y == _last.Y) return;
        _last = p;
        // Physische Pixel – unabhängig von der Skalierung des jeweiligen Bildschirms
        SetWindowPos(_hwnd, IntPtr.Zero, p.X + 14, p.Y + 18, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
