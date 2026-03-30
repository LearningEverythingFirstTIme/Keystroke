using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KeystrokeApp.Services;

/// <summary>
/// Detects the currently active (foreground) window's process name and title.
/// Uses Win32 P/Invoke — fast enough (~0.1ms) to call on every prediction request.
/// </summary>
public static class ActiveWindowService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// Get info about the currently focused window.
    /// Returns (processName, windowTitle). Both may be empty on failure.
    /// </summary>
    public static (string ProcessName, string WindowTitle) GetActiveWindow()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return ("", "");

            // Window title
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();

            // Process name
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return ("", title);
            using var process = Process.GetProcessById((int)pid);
            var processName = process.ProcessName;

            return (processName, title);
        }
        catch
        {
            return ("", "");
        }
    }
}
