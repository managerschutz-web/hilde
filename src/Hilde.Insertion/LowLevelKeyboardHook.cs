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
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const uint WM_REINSTALL = 0x8000 + 0x48; // WM_APP + 'H'
    private Timer? _watchdog;
    private Timer? _reinstallTimer;

    /// <summary>
    /// Sicherheitsnetz: Geht das Loslassen der Taste verloren (z. B. Remotedesktop im Vordergrund, Sperrbildschirm,
    /// UAC-Dialog), bliebe HILDE sonst „hängen“ und würde auf weitere Tastendrücke nicht mehr reagieren.
    /// </summary>
    private void Watchdog()
    {
        try
        {
            lock (_gestures)
            {
                if (!_triggerDown) return;
                long now = Environment.TickCount64;
                bool physicallyDown = (GetAsyncKeyState(_triggerKey()) & 0x8000) != 0;
                if (physicallyDown || now - _lastTriggerEvent < 400) return;
                _triggerDown = false;
                _log.Information("Loslassen der Diktiertaste nicht empfangen – Zustand korrigiert");
                _dispatch(_gestures.TriggerUp(now));
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
    private long _lastTriggerEvent;

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
    private bool _readDown;

    /// <summary>Taste zum Vorlesen von markiertem Text (wird nicht an das Programm weitergereicht).</summary>
    public Func<int>? ReadKey { get; set; }
    public event Action? ReadKeyPressed;

    /// <summary>Nur während der Ablage am Mauszeiger: Enter/Esc abfangen (true = Taste nicht weiterreichen).</summary>
    public Func<int, bool>? Interceptor { get; set; }
    private int _swallowedVk = -1;

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
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message != WM_REINSTALL) continue;
                // Windows entfernt Hooks stillschweigend, wenn sie einmal zu langsam waren – vorsorglich erneuern.
                var fresh = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (fresh != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = fresh; }
            }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "HILDE-Hotkey", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait();
        if (error != null) throw error;
        _log.Information("Globaler Hotkey aktiv");
        _watchdog = new Timer(_ => Watchdog(), null, 400, 400);
        _reinstallTimer = new Timer(_ => { if (_threadId != 0) PostThreadMessage(_threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero); },
            null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
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

            if (Interceptor is { } intercept && vk != _triggerKey())
            {
                if (down && intercept(vk)) { _swallowedVk = vk; return (IntPtr)1; }
                if (up && vk == _swallowedVk) { _swallowedVk = -1; return (IntPtr)1; }
            }

            if (ReadKey != null && vk == ReadKey() && vk != _triggerKey())
            {
                if (down && !_readDown) { _readDown = true; ReadKeyPressed?.Invoke(); }
                else if (up) _readDown = false;
                return (IntPtr)1;
            }
            if (vk == _triggerKey())
            {
                lock (_gestures)
                {
                    if (down)
                    {
                        // Verpasstes Loslassen: Autorepeat kommt spätestens nach ~1 s, längere Lücke = neuer Tastendruck
                        if (_triggerDown && now - _lastTriggerEvent > 1200)
                        {
                            _triggerDown = false;
                            _dispatch(_gestures.TriggerUp(_lastTriggerEvent));
                        }
                        _lastTriggerEvent = now;
                        if (!_triggerDown) { _triggerDown = true; _dispatch(_gestures.TriggerDown(now)); }
                    }
                    else if (up)
                    {
                        _lastTriggerEvent = now;
                        _triggerDown = false;
                        _dispatch(_gestures.TriggerUp(now));
                    }
                }
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
        _watchdog?.Dispose();
        _reinstallTimer?.Dispose();
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
    }
}
