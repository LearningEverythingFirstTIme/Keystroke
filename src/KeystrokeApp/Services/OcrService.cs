using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace KeystrokeApp.Services;

/// <summary>
/// Captures the active window and runs Windows OCR to extract visible text.
/// Results are cached and only refreshed on demand (e.g. window focus change).
/// </summary>
public class OcrService : IDisposable
{
    private readonly OcrEngine? _ocrEngine;
    private readonly string _logPath;
    private volatile string? _cachedText;
    private volatile string _cachedForWindow = "";
    private int _captureCount;
    private volatile bool _disposed;

    /// <summary>
    /// Maximum characters to keep from OCR output.
    /// </summary>
    private const int MaxCachedLength = 2000;

    /// <summary>
    /// Maximum bitmap dimension to prevent excessive memory allocation on large/multi-monitor setups.
    /// </summary>
    private const int MaxCaptureDimension = 2560;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    public OcrService()
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keystroke", "ocr.log");

        _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
        Log(_ocrEngine != null ? "OCR engine initialized" : "OCR engine unavailable");
    }

    /// <summary>
    /// Get the most recently cached OCR text. Returns null if no capture has run.
    /// This is safe to call from the prediction path — it never blocks on OCR.
    /// </summary>
    public string? CachedText => _cachedText;

    /// <summary>
    /// Capture and OCR the active window. Call this from a background thread.
    /// Results are stored in CachedText for the prediction engine to read.
    /// </summary>
    public async Task CaptureAsync()
    {
        if (_ocrEngine == null || _disposed)
            return;

        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            if (!GetWindowRect(hwnd, out RECT rect) || rect.Width <= 0 || rect.Height <= 0)
                return;

            // Cap dimensions to prevent excessive memory use on large/multi-monitor setups
            var captureWidth = Math.Min(rect.Width, MaxCaptureDimension);
            var captureHeight = Math.Min(rect.Height, MaxCaptureDimension);

            // Capture the window region
            using var bitmap = new Bitmap(captureWidth, captureHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(captureWidth, captureHeight));
            }

            // Convert System.Drawing.Bitmap → WinRT SoftwareBitmap
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Bmp);
            ms.Seek(0, SeekOrigin.Begin);

            using var stream = new InMemoryRandomAccessStream();
            await ms.CopyToAsync(stream.AsStreamForWrite());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            // Run OCR
            var result = await _ocrEngine.RecognizeAsync(softwareBitmap);
            var text = result.Text?.Trim();

            if (!string.IsNullOrEmpty(text))
            {
                // Strip Keystroke's own UI text that the OCR picks up from the overlay
                text = StripKeystrokeUiText(text);

                // Keep a reasonable amount — tail end is most relevant (near cursor)
                if (text.Length > MaxCachedLength)
                    text = text[^MaxCachedLength..];

                _cachedText = text;
                Log($"Captured {text.Length} chars from OCR");
            }
        }
        catch (Exception ex)
        {
            Log($"Capture error: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if a new OCR capture should be triggered.
    /// Returns true on window change OR if enough time has passed for a refresh.
    /// </summary>
    public bool ShouldRecapture()
    {
        var (processName, windowTitle) = ActiveWindowService.GetActiveWindow();
        var windowKey = $"{processName}|{windowTitle}";

        if (windowKey != _cachedForWindow)
        {
            _cachedForWindow = windowKey;
            _captureCount = 0;
            Log($"Window changed → {processName} \"{windowTitle}\"");
            return true;
        }

        // Even for the same window, re-capture periodically (every ~4 ticks = 12s)
        // to pick up content changes like scrolling or new messages
        var count = Interlocked.Increment(ref _captureCount);
        if (count >= 4)
        {
            Interlocked.Exchange(ref _captureCount, 0);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Clear cached OCR text (e.g. when switching contexts).
    /// </summary>
    public void ClearCache()
    {
        _cachedText = null;
        _cachedForWindow = "";
    }

    /// <summary>
    /// Remove text that comes from Keystroke's own suggestion overlay,
    /// which the OCR will pick up if it's visible during capture.
    /// </summary>
    private static string StripKeystrokeUiText(string text)
    {
        // The suggestion panel shows a hint line and completion text
        // that OCR captures as part of the screen content
        string[] uiFragments =
        [
            "Tab to accept",
            "Esc to dismiss",
            "⌨️ Tab to accept · Esc to dismiss",
            "Tab to accept · Esc to dismiss",
            "Tab to accept Esc to dismiss",
        ];

        foreach (var fragment in uiFragments)
        {
            text = text.Replace(fragment, "", StringComparison.OrdinalIgnoreCase);
        }

        // Clean up any double-spaces or trailing whitespace left behind
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        return text.Trim();
    }

    private void Log(string msg)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
