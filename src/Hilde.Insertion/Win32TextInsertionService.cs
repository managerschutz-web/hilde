using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Hilde.Core;
using Serilog;
using static Hilde.Insertion.Native;

namespace Hilde.Insertion;

public sealed class Win32InsertionTarget : InsertionTarget
{
    public IntPtr Root { get; init; }
    public IntPtr Focus { get; init; }
    public uint ProcessId { get; init; }
    public string? Process { get; init; }
    public string ClassName { get; init; } = "";
    public bool Remote { get; init; }
    public bool Console { get; init; }
    /// <summary>UI-Automation-Prüfung läuft im Hintergrund (Passwortfeld? schreibgeschützt?).</summary>
    public Task<FieldInfo?> Field { get; init; } = Task.FromResult<FieldInfo?>(null);

    public override string Description => $"{Process ?? "?"} ({ClassName})";
    public override string? ProcessName => Process;
    public override bool IsRemoteSession => Remote;
}

public sealed record FieldInfo(bool IsPassword, bool IsReadOnly, string ControlType);

/// <summary>
/// Einfügen in das bei Aufnahmestart aktive Feld (Pflichtenheft 3F):
/// 1. UI Automation prüft das Ziel (Passwortfeld/schreibgeschützt → nicht einfügen),
/// 2. Unicode-Zwischenablage + Einfügekommando (auch RDP/RemoteApp mit Zwischenablage-Umleitung),
///    alternativ Unicode-Tippen,
/// 3. sonst Kopierfenster (über den Aufrufer).
/// Sicherheitsrichtlinien werden nicht umgangen (z. B. erhöhte Zielprozesse).
/// </summary>
public sealed class Win32TextInsertionService : ITextInsertionService
{
    private static readonly string[] RemoteClasses = ["TscShellContainerClass", "RAIL_WINDOW", "RAILWindow", "IHWindowClass"];
    private static readonly string[] RemoteProcesses = ["mstsc", "msrdc", "msrdcw", "wfica32", "CDViewer", "vmconnect"];
    private static readonly string[] ConsoleClasses = ["ConsoleWindowClass"];

    private readonly ISettingsStore _settings;
    private readonly ILogger _log;
    private readonly bool _selfElevated;

    public Win32TextInsertionService(ISettingsStore settings, ILogger log)
    {
        _settings = settings;
        _log = log.ForContext<Win32TextInsertionService>();
        _selfElevated = IsProcessElevated((uint)Environment.ProcessId);
    }

