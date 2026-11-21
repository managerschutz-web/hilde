using System.Windows.Threading;
using Hilde.Insertion;
using Serilog;

namespace Hilde.Desktop;

public enum PlacementResult { Click, Enter, Cancel }

/// <summary>
/// „Am Mauszeiger ablegen“: Der Text hängt als kleine Pill am Mauszeiger. Ein Linksklick setzt im
/// angeklickten Programm den Cursor (Klick wird normal weitergereicht), danach fügt HILDE dort ein.
/// Klicks auf Taskleiste/Startmenü/Desktop dienen nur zum Wechseln. Enter = ins gerade aktive Feld,
/// Esc oder Rechtsklick = zurück zum Diktierfenster.
/// </summary>
public sealed class CursorPlacement
{
    private const int VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    private readonly Dispatcher _ui;
    private readonly ILogger _log;

    public bool Active { get; private set; }

    public CursorPlacement(Dispatcher ui, ILogger log)
    {
        _ui = ui;
        _log = log.ForContext<CursorPlacement>();
    }

    /// <summary>Muss auf dem UI-Thread aufgerufen werden.</summary>
    public async Task<PlacementResult> RunAsync(string text, LowLevelKeyboardHook? keyboard, TimeSpan timeout)
    {
        if (Active) return PlacementResult.Cancel;
        Active = true;
        var tcs = new TaskCompletionSource<PlacementResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pill = new CursorPillWindow(text);
        var hook = new PlacementHook();
        long armedAt = Environment.TickCount64 + 250; // nachlaufendes Enter/Autorepeat ignorieren
        hook.LeftClick += root =>
        {
            if (Win32TextInsertionService.IsShellWindow(root) || Win32TextInsertionService.IsOwnWindow(root)) return;
            tcs.TrySetResult(PlacementResult.Click);
        };
        hook.RightClick += () => tcs.TrySetResult(PlacementResult.Cancel);
        if (keyboard != null)
        {
            keyboard.Interceptor = vk =>
            {
                if (vk == VK_ESCAPE) { tcs.TrySetResult(PlacementResult.Cancel); return true; }
                if (vk == VK_RETURN)
                {
                    if (Environment.TickCount64 < armedAt) return true;
                    tcs.TrySetResult(PlacementResult.Enter);
                    return true;
                }
                return false;
            };
        }
        try
        {
            pill.Show();
            hook.Start();
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            var result = done == tcs.Task ? tcs.Task.Result : PlacementResult.Cancel;
            _log.Information("Ablage am Mauszeiger: {Result}", result);
            return result;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Ablage am Mauszeiger nicht möglich");
            return PlacementResult.Cancel;
        }
        finally
        {
            if (keyboard != null) keyboard.Interceptor = null;
            hook.Dispose();
            pill.Close();
            Active = false;
        }
    }
}
