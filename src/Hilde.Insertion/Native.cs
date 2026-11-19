using System.Runtime.InteropServices;
using System.Text;

namespace Hilde.Insertion;

internal static partial class Native
{
    [LibraryImport("user32.dll")] public static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(IntPtr hWnd);
    [LibraryImport("user32.dll")] public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
    [LibraryImport("kernel32.dll")] public static partial uint GetCurrentThreadId();
    [LibraryImport("user32.dll")] public static partial IntPtr SetFocus(IntPtr hWnd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);
    [LibraryImport("user32.dll")] public static partial uint GetClipboardSequenceNumber();
    [LibraryImport("user32.dll")] public static partial short GetAsyncKeyState(int vKey);
    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassName(IntPtr hWnd, [Out] char[] buffer, int maxCount);
    [LibraryImport("user32.dll")] public static partial IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool CloseHandle(IntPtr h);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int index);

    public const uint GA_ROOT = 2;
    public const int SM_REMOTESESSION = 0x1000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_MENU = 0x12;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_RCONTROL = 0xA3;
    public const ushort VK_V = 0x56;
    public const ushort VK_INSERT = 0x2D;
    public const ushort VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>Kennzeichnet von HILDE erzeugte Tastendrücke, damit der eigene Hook sie ignoriert.</summary>
    public static readonly IntPtr HildeInputMarker = new(0x48494C44); // "HILD"

    public static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = HildeInputMarker } }
    };

    public static INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = HildeInputMarker } }
    };

    public static string ClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        int n = GetClassName(hwnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "";
    }

    public static bool IsProcessElevated(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return true; // kein Zugriff → sehr wahrscheinlich erhöht
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out var token)) return true;
            try { return GetTokenInformation(token, TokenElevation, out int elevated, 4, out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(h); }
    }
}
