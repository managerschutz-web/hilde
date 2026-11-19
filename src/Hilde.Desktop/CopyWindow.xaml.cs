using System.Windows;

namespace Hilde.Desktop;

/// <summary>Fallback, wenn das Einfügen nicht möglich war: Text bleibt erhalten und lässt sich kopieren.</summary>
public partial class CopyWindow : Window
{
    public CopyWindow(string text, string message, string errorId)
    {
        InitializeComponent();
        TextBox.Text = text;
        Message.Text = message;
        ToolTip = errorId;
        Loaded += (_, _) => { TextBox.Focus(); TextBox.SelectAll(); };
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TextBox.Text);
            Copied.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            Message.Text = "Die Zwischenablage ist blockiert. Bitte markieren und mit Strg+C kopieren.";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
