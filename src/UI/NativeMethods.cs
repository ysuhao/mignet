using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopStamp.UI
{
    public static class NativeMethods
    {
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int GWL_EXSTYLE = -20;

        public const int WM_NCHITTEST = 0x0084;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_DPICHANGED = 0x02E0;
        public const int MA_NOACTIVATE = 3;
        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;

        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int ULW_ALPHA = 0x00000002;
        public const uint BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;

        public const int WH_MOUSE_LL = 14;
        public const int WH_KEYBOARD_LL = 13;

        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;

        public const int VK_ESCAPE = 0x1B;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12; // Alt
        public const int VK_SHIFT = 0x10;
        public const int VK_TAB = 0x09;
        public const int VK_RETURN = 0x0D;
        public const int VK_SPACE = 0x20;
        public const int VK_PRIOR = 0x21; // Page Up
        public const int VK_NEXT = 0x22;  // Page Down
        public const int VK_END = 0x23;
        public const int VK_HOME = 0x24;
        public const int VK_LEFT = 0x25;
        public const int VK_UP = 0x26;
        public const int VK_RIGHT = 0x27;
        public const int VK_DOWN = 0x28;
        public const int VK_ADD = 0x6B;
        public const int VK_SUBTRACT = 0x6D;
        public const int VK_OEM_PLUS = 0xBB;
        public const int VK_OEM_MINUS = 0xBD;

        public const uint LLMHF_INJECTED = 0x00000001;
        public const uint LLKHF_INJECTED = 0x00000010;
        public const uint GA_ROOT = 2;
        public const uint OBJID_NATIVEOM = 0xFFFFFFF0;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IOleMessageFilter
        {
            [PreserveSig]
            int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

            [PreserveSig]
            int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

            [PreserveSig]
            int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
        }

        [DllImport("ole32.dll")]
        public static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref POINT pptDst,
            ref SIZE psize,
            IntPtr hdcSrc,
            ref POINT pptSrc,
            uint crKey,
            [In] ref BLENDFUNCTION pblend,
            uint dwFlags);

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll", ExactSpelling = true)]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            [In] ref BITMAPINFOHEADER pbmi,
            uint iUsage,
            out IntPtr ppvBits,
            IntPtr hSection,
            uint dwOffset);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("oleacc.dll")]
        public static extern int AccessibleObjectFromWindow(
            IntPtr hwnd,
            uint dwId,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IDispatch)] out object ppvObject);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out bool pvParam, uint fWinIni);

        public static string GetWindowClassName(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(128);
            int len = GetClassName(hWnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        public static string GetWindowTitle(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            var sb = new StringBuilder(256);
            int len = GetWindowText(hWnd, sb, sb.Capacity);
            return len > 0 ? sb.ToString() : string.Empty;
        }

        public static string QueryProcessNameFast(uint pid)
        {
            if (pid == 0) return string.Empty;
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero)
                return string.Empty;

            try
            {
                var sb = new StringBuilder(260);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(hProc, 0, sb, ref size) && size > 0)
                {
                    return Path.GetFileNameWithoutExtension(sb.ToString()) ?? string.Empty;
                }
            }
            catch
            {
            }
            finally
            {
                CloseHandle(hProc);
            }

            return string.Empty;
        }

        public static bool IsReducedMotionEnabled()
        {
            try
            {
                if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out bool clientAnimationEnabled, 0))
                {
                    return !clientAnimationEnabled;
                }
            }
            catch
            {
            }
            return false;
        }

        public static bool IsReducedMotionPreferred()
        {
            return IsReducedMotionEnabled();
        }

        public static double GetDpiForMonitorPoint(System.Drawing.Point screenPoint)
        {
            return QueryDpiForPointOrWindow(new POINT(screenPoint.X, screenPoint.Y), IntPtr.Zero);
        }

        private sealed class StandardOleMessageFilter : IOleMessageFilter
        {
            private readonly int _retryDelayMs;
            private readonly int _timeoutMs;

            public StandardOleMessageFilter(int retryDelayMs, int timeoutMs)
            {
                _retryDelayMs = Math.Max(15, retryDelayMs);
                _timeoutMs = Math.Max(200, timeoutMs);
            }

            public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
            {
                return 0; // SERVERCALL_ISHANDLED
            }

            public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
            {
                // 2 == SERVERCALL_RETRYLATER
                if (dwRejectType == 2 && dwTickCount < _timeoutMs)
                {
                    return _retryDelayMs;
                }
                return -1; // Cancel call instead of hanging indefinitely
            }

            public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
            {
                return 2; // PENDINGMSG_WAITDEFPROCESS
            }
        }

        public static void RegisterStandardOleMessageFilter(int retryDelayMs = 50, int timeoutMs = 2200)
        {
            try
            {
                CoRegisterMessageFilter(new StandardOleMessageFilter(retryDelayMs, timeoutMs), out _);
            }
            catch
            {
            }
        }

        public static void RevokeOleMessageFilter()
        {
            try
            {
                CoRegisterMessageFilter(null, out _);
            }
            catch
            {
            }
        }

        public static double QueryDpiForPointOrWindow(POINT screenPoint, IntPtr hWnd)
        {
            try
            {
                IntPtr hMon = MonitorFromPoint(screenPoint, MONITOR_DEFAULTTONEAREST);
                if (hMon != IntPtr.Zero && GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0 && dpiX >= 72 && dpiX <= 480)
                {
                    return dpiX;
                }
            }
            catch
            {
            }

            try
            {
                if (hWnd != IntPtr.Zero)
                {
                    uint winDpi = GetDpiForWindow(hWnd);
                    if (winDpi >= 72 && winDpi <= 480)
                        return winDpi;
                }
            }
            catch
            {
            }

            return 96.0;
        }

        public static bool IsKeyDown(int vKey)
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }
    }
}
