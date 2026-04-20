using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KeystrokeApp.Services;

/// <summary>
/// Requests the Windows dark title bar on any WPF window.
/// Uses DWMWA_USE_IMMERSIVE_DARK_MODE (attribute 20) on Windows 10 2004+ (build 19041+),
/// falls back to the pre-release attribute number 19 on Windows 10 1809–1909.
/// DwmSetWindowAttribute is marked PreserveSig so an unsupported attribute returns
/// E_INVALIDARG instead of throwing — any failure is silently absorbed.
/// </summary>
public static class DarkTitleBarHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void Apply(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        int value = 1;
        int attr = Environment.OSVersion.Version.Build >= 19041
            ? DWMWA_USE_IMMERSIVE_DARK_MODE
            : DWMWA_USE_IMMERSIVE_DARK_MODE_OLD;
        DwmSetWindowAttribute(helper.Handle, attr, ref value, sizeof(int));
    }
}
