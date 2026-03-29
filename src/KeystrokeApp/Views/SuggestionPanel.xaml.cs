using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // Animation durations
    private static readonly Duration ShowDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration HideDuration = new(TimeSpan.FromMilliseconds(100));
    private static readonly IEasingFunction ShowEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction HideEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };

    private string _currentSuggestion = "";
    private string _currentPrefix = "";
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    // Multi-suggestion support
    private readonly List<string> _suggestions = new();
    private int _currentIndex;

    // Drag support
    private bool _isDragging;
    private bool _isDragged; // true when user has manually positioned the panel
    private Point _dragStartMouse;
    private double _dragStartLeft;
    private double _dragStartTop;

    // Animation state
    private bool _isAnimatingHide;

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

        // Reset drag state — new prediction snaps back to caret
        _isDragged = false;

        bool wasVisible = IsVisible && !_isAnimatingHide;

        if (!IsVisible || _isAnimatingHide)
        {
            _isAnimatingHide = false;
            Show();
            AnimateShow();
        }

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

        if (!IsVisible || _isAnimatingHide)
        {
            _isAnimatingHide = false;
            Show();
            AnimateShow();
        }
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

        // Reset drag state — new prediction snaps back to caret
        _isDragged = false;

        if (!IsVisible || _isAnimatingHide)
        {
            _isAnimatingHide = false;
            Show();
            AnimateShow();
        }

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

        if (IsVisible && !_isAnimatingHide)
            AnimateHide();
        else if (!IsVisible)
            return; // Already hidden
    }

    #region Animations

    private void AnimateShow()
    {
        // Cancel any in-progress hide
        _isAnimatingHide = false;

        // Fade in
        var fadeIn = new DoubleAnimation(0, 1, ShowDuration) { EasingFunction = ShowEase };
        BeginAnimation(OpacityProperty, fadeIn);

        // Slide up from 8px below
        var slideUp = new DoubleAnimation(8, 0, ShowDuration) { EasingFunction = ShowEase };
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private void AnimateHide()
    {
        _isAnimatingHide = true;

        // Fade out
        var fadeOut = new DoubleAnimation(Opacity, 0, HideDuration) { EasingFunction = HideEase };
        fadeOut.Completed += (_, _) =>
        {
            if (_isAnimatingHide)
            {
                _isAnimatingHide = false;
                _isDragged = false;
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);

        // Slide down slightly
        var slideDown = new DoubleAnimation(0, 4, HideDuration) { EasingFunction = HideEase };
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideDown);
    }

    #endregion

    #region Drag Support

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStartMouse = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        SuggestionBorder.CaptureMouse();
        e.Handled = true;
    }

    private void Border_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging) return;

        var currentMouse = PointToScreen(e.GetPosition(this));
        Left = _dragStartLeft + (currentMouse.X - _dragStartMouse.X);
        Top = _dragStartTop + (currentMouse.Y - _dragStartMouse.Y);
        e.Handled = true;
    }

    private void Border_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;

        _isDragging = false;
        _isDragged = true; // User has manually positioned — stop following caret
        SuggestionBorder.ReleaseMouseCapture();
        e.Handled = true;
    }

    #endregion

    /// <summary>
    /// Position the panel just below the text caret.
    /// Falls back to below the mouse cursor if caret can't be detected.
    /// Clamps to screen bounds so the panel never goes off-screen.
    /// Skipped if the user has manually dragged the panel.
    /// </summary>
    private void PositionNearCaret()
    {
        // If the user dragged the panel, keep their position
        if (_isDragged) return;

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
