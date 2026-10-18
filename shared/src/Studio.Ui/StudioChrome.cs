using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Studio.Ui;

/// <summary>
/// The Studio's borderless app window: no Windows title bar; the app's brand band at the top is the title bar (drag
/// to move, double-click to maximise, right-click for the window menu) and holds <see cref="CaptionButtons"/>.
/// Everything a normal window does keeps working: resizing from the edges, snapping (drag to an edge, Win + arrows,
/// and on Windows 11 the snap layouts shown when hovering the maximise button), the rounded corners and the shadow,
/// and a maximised window that fits the screen exactly.
/// </summary>
public static class StudioChrome
{
    sealed class State
    {
        public required WindowChrome Chrome;
        public required double CaptionHeight;
        public CaptionButtons? Buttons;
        public IntPtr Hwnd;
    }

    static readonly ConditionalWeakTable<Window, State> States = new();

    /// <summary>Make <paramref name="window"/> borderless. <paramref name="captionHeight"/> = the height of its band
    /// (the top strip that works as the title bar). Call it in the constructor, after InitializeComponent.</summary>
    public static void Apply(Window window, double captionHeight)
    {
        var chrome = new WindowChrome
        {
            CaptionHeight = captionHeight,
            ResizeBorderThickness = new Thickness(8),
            // a sliver of the system frame stays, so Windows keeps the shadow and the snap animations
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
            // only the title bar is taken over: the sides and the bottom keep Windows' own (invisible) resize frame, just
            // outside the window as on any other app, so resizing works even where an embedded view (the WebView2 3D
            // previews) reaches the window's edge
            NonClientFrameEdges = NonClientFrameEdges.Left | NonClientFrameEdges.Right | NonClientFrameEdges.Bottom,
        };
        WindowChrome.SetWindowChrome(window, chrome);
        var st = new State { Chrome = chrome, CaptionHeight = captionHeight };
        States.AddOrUpdate(window, st);
        window.SourceInitialized += (_, _) =>
        {
            st.Hwnd = new WindowInteropHelper(window).Handle;
            // Windows 11: rounded corners like any other window (ignored on Windows 10)
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(st.Hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            HwndSource.FromHwnd(st.Hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) => Hook(window, st, msg, wp, lp, ref handled));
            Fit(window, st);
        };
        window.StateChanged += (_, _) => Fit(window, st);
        window.SizeChanged += (_, _) => { if (window.WindowState == WindowState.Maximized) Fit(window, st); };
        window.DpiChanged += (_, _) => Fit(window, st);
    }

    /// <summary>Called by <see cref="CaptionButtons"/> when it is placed in a window.</summary>
    internal static void Register(Window window, CaptionButtons buttons)
    {
        if (States.TryGetValue(window, out var st)) st.Buttons = buttons;
    }

    /// <summary>
    /// A maximised window reaches past the screen by the width of its (now invisible) frame, which would cut off the
    /// edges of the app; keep the content inside the screen's work area instead.
    /// </summary>
    static void Fit(Window window, State st)
    {
        if (window.Content is not FrameworkElement root) return;
        var margin = new Thickness(0);
        if (window.WindowState == WindowState.Maximized && st.Hwnd != IntPtr.Zero && GetClientRect(st.Hwnd, out var r))
        {
            // the app's area on screen (the sides and bottom keep a system frame, so it is not the window rectangle)
            var tl = new POINT(); ClientToScreen(st.Hwnd, ref tl);
            r = new RECT { Left = tl.X, Top = tl.Y, Right = tl.X + r.Right, Bottom = tl.Y + r.Bottom };
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(st.Hwnd, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                var dpi = VisualTreeHelper.GetDpi(window);
                var w = mi.rcWork;
                margin = new Thickness(Math.Max(0, w.Left - r.Left) / dpi.DpiScaleX, Math.Max(0, w.Top - r.Top) / dpi.DpiScaleY,
                                       Math.Max(0, r.Right - w.Right) / dpi.DpiScaleX, Math.Max(0, r.Bottom - w.Bottom) / dpi.DpiScaleY);
            }
        }
        if (root.Margin != margin) root.Margin = margin;
        double caption = st.CaptionHeight + margin.Top;
        if (st.Chrome.CaptionHeight != caption) st.Chrome.CaptionHeight = caption;
    }

    const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2,
              WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9;

    /// <summary>
    /// Windows 11 shows its snap layouts when the pointer rests on a window's maximise button, which it finds by asking
    /// the window what is under the pointer: answer "maximise button" over ours, then do its hover / press / click.
    /// </summary>
    static IntPtr Hook(Window window, State st, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var max = st.Buttons?.MaximiseButton;
        if (max is null || !max.IsVisible) return IntPtr.Zero;
        switch (msg)
        {
            case WM_NCHITTEST:
                bool over = Over(max, lParam);
                max.IsHot = over;
                if (!over) max.IsDown = false;
                if (over) { handled = true; return HTMAXBUTTON; }
                break;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when wParam == HTMAXBUTTON:
                max.IsDown = true;
                handled = true;
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                if (max.IsDown) st.Buttons!.ToggleMaximise();
                max.IsDown = false;
                handled = true;
                return IntPtr.Zero;
            case WM_NCMOUSEMOVE when wParam != HTMAXBUTTON:
            case WM_NCMOUSELEAVE:
                max.IsHot = false;
                max.IsDown = false;
                break;
        }
        return IntPtr.Zero;
    }

    static bool Over(FrameworkElement e, IntPtr lParam)
    {
        if (PresentationSource.FromVisual(e) is null) return false;
        int x = unchecked((short)(long)lParam), y = unchecked((short)((long)lParam >> 16));
        var a = e.PointToScreen(new Point(0, 0));
        var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return x >= Math.Min(a.X, b.X) && x < Math.Max(a.X, b.X) && y >= Math.Min(a.Y, b.Y) && y < Math.Max(a.Y, b.Y);
    }

    // ---------------------------------------------------------------- Win32

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
