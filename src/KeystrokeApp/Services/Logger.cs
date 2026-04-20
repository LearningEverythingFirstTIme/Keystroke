using System.IO;

namespace KeystrokeApp.Services;

/// <summary>
/// Shared file logger. Writes to %APPDATA%\Keystroke\debug.log with millisecond
/// precision and severity levels. Thread-safe. Swallows all IO errors — logging
/// must never cascade into a crash.
/// </summary>
internal static class Logger
{
    private static readonly object _lock = new();
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Keystroke", "debug.log");
    private static volatile bool _directoryEnsured;

    public static string LogPath => _path;

    public static void Info(string message)  => Write("INFO", message);
    public static void Warn(string message)  => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);
    public static void Debug(string message) => Write("DBG ", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                // Create %APPDATA%\Keystroke on first write — it may not exist
                // on a clean install if the logger is the first thing to touch it.
                if (!_directoryEnsured)
                {
                    var dir = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    _directoryEnsured = true;
                }

                File.AppendAllText(
                    _path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