    public InsertionTarget CaptureTarget()
    {
        var fg = GetForegroundWindow();
        var root = fg == IntPtr.Zero ? IntPtr.Zero : GetAncestor(fg, GA_ROOT);
        uint tid = GetWindowThreadProcessId(fg, out uint pid);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        IntPtr focus = GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg;
        string? procName = null;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); procName = p.ProcessName; } catch { }
        var cls = ClassName(root);
        bool remote = RemoteClasses.Contains(cls, StringComparer.OrdinalIgnoreCase)
                      || (procName != null && RemoteProcesses.Contains(procName, StringComparer.OrdinalIgnoreCase));

        return new Win32InsertionTarget
        {
            Root = root, Focus = focus, ProcessId = pid, Process = procName, ClassName = cls, Remote = remote,
            Console = ConsoleClasses.Contains(cls),
            Field = remote ? Task.FromResult<FieldInfo?>(null) : Task.Run(() => InspectField(focus))
        };
    }

    private static FieldInfo? InspectField(IntPtr focusHwnd)
    {
        try
        {
            var el = AutomationElement.FocusedElement ?? AutomationElement.FromHandle(focusHwnd);
            if (el == null) return null;
            bool pw = (bool)el.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty);
            bool ro = false;
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                ro = ((ValuePattern)vp).Current.IsReadOnly;
            return new FieldInfo(pw, ro, el.Current.ControlType?.ProgrammaticName ?? "");
        }
        catch { return null; } // UIA ist optional
    }

    public async Task<InsertionOutcome> InsertAsync(InsertionTarget target, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return InsertionOutcome.Ok("none");
        if (target is not Win32InsertionTarget t || t.Root == IntPtr.Zero) return InsertionOutcome.Fail(HildeErrors.TargetGone.Id);
        if (!IsWindow(t.Root) || !IsWindowVisible(t.Root)) return InsertionOutcome.Fail(HildeErrors.TargetGone.Id);
        if (!_selfElevated && IsProcessElevated(t.ProcessId)) return InsertionOutcome.Fail(HildeErrors.ElevatedTarget.Id);
        if (t.ProcessId == (uint)Environment.ProcessId) return InsertionOutcome.Fail(HildeErrors.InsertFailed.Id, "HILDE selbst");

        // UIA-Prüfung (höchstens 300 ms warten)
        var field = await WaitOrNull(t.Field, 300).ConfigureAwait(false);
        if (field is { IsPassword: true }) return InsertionOutcome.Fail(HildeErrors.InsertFailed.Id, "Passwortfeld");
        if (field is { IsReadOnly: true }) return InsertionOutcome.Fail(HildeErrors.InsertFailed.Id, "schreibgeschützt");

        if (!EnsureForeground(t)) return InsertionOutcome.Fail(HildeErrors.TargetGone.Id, "Fokus nicht wiederherstellbar");
        await WaitForModifiersReleasedAsync(ct).ConfigureAwait(false);

        var s = _settings.Current.Insertion;
        if (s.Method == InsertionMethod.Typing) return TypeText(text);

        try
        {
            return await StaThread.RunAsync(() => PasteViaClipboard(t, text, s)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            _log.Warning(ex, "{ErrorId} Zwischenablage blockiert", HildeErrors.ClipboardBlocked.Id);
            // Letzter Versuch ohne Zwischenablage, sofern kein RDP (dort ist Tippen unzuverlässig).
            if (!t.Remote && text.Length <= 2000) return TypeText(text);
            return InsertionOutcome.Fail(HildeErrors.ClipboardBlocked.Id);
        }
    }

    private InsertionOutcome PasteViaClipboard(Win32InsertionTarget t, string text, InsertionSettings s)
    {
        ClipboardSnapshot? snapshot = null;
        if (s.RestoreClipboard)
        {
            try { snapshot = ClipboardSnapshot.Capture(); }
            catch (Exception ex) { _log.Debug(ex, "Snapshot"); snapshot = null; }
        }
        ClipboardSnapshot.SetText(text);
        uint seq = GetClipboardSequenceNumber();

        // RDP: der Client muss die neue Zwischenablage erst an die Sitzung melden.
        Thread.Sleep(t.Remote ? 250 : 30);
        var keys = t.Console
            ? new[] { Key(VK_SHIFT, false), Key(VK_INSERT, false), Key(VK_INSERT, true), Key(VK_SHIFT, true) }
            : new[] { Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true) };
        uint sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>());
        if (sent != keys.Length) return InsertionOutcome.Fail(HildeErrors.InsertFailed.Id, "SendInput blockiert");

        if (snapshot is { Restorable: true })
        {
            Thread.Sleep(t.Remote ? s.RemoteRestoreDelayMs : s.RestoreDelayMs);
            // Nur zurücksetzen, wenn niemand sonst die Zwischenablage inzwischen geändert hat.
            if (GetClipboardSequenceNumber() == seq)
            {
                try { snapshot.Restore(); }
                catch (Exception ex) { _log.Debug(ex, "Restore"); }
            }
        }
        return InsertionOutcome.Ok(t.Remote ? "clipboard-rdp" : "clipboard");
    }

    private static InsertionOutcome TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n') { inputs.Add(Key(VK_RETURN, false)); inputs.Add(Key(VK_RETURN, true)); continue; }
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }
        // In Blöcken senden, damit langsame Ziele mitkommen.
        const int block = 200;
        for (int i = 0; i < inputs.Count; i += block)
        {
            var chunk = inputs.Skip(i).Take(block).ToArray();
            if (SendInput((uint)chunk.Length, chunk, Marshal.SizeOf<INPUT>()) != chunk.Length)
                return InsertionOutcome.Fail(HildeErrors.InsertFailed.Id, "SendInput blockiert");
            Thread.Sleep(5);
        }
        return InsertionOutcome.Ok("typing");
    }

    private static bool EnsureForeground(Win32InsertionTarget t)
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && GetAncestor(fg, GA_ROOT) == t.Root) return true;

        uint targetThread = GetWindowThreadProcessId(t.Root, out _);
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint self = GetCurrentThreadId();
        bool a1 = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
        bool a2 = targetThread != 0 && targetThread != self && AttachThreadInput(self, targetThread, true);
        try
        {
            SetForegroundWindow(t.Root);
            if (t.Focus != IntPtr.Zero && IsWindow(t.Focus)) SetFocus(t.Focus);
        }
        finally
        {
            if (a1) AttachThreadInput(self, fgThread, false);
            if (a2) AttachThreadInput(self, targetThread, false);
        }
        Thread.Sleep(60);
        var now = GetForegroundWindow();
        return now != IntPtr.Zero && GetAncestor(now, GA_ROOT) == t.Root;
    }

    /// <summary>Gedrückte Umschalt-/Alt-/Win-Taste würde Strg+V verfälschen.</summary>
    private static async Task WaitForModifiersReleasedAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1500)
        {
            bool down = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                        || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0
                        || (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
            if (!down) return;
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private static async Task<T?> WaitOrNull<T>(Task<T?> task, int ms) where T : class
    {
        var done = await Task.WhenAny(task, Task.Delay(ms)).ConfigureAwait(false);
        return done == task && task.IsCompletedSuccessfully ? task.Result : null;
    }
}
