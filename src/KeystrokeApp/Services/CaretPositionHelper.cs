using System.Runtime.InteropServices;

namespace KeystrokeApp.Services;

/// <summary>
/// Gets the text caret (insertion point) position from the active window using
/// GetGUIThreadInfo — the Win32 API that returns the actual caret rectangle,
/// not the mouse cursor. Falls back to Accessibility (MSAA) caret tracking
/// for apps that don't expose a standard caret (e.g. Chromium-based editors).
///
/// This is a beta service used by GhostTextWindow for inline autocomplete.
/// </summary>
public static class CaretPositionHelper
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
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

    public struct CaretPosition
    {
        public double X;
        public double Y;
        public double Height;
        public bool IsFromCaret; // true = real caret, false = mouse fallback
    }

    /// <summary>
    /// Attempts to get the real text caret position via GetGUIThreadInfo.
    /// Falls back to mouse cursor position if the caret can't be found
    /// (common in Chromium-based apps, games, etc.).
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

            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(threadId, ref info))
                return GetMouseFallback();

            // If there's no caret window, the focused app isn't showing a text caret
            if (info.hwndCaret == IntPtr.Zero)
                return GetMouseFallback();

            // rcCaret is in client coordinates of hwndCaret — convert to screen coordinates
            var caretPoint = new POINT
            {
                X = info.rcCaret.Right, // Right edge of caret = where new text will appear
                Y = info.rcCaret.Top
            };

            if (!ClientToScreen(info.hwndCaret, ref caretPoint))
                return GetMouseFallback();

            int caretHeight = info.rcCaret.Bottom - info.rcCaret.Top;
            if (caretHeight <= 0) caretHeight = 20; // Sensible default

            return new CaretPosition
            {
                X = caretPoint.X,
                Y = caretPoint.Y,
                Height = caretHeight,
                IsFromCaret = true
            };
        }
        catch
        {
            return GetMouseFallback();
        }
    }

    /// <summary>
    /// Fallback: use mouse position when the caret can't be found.
    /// Ghost text positioned near the mouse is still better than nothing.
    /// </summary>
    private static CaretPosition GetMouseFallback()
    {
        if (GetCursorPos(out POINT pos))
        {
            return new CaretPosition
            {
                X = pos.X,
                Y = pos.Y,
                Height = 20,
                IsFromCaret = false
            };
        }

        return new CaretPosition { X = 100, Y = 100, Height = 20, IsFromCaret = false };
    }
}
