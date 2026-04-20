using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace KeystrokeApp.Services;

/// <summary>
/// One-shot host probes captured at startup so bug reports include the
/// machine fingerprint without needing remote access. Kept light: no
/// live polling, no caching that would mask later changes.
/// </summary>
internal static class SystemDiagnostics
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    private const int SM_CMONITORS = 80;
    private const int LOGPIXELSX = 88;

    public static int MonitorCount
    {
        get { try { return GetSystemMetrics(SM_CMONITORS); } catch { return -1; } }
    }

    public static int SystemDpi
    {
        get
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = GetDC(IntPtr.Zero);
                return hdc == IntPtr.Zero ? -1 : GetDeviceCaps(hdc, LOGPIXELSX);
            }
            catch { return -1; }
            finally { if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc); }
        }
    }

    public static string DpiAwareness
    {
        get
        {
            try
            {
                var ctx = GetThreadDpiAwarenessContext();
                // Compare against the well-known sentinel values. These are defined
                // by Windows as negative handles; matching by equality is the only
                // supported way to identify the active mode.
                if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-4))) return "PerMonitorV2";
                if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-3))) return "PerMonitorV1";
                if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-2))) return "System";
                if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-1))) return "Unaware";
                if (AreDpiAwarenessContextsEqual(ctx, new IntPtr(-5))) return "UnawareGdiScaled";
                return "unknown";
            }
            catch { return "unavailable"; }
        }
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static bool IsDirectoryWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
