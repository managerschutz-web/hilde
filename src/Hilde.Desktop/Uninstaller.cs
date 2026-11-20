using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace Hilde.Desktop;

/// <summary>
/// Deinstallation aus HILDE heraus: startet den regulären Windows-Deinstaller (Setup-Paket bzw. MSI),
/// nachdem HILDE beendet ist. Optional werden danach auch die Benutzerdaten gelöscht.
/// </summary>
internal static class Uninstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public sealed record Entry(string Command, string Arguments, string Kind);

    public static Entry? Find()
    {
        using var root = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (root == null) return null;
        Entry? msi = null;
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("DisplayName") as string != "HILDE") continue;
            var uninstall = key.GetValue("UninstallString") as string;
            if (key.GetValue("BundleUpgradeCode") != null && uninstall != null)
            {
                var (exe, args) = Split(uninstall);
                return new Entry(exe, args, "bundle");
            }
            if (name.StartsWith('{')) msi = new Entry("msiexec.exe", $"/x {name}", "msi");
        }
        return msi;
    }

    private static (string Exe, string Args) Split(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return (command[1..end], command[(end + 1)..].Trim());
        }
        int space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
    }

    /// <summary>Schreibt ein kleines Skript, das nach dem Beenden von HILDE deinstalliert.</summary>
    public static void Launch(Entry entry, bool deleteData, string dataRoot)
    {
        var script = Path.Combine(Path.GetTempPath(), $"hilde-uninstall-{Guid.NewGuid():N}.cmd");
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("timeout /t 3 /nobreak >nul");
        sb.AppendLine("taskkill /im Hilde.Engine.exe /f >nul 2>&1");
        sb.AppendLine($"start \"\" /wait \"{entry.Command}\" {entry.Arguments}");
        if (deleteData) sb.AppendLine($"rmdir /s /q \"{dataRoot}\"");
        sb.AppendLine("del \"%~f0\"");
        File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}
