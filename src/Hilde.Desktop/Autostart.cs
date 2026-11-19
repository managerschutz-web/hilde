using Microsoft.Win32;

namespace Hilde.Desktop;

/// <summary>Optionaler Autostart über HKCU\…\Run (keine Adminrechte nötig).</summary>
internal static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "HILDE";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true) ?? Registry.CurrentUser.CreateSubKey(Key);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HILDE.exe");
            key.SetValue(Name, $"\"{exe}\" --tray");
        }
        else if (key.GetValue(Name) != null)
        {
            key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(Name) != null;
    }
}
