using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KeystrokeApp.Services;

/// <summary>
/// Detects the currently active (foreground) window's process name and title.
/// Uses Win32 P/Invoke — fast enough (~0.1ms) to call on every prediction request.
/// </summary>
public static class AppContextService
{
    public sealed record VisibleAppInfo(string ProcessName, string WindowTitle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // Hot path: called on every keystroke. Track the last logged failure reason so
    // we surface a single WARN per distinct failure mode instead of flooding the log.
    private static string _lastLoggedFailureReason = "";

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
            {
                LogFailureOnce("foreground hwnd was zero");
                return ("", "");
            }

            // Window title
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();

            // Process name
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
            {
                LogFailureOnce("GetWindowThreadProcessId returned pid=0");
                return ("", title);
            }
            using var process = Process.GetProcessById((int)pid);
            var processName = process.ProcessName;

            return (processName, title);
        }
        catch (Exception ex)
        {
            LogFailureOnce($"{ex.GetType().Name}: {ex.Message}");
            return ("", "");
        }
    }

    private static void LogFailureOnce(string reason)
    {
        if (reason == _lastLoggedFailureReason) return;
        _lastLoggedFailureReason = reason;
        Logger.Warn($"AppContextService.GetActiveWindow failed: {reason}");
    }

    public static List<VisibleAppInfo> GetVisibleApps(string? excludedProcessName = null)
    {
        var excluded = PerAppSettings.NormalizeProcessName(excludedProcessName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<VisibleAppInfo>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.HasExited || process.MainWindowHandle == IntPtr.Zero)
                    continue;

                var processName = process.ProcessName;
                var normalized = PerAppSettings.NormalizeProcessName(processName);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (!string.IsNullOrWhiteSpace(excluded) &&
                    string.Equals(normalized, excluded, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var title = process.MainWindowTitle?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                if (!seen.Add(normalized))
                    continue;

                results.Add(new VisibleAppInfo(processName, title));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return results
            .OrderBy(app => PerAppSettings.NormalizeProcessName(app.ProcessName), StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.WindowTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
