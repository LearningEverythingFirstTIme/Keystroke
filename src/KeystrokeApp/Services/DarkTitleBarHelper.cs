using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KeystrokeApp.Services;

/// <summary>
/// Requests the Windows dark title bar on any WPF window.
/// Uses DWMWA_USE_IMMERSIVE_DARK_MODE (attribute 20), available on Windows 10 2004+.
/// Silently no-ops on older builds.
/// </summary>
public static class DarkTitleBarHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void Apply(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        int value = 1;
        DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
