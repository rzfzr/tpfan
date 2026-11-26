using System;
using System.Runtime.InteropServices;

/// Win32 calls used by the bar (window, layered drawing, tooltips, menu). Source-generated, so NativeAOT-safe.
static unsafe partial class Native {
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_POPUP = 0x80000000, WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
    public const uint WM_DESTROY = 0x2, WM_TIMER = 0x113, WM_MOUSEMOVE = 0x200, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205,
        WM_MOUSELEAVE = 0x2A3, WM_COMMAND = 0x111, WM_USER = 0x400, WM_DPICHANGED = 0x2E0, WM_SETTINGCHANGE = 0x1A, WM_MOUSEACTIVATE = 0x21;
    public const int MA_NOACTIVATE = 3;
    public const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4, SWP_SHOWWINDOW = 0x40;
    public const uint ULW_ALPHA = 2;
    public const uint TME_LEAVE = 2;
    public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800;
    public const uint TTS_ALWAYSTIP = 1, TTS_NOPREFIX = 2, TTF_SUBCLASS = 0x10;
    public const uint TTM_ADDTOOLW = WM_USER + 50, TTM_DELTOOLW = WM_USER + 51, TTM_NEWTOOLRECTW = WM_USER + 52,
        TTM_UPDATETIPTEXTW = WM_USER + 57, TTM_SETMAXTIPWIDTH = WM_USER + 24;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; public uint priv; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [StructLayout(LayoutKind.Sequential)]
    public struct TOOLINFOW { public uint cbSize, uFlags; public IntPtr hwnd, uId; public RECT rect; public IntPtr hinst, lpszText, lParam, lpReserved; }
    [StructLayout(LayoutKind.Sequential)] public struct INITCOMMONCONTROLSEX { public uint dwSize, dwICC; }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")] public static partial ushort RegisterClassEx(in WNDCLASSEXW wc);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] public static partial IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")] public static partial int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool TranslateMessage(in MSG m);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] public static partial IntPtr DispatchMessage(in MSG m);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyWindow(IntPtr h);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)] public static partial IntPtr FindWindow(string cls, string name);
    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)] public static partial IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(IntPtr h, out RECT r);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool ScreenToClient(IntPtr h, ref POINT p);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cy, uint flags);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool MoveWindow(IntPtr h, int x, int y, int w, int cy, [MarshalAs(UnmanagedType.Bool)] bool repaint);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(IntPtr h);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindowVisible(IntPtr h);
    [LibraryImport("user32.dll")] public static partial IntPtr GetParent(IntPtr h);
    [LibraryImport("user32.dll")] public static partial uint GetDpiForWindow(IntPtr h);
    [LibraryImport("user32.dll")] public static partial UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr proc);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)] public static partial uint RegisterWindowMessage(string name);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetCursorPos(out POINT p);
    [LibraryImport("user32.dll")] public static partial IntPtr CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)][return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr h, IntPtr rect);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool DestroyMenu(IntPtr menu);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(IntPtr h);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, IntPtr pptDst, in SIZE size, IntPtr hdcSrc, in POINT ptSrc, uint key, in BLENDFUNCTION blend, uint flags);
    [LibraryImport("user32.dll")] public static partial IntPtr GetDC(IntPtr h);
    [LibraryImport("user32.dll")] public static partial int ReleaseDC(IntPtr h, IntPtr dc);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")] public static partial IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")] public static partial IntPtr LoadCursor(IntPtr inst, IntPtr id);

    [LibraryImport("gdi32.dll")] public static partial IntPtr CreateCompatibleDC(IntPtr dc);
    [LibraryImport("gdi32.dll")] public static partial IntPtr CreateDIBSection(IntPtr dc, in BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll")] public static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("comctl32.dll")][return: MarshalAs(UnmanagedType.Bool)] public static partial bool InitCommonControlsEx(in INITCOMMONCONTROLSEX icc);
    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr ShellExecute(IntPtr h, string op, string file, string args, string dir, int show);
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)] public static partial IntPtr GetModuleHandle(string name);

    public static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);
    public static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);
}
