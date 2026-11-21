using System.Diagnostics;
using System.Runtime.InteropServices;
using static Hilde.Insertion.Native;

namespace Hilde.Insertion;

/// <summary>
/// Mausüberwachung nur während „Text am Mauszeiger ablegen“: Der Linksklick geht ganz normal an das
/// angeklickte Programm (setzt dort den Cursor), danach fügt HILDE ein. Rechtsklick bricht ab und wird
/// nicht weitergereicht. Es werden keine Positionen gespeichert oder protokolliert.
/// </summary>
public sealed class PlacementHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const uint LLMHF_INJECTED = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

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

    private readonly HookProc _proc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    /// <summary>Linke Maustaste losgelassen über einem Fenster (Wurzelfenster).</summary>
    public event Action<IntPtr>? LeftClick;
    public event Action? RightClick;

    public PlacementHook() => _proc = Callback;

    public void Start()
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }) { IsBackground = true, Name = "HILDE-Ablage" };
        _thread.Start();
        ready.Wait();
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("Maus-Hook konnte nicht installiert werden");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        try
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((m.flags & LLMHF_INJECTED) == 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONUP)
                {
                    var hwnd = WindowFromPoint(m.pt);
                    var root = hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_ROOT);
                    ThreadPool.QueueUserWorkItem(_ => LeftClick?.Invoke(root));
                }
                else if (msg == WM_RBUTTONDOWN)
                {
                    ThreadPool.QueueUserWorkItem(_ => RightClick?.Invoke());
                    return (IntPtr)1;
                }
                else if (msg == WM_RBUTTONUP) return (IntPtr)1; // kein Kontextmenü im Zielprogramm
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _threadId = 0;
    }
}
