using System.Runtime.InteropServices;

namespace KeystrokeApp.Services;

/// <summary>
/// Gets the mouse cursor position for panel positioning.
/// </summary>
public static class CursorPositionHelper
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public struct CursorPosition
    {
        public double X;
        public double Y;
    }

    /// <summary>
    /// Get the mouse cursor position in screen coordinates.
    /// </summary>
    public static CursorPosition GetMousePosition()
    {
        if (GetCursorPos(out POINT pos))
        {
            return new CursorPosition
            {
                X = pos.X,
                Y = pos.Y + 20 // Offset below mouse cursor
            };
        }

        return new CursorPosition { X = 100, Y = 100 };
    }
}
