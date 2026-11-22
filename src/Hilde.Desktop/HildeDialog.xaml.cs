using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Hilde.Desktop;

/// <summary>Rückfrage bzw. Meldung im HILDE-Stil statt klassischer Windows-MessageBox.</summary>
public partial class HildeDialog : Window
{
    private HildeDialog(string title, string message, string yes, string? no, bool warning)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        YesButton.Content = yes;
        if (no == null) NoButton.Visibility = Visibility.Collapsed;
        else NoButton.Content = no;
        if (warning) Ring.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        Loaded += (_, _) => (no != null ? NoButton : YesButton).Focus();
    }

    /// <summary>Ja/Nein-Frage. Standard-Fokus liegt auf „Nein“, damit Enter nichts Unwiderrufliches auslöst.</summary>
    public static bool Ask(Window? owner, string title, string message, string yes = "Ja", string no = "Abbrechen", bool warning = false)
    {
        var d = new HildeDialog(title, message, yes, no, warning);
        if (owner is { IsVisible: true }) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        return d.ShowDialog() == true;
    }

    public static void Inform(Window? owner, string title, string message, bool warning = false)
    {
        var d = new HildeDialog(title, message, "OK", null, warning);
        if (owner is { IsVisible: true }) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        d.ShowDialog();
    }

    private void OnYes(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnNo(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { }
    }
}
