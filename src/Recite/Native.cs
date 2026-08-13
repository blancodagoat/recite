using System.Runtime.InteropServices;

namespace Recite;

/// <summary>All Win32 interop for the app. No third-party dependencies.</summary>
internal static class Native
{
    // messages
    public const int WM_HOTKEY = 0x0312;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_DPICHANGED = 0x02E0;

    /// <summary>Posted to the hotkey window by the single-instance watcher.</summary>
    public const int WM_APP_SHOW_SETTINGS = 0x0400 + 17;

    /// <summary>Posted when another process (the Triumvirate suite) asks for a clean
    /// exit instead of killing the process.</summary>
    public const int WM_APP_QUIT = 0x0400 + 18;

    public static readonly IntPtr HWND_MESSAGE = new(-3);
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    public const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    // hotkey modifiers
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int VK_SNAPSHOT = 0x2C;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // window positioning
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT rect, int size);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    /// <summary>A visible top-level window's frame bounds plus its visible child-control rects.</summary>
    public readonly record struct WindowRects(Rectangle Bounds, IReadOnlyList<Rectangle> Children);

    /// <summary>
    /// Rects of the visible top-level windows, topmost first, as composited right now,
    /// each with its visible child controls. Cloaked windows (UWP suspended, other
    /// virtual desktops) are on screen as far as Win32 is concerned but invisible to the
    /// user, so they are skipped; the extended frame bounds are used for the window
    /// because GetWindowRect includes the invisible resize border.
    /// </summary>
    public static List<WindowRects> VisibleWindowRects()
    {
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        const int DWMWA_CLOAKED = 14;

        var windows = new List<WindowRects>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                return true;
            }

            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf<RECT>()) != 0)
            {
                return true;
            }

            var bounds = rect.ToRectangle();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return true;
            }

            var children = new List<Rectangle>();
            EnumChildWindows(hWnd, (child, _) =>
            {
                if (IsWindowVisible(child) && GetWindowRect(child, out RECT childRect))
                {
                    var c = Rectangle.Intersect(childRect.ToRectangle(), bounds);
                    if (c.Width > 0 && c.Height > 0)
                    {
                        children.Add(c);
                    }
                }

                return true;
            }, IntPtr.Zero);

            windows.Add(new WindowRects(bounds, children));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// Rect to highlight for a cursor position: the topmost window containing the point,
    /// narrowed to the smallest of its child controls that also contains it, so hovering
    /// a pane targets the pane while the title bar still targets the whole window.
    /// Empty when the point is over bare desktop.
    /// </summary>
    public static Rectangle HitTest(IEnumerable<WindowRects> windows, Point p)
    {
        foreach (var w in windows)
        {
            if (!w.Bounds.Contains(p))
            {
                continue;
            }

            var best = w.Bounds;
            foreach (var c in w.Children)
            {
                if (c.Contains(p) && (long)c.Width * c.Height < (long)best.Width * best.Height)
                {
                    best = c;
                }
            }

            return best;
        }

        return Rectangle.Empty;
    }

    /// <summary>Blocks until the desktop composition that includes any pending window
    /// changes has been presented — i.e. a just-hidden window is really gone from screen.</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);

    /// <summary>
    /// Tells every top-level window a Control Panel section changed, so the shell re-reads
    /// it without a sign-out. Timeout-guarded: one hung window must not hang us.
    /// </summary>
    public static void BroadcastSettingChange(string section) =>
        SendMessageTimeoutW(
            HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, section, SMTO_ABORTIFHUNG, 1000, out _);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>True while the Windows key is physically held down.</summary>
    public static bool IsWinKeyDown() =>
        (GetKeyState(VK_LWIN) & 0x8000) != 0 || (GetKeyState(VK_RWIN) & 0x8000) != 0;

    /// <summary>Best-effort dark title bar. Attribute 20 on current builds, 19 on older Win10.</summary>
    public static void TryUseDarkTitleBar(IntPtr hWnd)
    {
        int on = 1;
        if (DwmSetWindowAttribute(hWnd, 20, ref on, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hWnd, 19, ref on, sizeof(int));
        }
    }

    /// <summary>Best-effort Win11 rounded corners (attribute 33, DWMWCP_ROUND). No-op on Win10.</summary>
    public static void TryRoundCorners(IntPtr hWnd)
    {
        int round = 2;
        DwmSetWindowAttribute(hWnd, 33, ref round, sizeof(int));
    }

    private enum NotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7,
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out NotificationState state);

    /// <summary>
    /// True while no notification window should appear: a fullscreen game or presentation
    /// owns the display (showing any window could knock it out of fullscreen), or the user
    /// has Focus Assist / quiet hours on. Failure to query counts as not suppressed.
    /// </summary>
    public static bool NotificationsSuppressed() =>
        SHQueryUserNotificationState(out var state) == 0
        && state is NotificationState.Busy
            or NotificationState.RunningD3dFullScreen
            or NotificationState.PresentationMode
            or NotificationState.QuietTime;
}
