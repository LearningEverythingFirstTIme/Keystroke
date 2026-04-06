using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

/// <summary>
/// Ghost Text Overlay (Beta Feature)
///
/// A fully transparent, click-through window that renders autocomplete suggestions
/// as faded inline text at the caret position — mimicking the ghost text UX of
/// VS Code Copilot, Gmail Smart Compose, etc.
///
/// Design constraints:
///   - Zero visual chrome (no border, no background, no shadow)
///   - Fully click-through (WS_EX_TRANSPARENT) so it never steals focus or blocks input
///   - Positioned at the real text caret via GetGUIThreadInfo, not the mouse cursor
///   - Lightweight animations: subtle fade-in/out only, no springs or slides
///   - Coexists with SuggestionPanel — both can be active simultaneously (panel shows
///     alternatives + hints, ghost shows the primary suggestion inline)
///
/// Known limitations (beta):
///   - Font/size won't match every target app (uses Cascadia Code 14px as a baseline)
///   - Chromium-based apps don't expose a Win32 caret, so we fall back to mouse position
///   - AllowsTransparency=True forces software rendering (same as SuggestionPanel)
/// </summary>
public partial class GhostTextWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE   = 0x08000000;
    private const int WS_EX_TOOLWINDOW   = 0x00000080;
    private const int WS_EX_TRANSPARENT  = 0x00000020; // Click-through
    private const int WS_EX_LAYERED      = 0x00080000;

    private static readonly Duration FadeInDuration  = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(80));
    private static readonly IEasingFunction FadeEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    private string _currentText = "";
    private bool _isAnimatingHide;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    /// <summary>
    /// Tracks whether the last positioning was from a real caret or mouse fallback.
    /// Exposed so callers can decide whether to show ghost text at all (mouse fallback
    /// looks wrong in most cases).
    /// </summary>
    public bool LastPositionWasFromCaret { get; private set; }

    public GhostTextWindow()
    {
        InitializeComponent();
    }

    #region Public API

    /// <summary>
    /// Show ghost text at the current caret position.
    /// Call this when a new suggestion arrives or streaming updates.
    /// </summary>
    public void ShowGhostText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            HideGhostText();
            return;
        }

        // Trim leading space — ghost text should start flush with the caret
        _currentText = text.TrimStart();
        GhostText.Text = _currentText;

        PositionAtCaret();

        if (!IsVisible || _isAnimatingHide)
        {
            _isAnimatingHide = false;
            Show();
            AnimateFadeIn();
        }
    }

    /// <summary>
    /// Update ghost text content without repositioning (for streaming chunks).
    /// </summary>
    public void AppendGhostText(string additionalText)
    {
        _currentText += additionalText;
        GhostText.Text = _currentText.TrimStart();

        if (!IsVisible || _isAnimatingHide)
        {
            _isAnimatingHide = false;
            Show();
            PositionAtCaret();
            AnimateFadeIn();
        }
    }

    /// <summary>
    /// Begin a new streaming session — clear text and show the cursor-anchored window.
    /// Unlike SuggestionPanel, ghost text shows nothing during loading (no "Thinking...").
    /// It only becomes visible when the first chunk arrives.
    /// </summary>
    public void BeginStreaming()
    {
        _currentText = "";
        GhostText.Text = "";

        // Don't show yet — wait for first chunk in AppendGhostText
        if (IsVisible)
            HideGhostText();
    }

    /// <summary>
    /// Hide the ghost text with a quick fade-out.
    /// </summary>
    public void HideGhostText()
    {
        _currentText = "";
        GhostText.Text = "";

        if (IsVisible && !_isAnimatingHide)
            AnimateFadeOut();
    }

    /// <summary>
    /// Flash the ghost text to white briefly on acceptance (visual confirmation).
    /// </summary>
    public void FlashAccept()
    {
        if (!IsVisible) return;

        var brush = new SolidColorBrush(Color.FromArgb(0x60, 0xA0, 0xA8, 0xC0));
        GhostText.Foreground = brush;

        var flash = new ColorAnimation(
            Color.FromArgb(0x60, 0xA0, 0xA8, 0xC0),
            Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF),
            TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = FadeEase,
            AutoReverse = true
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, flash);
    }

    public bool HasGhostText => IsVisible && !string.IsNullOrEmpty(_currentText);

    #endregion

    #region Positioning

    /// <summary>
    /// Position the ghost text window at the text caret in the active application.
    /// Uses CaretPositionHelper which calls GetGUIThreadInfo for the real caret,
    /// falling back to mouse position for apps without a standard caret.
    /// </summary>
    private void PositionAtCaret()
    {
        var caret = CaretPositionHelper.GetCaretPosition();
        LastPositionWasFromCaret = caret.IsFromCaret;

        // Convert screen pixels to WPF device-independent units
        double x = caret.X / _dpiScaleX;
        double y = caret.Y / _dpiScaleY;

        // Position ghost text at the caret's right edge, vertically centered on the caret
        // Small horizontal gap (2px) so text doesn't butt up against the caret cursor
        Left = x + (2.0 / _dpiScaleX);
        Top  = y;

        // Clamp to work area so ghost text doesn't fly off-screen
        var workArea = SystemParameters.WorkArea;
        double panelWidth = Math.Max(ActualWidth, 50);
        double panelHeight = Math.Max(ActualHeight, 16);

        if (Left + panelWidth > workArea.Right - 4)
            Left = workArea.Right - panelWidth - 4;
        if (Top + panelHeight > workArea.Bottom - 4)
            Top = workArea.Bottom - panelHeight - 4;

        Left = Math.Max(workArea.Left, Left);
        Top  = Math.Max(workArea.Top, Top);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // Don't reposition on size change — ghost text anchors to the caret position
        // at the moment it was shown, and the text grows rightward from there.
    }

    #endregion

    #region Animations

    private void AnimateFadeIn()
    {
        var fadeIn = new DoubleAnimation(0, 1, FadeInDuration) { EasingFunction = FadeEase };
        BeginAnimation(OpacityProperty, fadeIn);
    }

    private void AnimateFadeOut()
    {
        _isAnimatingHide = true;

        // Clear previous animation first
        BeginAnimation(OpacityProperty, null);

        var fadeOut = new DoubleAnimation(Opacity, 0, FadeOutDuration) { EasingFunction = FadeEase };
        fadeOut.Completed += (_, _) =>
        {
            if (_isAnimatingHide)
            {
                _isAnimatingHide = false;
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    #endregion

    #region Window Setup

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);

        // WS_EX_TRANSPARENT: mouse events pass through to the window below
        // WS_EX_NOACTIVATE:  never steals focus
        // WS_EX_TOOLWINDOW:  hidden from Alt-Tab
        // WS_EX_LAYERED:     required for click-through with transparency
        SetWindowLong(helper.Handle, GWL_EXSTYLE,
            exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED);

        // Capture DPI for coordinate conversion
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

    #endregion
}
