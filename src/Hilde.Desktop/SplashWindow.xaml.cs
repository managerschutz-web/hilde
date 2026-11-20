using System.Windows;
using System.Windows.Media.Animation;

namespace Hilde.Desktop;

/// <summary>Startbildschirm mit HILDE und dieapp.info – erscheint beim manuellen Start.</summary>
public partial class SplashWindow : Window
{
    private readonly DateTime _shownAt = DateTime.UtcNow;

    public SplashWindow()
    {
        InitializeComponent();
        var version = typeof(SplashWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        Info.Text = $"Version {version} · startet …";
        var anim = new DoubleAnimation(-70, 230, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
        Loaded += (_, _) => BarMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

    public void SetInfo(string text) => Info.Text = text;

    /// <summary>Mindestens kurz sichtbar lassen, dann sanft ausblenden.</summary>
    public async Task CloseGentlyAsync(TimeSpan minVisible)
    {
        var rest = minVisible - (DateTime.UtcNow - _shownAt);
        if (rest > TimeSpan.Zero) await Task.Delay(rest);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        var tcs = new TaskCompletionSource();
        fade.Completed += (_, _) => tcs.TrySetResult();
        BeginAnimation(OpacityProperty, fade);
        await tcs.Task;
        Close();
    }
}
