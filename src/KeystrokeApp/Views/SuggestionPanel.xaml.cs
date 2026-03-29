using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

public partial class SuggestionPanel : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    // Gap between the caret and the panel
    private const double CaretGap = 4;
    // Margin from screen edges to prevent overflow
    private const double ScreenEdgeMargin = 10;

    private string _currentSuggestion = "";
    private string _currentPrefix = "";
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    // Multi-suggestion support
    private readonly List<string> _suggestions = new();
    private int _currentIndex;

    public SuggestionPanel()
    {
        InitializeComponent();
    }

    public void ShowSuggestion(string prefix, string completion)
    {
        // Add space if needed
        if (!string.IsNullOrEmpty(completion) && !prefix.EndsWith(" ") && !completion.StartsWith(" "))
            completion = " " + completion;

        _currentPrefix = prefix;
        _currentSuggestion = completion;
        _suggestions.Clear();
        _suggestions.Add(completion);
        _currentIndex = 0;

        CompletionText.Text = completion;
        UpdateCounter();

        if (!IsVisible)
            Show();

        PositionNearCaret();
    }

    /// <summary>
    /// Append more text to a streaming suggestion. Shows immediately if not visible.
    /// </summary>
    public void AppendSuggestion(string prefix, string additionalText)
    {
        _currentPrefix = prefix;
        _currentSuggestion += additionalText;

        // Keep slot 0 in sync with the streaming text
        if (_suggestions.Count == 0)
            _suggestions.Add(_currentSuggestion);
        else
            _suggestions[0] = _currentSuggestion;

        CompletionText.Text = _currentSuggestion;

        if (!IsVisible)
            Show();
    }

    /// <summary>
    /// Start a new streaming suggestion — clears previous text and shows the panel.
    /// </summary>
    public void BeginStreamingSuggestion(string prefix)
    {
        _currentPrefix = prefix;
        _currentSuggestion = "";
        _suggestions.Clear();
        _currentIndex = 0;
        CompletionText.Text = "";
        CounterText.Visibility = Visibility.Collapsed;
        HintText.Text = "Tab accept · Ctrl+→ word · Esc dismiss";

        if (!IsVisible)
            Show();

        PositionNearCaret();
    }

    /// <summary>
    /// Add alternative suggestions (from a background candidateCount request).
    /// Does not change the currently displayed suggestion.
    /// </summary>
    public void SetAlternatives(string prefix, List<string> alternatives)
    {
        if (prefix != _currentPrefix || alternatives.Count == 0)
            return;

        // Slot 0 is the primary (already showing). Add alternatives that differ.
        foreach (var alt in alternatives)
        {
            var normalized = alt.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(normalized) && !_suggestions.Contains(normalized))
                _suggestions.Add(normalized);
        }

        UpdateCounter();
    }

    /// <summary>
    /// Cycle to the next suggestion. Returns true if cycled.
    /// </summary>
    public bool NextSuggestion()
    {
        if (_suggestions.Count <= 1) return false;

        _currentIndex = (_currentIndex + 1) % _suggestions.Count;
        ShowCurrentIndex();
        return true;
    }

    /// <summary>
    /// Cycle to the previous suggestion. Returns true if cycled.
    /// </summary>
    public bool PreviousSuggestion()
    {
        if (_suggestions.Count <= 1) return false;

        _currentIndex = (_currentIndex - 1 + _suggestions.Count) % _suggestions.Count;
        ShowCurrentIndex();
        return true;
    }

    private void ShowCurrentIndex()
    {
        _currentSuggestion = _suggestions[_currentIndex];
        CompletionText.Text = _currentSuggestion;
        UpdateCounter();
    }

    private void UpdateCounter()
    {
        if (_suggestions.Count > 1)
        {
            CounterText.Text = $"{_currentIndex + 1}/{_suggestions.Count}";
            CounterText.Visibility = Visibility.Visible;
            HintText.Text = "Tab accept · Ctrl+→ word · Ctrl+↑↓ cycle · Esc dismiss";
        }
        else
        {
            CounterText.Visibility = Visibility.Collapsed;
            HintText.Text = "Tab accept · Ctrl+→ word · Esc dismiss";
        }
    }

    public void HideSuggestion()
    {
        _currentPrefix = "";
        _currentSuggestion = "";
        _suggestions.Clear();
        _currentIndex = 0;
        CompletionText.Text = "";
        CounterText.Visibility = Visibility.Collapsed;
        HintText.Text = "Tab accept · Ctrl+→ word · Esc dismiss";
        if (IsVisible)
            Hide();
    }

    /// <summary>
    /// Position the panel just below the text caret.
    /// Falls back to below the mouse cursor if caret can't be detected.
    /// Clamps to screen bounds so the panel never goes off-screen.
    /// </summary>
    private void PositionNearCaret()
    {
        var caret = CursorPositionHelper.GetCaretPosition();
        var workArea = SystemParameters.WorkArea;

        double panelWidth = Math.Max(ActualWidth, 200);
        double panelHeight = Math.Max(ActualHeight, 60);

        // Convert from physical pixels (Win32) to WPF device-independent pixels
        double caretX = caret.X / _dpiScaleX;
        double caretY = caret.Y / _dpiScaleY;
        double caretH = caret.Height / _dpiScaleY;

        // Position below the caret, aligned to the left edge of the caret
        double x = caretX;
        double y = caretY + CaretGap;

        // If the panel would go off the right edge, shift left
        if (x + panelWidth > workArea.Right - ScreenEdgeMargin)
            x = workArea.Right - panelWidth - ScreenEdgeMargin;

        // If the panel would go below the screen, show it above the caret instead
        if (y + panelHeight > workArea.Bottom - ScreenEdgeMargin)
            y = caretY - caretH - panelHeight - CaretGap;

        // Clamp to screen bounds
        x = Math.Max(workArea.Left + ScreenEdgeMargin, x);
        y = Math.Max(workArea.Top + ScreenEdgeMargin, y);

        Left = x;
        Top = y;
    }

    /// <summary>
    /// When content changes size, reposition to stay near the caret.
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsVisible)
            PositionNearCaret();
    }

    public string GetFullSuggestion() => _currentPrefix + _currentSuggestion;
    public bool HasSuggestion => IsVisible && !string.IsNullOrEmpty(_currentSuggestion);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // Capture DPI scale — Win32 APIs return physical pixels,
        // but WPF Left/Top use device-independent pixels (96 DPI base)
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
