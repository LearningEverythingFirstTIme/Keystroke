using System.Runtime.InteropServices;

namespace KeystrokeApp.Services;

/// <summary>
/// Gets the mouse cursor position and monitor work area for panel positioning.
/// All returned coordinates are in physical screen pixels — callers must scale
/// by the target monitor's DPI to reach DIPs. Any "below the cursor" offset
/// must be applied DIP-side by the caller so it stays visually constant across
/// 100%, 125%, 150%, and 200% DPI scales.
/// </summary>
public static class CursorPositionHelper
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;

    public struct CursorPosition
    {
        public double X;
        public double Y;
    }

    /// <summary>
    /// Work area of the monitor containing the cursor, in physical screen pixels.
    /// Use this in preference to SystemParameters.WorkArea — the latter only
    /// reports the primary monitor, so clamping a panel to it produces off-screen
    /// or wrong-monitor placement on multi-monitor setups.
    /// </summary>
    public struct MonitorWorkArea
    {
        public double Left;
        public double Top;
        public double Right;
        public double Bottom;
        public double DpiScaleX;
        public double DpiScaleY;
    }

    /// <summary>
    /// Get the mouse cursor position in physical screen coordinates.
    /// Callers must divide by the relevant monitor's DPI scale to get DIPs.
    /// </summary>
    public static CursorPosition GetMousePosition()
    {
        if (GetCursorPos(out POINT pos))
            return new CursorPosition { X = pos.X, Y = pos.Y };

        return new CursorPosition { X = 100, Y = 100 };
    }

    /// <summary>
    /// Returns the work area and DPI of the monitor under the cursor. Falls back to
    /// a safe default if any lookup fails so callers never have to null-check.
    /// </summary>
    public static MonitorWorkArea GetWorkAreaForCursor()
    {
        try
        {
            if (!GetCursorPos(out POINT pt))
                return FallbackWorkArea();

            var hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hmon == IntPtr.Zero)
                return FallbackWorkArea();

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hmon, ref mi))
                return FallbackWorkArea();

            double scaleX = 1.0, scaleY = 1.0;
            if (GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
            {
                scaleX = dpiX / 96.0;
                scaleY = dpiY / 96.0;
            }

            return new MonitorWorkArea
            {
                Left = mi.rcWork.Left,
                Top = mi.rcWork.Top,
                Right = mi.rcWork.Right,
                Bottom = mi.rcWork.Bottom,
                DpiScaleX = scaleX,
                DpiScaleY = scaleY
            };
        }
        catch
        {
            return FallbackWorkArea();
        }
    }

    private static MonitorWorkArea FallbackWorkArea() => new()
    {
        Left = 0,
        Top = 0,
        Right = System.Windows.SystemParameters.PrimaryScreenWidth,
        Bottom = System.Windows.SystemParameters.PrimaryScreenHeight,
        DpiScaleX = 1.0,
        DpiScaleY = 1.0
    };
}
