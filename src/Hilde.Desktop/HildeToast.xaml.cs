using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Hilde.Desktop;

/// <summary>Hinweis im HILDE-Stil unten rechts (statt Windows-Sprechblase). Stiehlt nie den Fokus.</summary>
public partial class HildeToast : Window
{
    private const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    private static readonly List<HildeToast> Open = [];
    private readonly DispatcherTimer _timer;
    private readonly Action? _onClick;

    private HildeToast(string title, string message, bool warning, TimeSpan duration, Action? onClick)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        _onClick = onClick;
        if (warning) Ring.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) => FadeOut();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => _timer.Start();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
        Closed += (_, _) => { Open.Remove(this); Arrange(); };
    }

    /// <summary>Muss auf dem UI-Thread aufgerufen werden.</summary>
    public static void Pop(string title, string message, bool warning = false, TimeSpan? duration = null, Action? onClick = null)
    {
        while (Open.Count >= 3) Open[0].Close();
        var t = new HildeToast(title, message, warning, duration ?? TimeSpan.FromSeconds(7), onClick) { Opacity = 0 };
        Open.Add(t);
        t.Show();
        t.UpdateLayout();
        Arrange();
        t.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        t._timer.Start();
    }

    /// <summary>Unten rechts übereinander stapeln (neueste unten).</summary>
    private static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 4;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            t.Left = area.Right - t.ActualWidth - 4;
            t.Top = bottom - t.ActualHeight;
            bottom = t.Top + 4;
        }
    }

    private void FadeOut()
    {
        _timer.Stop();
        var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(220));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }

    private void OnClose(object sender, RoutedEventArgs e) { e.Handled = true; FadeOut(); }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        _onClick?.Invoke();
        FadeOut();
    }
}
