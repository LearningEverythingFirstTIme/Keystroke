using System.Runtime.InteropServices;

namespace KeystrokeApp.Services;

/// <summary>
/// Gets the text caret position from the foreground window using GetGUIThreadInfo.
/// Works cross-process, unlike GetCaretPos which only works in-process.
/// Falls back to mouse cursor position if caret detection fails.
/// </summary>
public static class CursorPositionHelper
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    /// <summary>
    /// Result of caret position detection.
    /// </summary>
    public struct CaretPosition
    {
        public double X;
        public double Y;
        public double Height;
        public bool IsCaretDetected;
    }

    /// <summary>
    /// Get the text caret position in screen coordinates.
    /// Uses GetGUIThreadInfo for cross-process caret detection.
    /// Falls back to mouse cursor position if caret can't be found.
    /// </summary>
    public static CaretPosition GetCaretPosition()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return GetMouseFallback();

            var threadId = GetWindowThreadProcessId(hwnd, out _);
            if (threadId == 0)
                return GetMouseFallback();

            var info = new GUITHREADINFO();
            info.cbSize = Marshal.SizeOf<GUITHREADINFO>();

            if (!GetGUIThreadInfo(threadId, ref info))
                return GetMouseFallback();

            // Check if there's a caret and a window to map coordinates from
            if (info.hwndCaret == IntPtr.Zero)
                return GetMouseFallback();

            // rcCaret is in client coordinates of hwndCaret — convert to screen
            var caretPoint = new POINT
            {
                X = info.rcCaret.Left,
                Y = info.rcCaret.Bottom // Bottom of caret = where we want to place the panel
            };

            if (!ClientToScreen(info.hwndCaret, ref caretPoint))
                return GetMouseFallback();

            int caretHeight = info.rcCaret.Bottom - info.rcCaret.Top;

            return new CaretPosition
            {
                X = caretPoint.X,
                Y = caretPoint.Y,
                Height = caretHeight > 0 ? caretHeight : 20,
                IsCaretDetected = true
            };
        }
        catch
        {
            return GetMouseFallback();
        }
    }

    private static CaretPosition GetMouseFallback()
    {
        if (GetCursorPos(out POINT pos))
        {
            return new CaretPosition
            {
                X = pos.X,
                Y = pos.Y + 20, // Offset below mouse cursor
                Height = 20,
                IsCaretDetected = false
            };
        }

        return new CaretPosition { X = 100, Y = 100, Height = 20, IsCaretDetected = false };
    }
}
