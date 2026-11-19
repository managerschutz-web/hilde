using System.Diagnostics;
using System.Runtime.InteropServices;
using Hilde.Core;
using Serilog;

namespace Hilde.Insertion;

/// <summary>
/// Globaler Tastatur-Hook für Push-to-Talk. Datenschutz: Es werden ausschließlich die Auslösetaste und
/// Escape ausgewertet; für alle anderen Tasten wird nur „irgendeine Taste gedrückt“ gemeldet –
/// ohne Tastencode, ohne Speicherung. Tasten werden nicht blockiert (außer Escape während Aufnahme).
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_ESCAPE = 0x1B;
    private const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    private readonly GestureRecognizer _gestures;
    private readonly Action<GestureAction> _dispatch;
    private readonly Func<int> _triggerKey;
    private readonly Func<bool> _escapeEnabled;
    private readonly ILogger _log;
    private readonly HookProc _proc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private bool _triggerDown;
    private volatile bool _paused;

    public LowLevelKeyboardHook(GestureRecognizer gestures, Action<GestureAction> dispatch,
        Func<int> triggerKey, Func<bool> escapeEnabled, ILogger log)
    {
        _gestures = gestures;
        _dispatch = dispatch;
        _triggerKey = triggerKey;
        _escapeEnabled = escapeEnabled;
        _log = log.ForContext<LowLevelKeyboardHook>();
        _proc = Callback; // Referenz halten, sonst sammelt der GC den Delegaten ein
    }

    /// <summary>Für die Tastenzuweisung in den Einstellungen.</summary>
    public event Action<int>? KeyCaptured;
    public bool CaptureMode { get; set; }

    /// <summary>Hook vorübergehend ignorieren (z. B. während der Einrichtung).</summary>
    public bool Paused { get => _paused; set => _paused = value; }

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        Exception? error = null;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) error = new InvalidOperationException("Tastatur-Hook konnte nicht installiert werden (" + Marshal.GetLastWin32Error() + ")");
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "HILDE-Hotkey", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
        _log.Information("Globaler Hotkey aktiv");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Eigene Eingaben (Strg+V beim Einfügen) ignorieren
            if ((k.flags & LLKHF_INJECTED) != 0 && k.dwExtraInfo == Native.HildeInputMarker)
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            int vk = (int)k.vkCode;
            long now = Environment.TickCount64;

            if (CaptureMode)
            {
                if (down) { CaptureMode = false; KeyCaptured?.Invoke(vk); return (IntPtr)1; }
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            }
            if (_paused) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            if (vk == _triggerKey())
            {
                if (down && !_triggerDown) { _triggerDown = true; GestureAction a; lock (_gestures) a = _gestures.TriggerDown(now); _dispatch(a); }
                else if (up) { _triggerDown = false; GestureAction a; lock (_gestures) a = _gestures.TriggerUp(now); _dispatch(a); }
            }
            else if (vk == VK_ESCAPE && down && _escapeEnabled() && _gestures.IsRecording)
            {
                GestureAction a; lock (_gestures) a = _gestures.Escape();
                _dispatch(a);
                return (IntPtr)1; // Escape nicht an das Zielfenster weiterreichen
            }
            else if (down)
            {
                GestureAction a; lock (_gestures) a = _gestures.OtherKeyDown(now);
                _dispatch(a);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
