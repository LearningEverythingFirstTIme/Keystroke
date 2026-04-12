using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

public partial class SuggestionPanel : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const double CaretGap = 4;
    private const double ScreenEdgeMargin = 10;

    private static readonly Duration ShowDuration = new(TimeSpan.FromMilliseconds(280));
    private static readonly Duration HideDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration WordRevealDuration = new(TimeSpan.FromMilliseconds(40));

    // Position/fade easing: fast deceleration, no overshoot — clean landing
    private static readonly IEasingFunction ShowPositionEase = new ExponentialEase
    {
        EasingMode = EasingMode.EaseOut,
        Exponent = 4
    };
    // Scale easing: springy settle so the panel "clicks" into place
    private static readonly IEasingFunction ShowScaleEase = new BackEase
    {
        EasingMode = EasingMode.EaseOut,
        Amplitude = 0.25
    };
    private static readonly IEasingFunction HideEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction WordRevealEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };

    // Theme-driven colors — updated live by ApplyTheme()
    private Color _normalBorderColor  = ThemeDefinitions.Midnight.NormalBorder;
    private Color _sweepPeakColor     = ThemeDefinitions.Midnight.SweepPeak;
    private Color _sweepSoftColor     = ThemeDefinitions.Midnight.SweepSoft;
    private Color _streamingFlashColor = ThemeDefinitions.Midnight.StreamingFlash;

    private string _currentSuggestion = "";
    private string _currentPrefix = "";
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    private readonly List<string> _suggestions = new();
    private int _currentIndex;

    private bool _isDragging;
    private bool _isDragged;
    private Point _dragStartMouse;
    private double _dragStartLeft;
    private double _dragStartTop;

    private bool _isAnimatingHide;
    private bool _isUsingWordAnimation;
    private int _revealedWordCount;

    private DispatcherTimer? _loadingDotTimer;
    private int _loadingDotCount;
    private LinearGradientBrush? _sweepBrush;
    private double _sweepAngle;
    private DispatcherTimer? _sweepTimer;

    public SuggestionPanel()
    {
        InitializeComponent();
    }

    #region Public API

    public void ShowSuggestion(string prefix, string completion)
    {
        if (!string.IsNullOrEmpty(completion) && !prefix.EndsWith(" ") && !completion.StartsWith(" "))
            completion = " " + completion;

        _currentPrefix = prefix;
        _currentSuggestion = completion;
        _suggestions.Clear();
        _suggestions.Add(completion);
        _currentIndex = 0;
        _isUsingWordAnimation = false;
        _revealedWordCount = 0;

        CompletionWordsPanel.Visibility = Visibility.Collapsed;
        CompletionText.Visibility = Visibility.Visible;
        CompletionText.Text = completion;
        UpdateCounter();

        _isDragged = false;
        StopLoadingAnimation();

        if (!IsVisible || _isAnimatingHide)
            ShowAndAnimate();
        else
            PositionNearCursor();
    }

    public void AppendSuggestion(string prefix, string additionalText)
    {
        _currentPrefix = prefix;
        var previousText = _currentSuggestion;
        _currentSuggestion += additionalText;

        if (_suggestions.Count == 0)
            _suggestions.Add(_currentSuggestion);
        else
            _suggestions[0] = _currentSuggestion;

        if (!_isUsingWordAnimation && _currentSuggestion.Length > 20)
            SwitchToWordAnimation();

        if (_isUsingWordAnimation)
            UpdateWordAnimation(previousText, additionalText);
        else
            CompletionText.Text = _currentSuggestion;

        if (!IsVisible || _isAnimatingHide)
            ShowAndAnimate();
    }

    public void BeginStreamingSuggestion(string prefix)
    {
        _currentPrefix = prefix;
        _currentSuggestion = "";
        _suggestions.Clear();
        _currentIndex = 0;
        _isUsingWordAnimation = false;
        _revealedWordCount = 0;
        
        CompletionWordsPanel.Visibility = Visibility.Collapsed;
        CompletionWordsPanel.Children.Clear();
        CompletionText.Visibility = Visibility.Visible;
        CompletionText.Text = "";
        
        CounterText.Visibility = Visibility.Collapsed;
        HintText.Text = "Tab accept · Ctrl+\u2192 word · Esc dismiss";

        _isDragged = false;
        StartLoadingAnimation();

        if (!IsVisible || _isAnimatingHide)
            ShowAndAnimate();
        else
            PositionNearCursor();
    }

    public void SetAlternatives(string prefix, List<string> alternatives)
    {
        if (prefix != _currentPrefix || alternatives.Count == 0)
            return;

        // The primary suggestion (from streaming) may have a leading space prepended.
        // Normalize both sides for dedup so we don't show near-identical suggestions.
        bool prefixNeedsSpace = !string.IsNullOrEmpty(prefix) && !prefix.EndsWith(" ");

        foreach (var alt in alternatives)
        {
            var normalized = alt.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            // Check if this alternative duplicates any existing suggestion (ignoring leading space)
            bool isDuplicate = _suggestions.Any(existing =>
                string.Equals(existing.TrimStart(), normalized.TrimStart(), StringComparison.OrdinalIgnoreCase));
            if (isDuplicate)
                continue;

            // Match the leading-space convention of the primary suggestion
            if (prefixNeedsSpace && !normalized.StartsWith(" "))
                normalized = " " + normalized;

            _suggestions.Add(normalized);
        }

        UpdateCounter();
    }

    public bool NextSuggestion()
    {
        if (_suggestions.Count <= 1) return false;
        _currentIndex = (_currentIndex + 1) % _suggestions.Count;
        ShowCurrentIndex();
        return true;
    }

    public bool PreviousSuggestion()
    {
        if (_suggestions.Count <= 1) return false;
        _currentIndex = (_currentIndex - 1 + _suggestions.Count) % _suggestions.Count;
        ShowCurrentIndex();
        return true;
    }

    public void HideSuggestion()
    {
        _currentPrefix = "";
        _currentSuggestion = "";
        _suggestions.Clear();
        _currentIndex = 0;
        _isUsingWordAnimation = false;
        _revealedWordCount = 0;
        
        CompletionText.Text = "";
        CompletionWordsPanel.Children.Clear();
        CompletionWordsPanel.Visibility = Visibility.Collapsed;
        CompletionText.Visibility = Visibility.Visible;
        
        CounterText.Visibility = Visibility.Collapsed;
        HintText.Text = "Tab accept · Ctrl+\u2192 word · Esc dismiss";

        StopLoadingAnimation();

        if (IsVisible && !_isAnimatingHide)
            AnimateHide();
        else if (!IsVisible)
            return;
    }

    public void OnStreamingComplete()
    {
        if (!IsVisible) return;

        StopLoadingAnimation();

        // Flash the border with the brand accent — bright blue-violet, same family as the sweep
        var flashBrush = new SolidColorBrush(_normalBorderColor);
        SuggestionBorder.BorderBrush = flashBrush;

        var borderFlash = new ColorAnimation(
            _normalBorderColor,
            _streamingFlashColor,
            TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            AutoReverse = true
        };
        flashBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderFlash);
    }

    public void AcceptSuggestion()
    {
        if (!IsVisible) return;
        StopLoadingAnimation();

        // Border: vivid green flash — intentional contrast with the blue-violet brand accent
        var borderBrush = new SolidColorBrush(_normalBorderColor);
        SuggestionBorder.BorderBrush = borderBrush;
        var borderFlash = new ColorAnimation(
            _normalBorderColor,
            Color.FromArgb(0xFF, 0x50, 0xFF, 0x80),
            TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            AutoReverse = true
        };
        borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderFlash);

        // Background: brief green-tinted wash so the whole panel acknowledges the accept
        var bgBrush = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x18, 0x25));
        SuggestionBorder.Background = bgBrush;
        var bgFlash = new ColorAnimation(
            Color.FromArgb(0xD8, 0x10, 0x18, 0x25),
            Color.FromArgb(0xD8, 0x13, 0x27, 0x22),
            TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            AutoReverse = true
        };
        bgBrush.BeginAnimation(SolidColorBrush.ColorProperty, bgFlash);

        // Scale: satisfying pop — noticeably larger than before (1.02 → 1.05)
        var confirmScale = new DoubleAnimation(1.0, 1.05, TimeSpan.FromMilliseconds(90))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            AutoReverse = true
        };
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, confirmScale);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, confirmScale);

        // Text: flash to white so the accepted text visibly lights up
        if (!_isUsingWordAnimation && CompletionText.Visibility == Visibility.Visible)
        {
            var textBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0));
            CompletionText.Foreground = textBrush;
            var textFlash = new ColorAnimation(
                Color.FromRgb(0xE0, 0xE0, 0xF0),
                Color.FromRgb(0xFF, 0xFF, 0xFF),
                TimeSpan.FromMilliseconds(80))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                AutoReverse = true
            };
            textBrush.BeginAnimation(SolidColorBrush.ColorProperty, textFlash);
        }
    }

    public string GetFullSuggestion() => _currentPrefix + _currentSuggestion;
    public string CurrentCompletion => _currentSuggestion;
    public string CurrentPrefix => _currentPrefix;
    public int SuggestionCount => _suggestions.Count;
    public bool HasSuggestion => IsVisible && !string.IsNullOrEmpty(_currentSuggestion);

    public void ApplyTheme(PanelTheme theme)
    {
        _normalBorderColor   = theme.NormalBorder;
        _sweepPeakColor      = theme.SweepPeak;
        _sweepSoftColor      = theme.SweepSoft;
        _streamingFlashColor = theme.StreamingFlash;

        PanelGlow.Color = theme.ShadowColor;

        // Update resting border when not in loading state
        if (_sweepTimer == null)
            SuggestionBorder.BorderBrush = new SolidColorBrush(_normalBorderColor);

        // Hot-update the sweep gradient if it's currently animating
        if (_sweepBrush != null)
        {
            _sweepBrush.GradientStops[1].Color = _sweepSoftColor;
            _sweepBrush.GradientStops[2].Color = _sweepPeakColor;
            _sweepBrush.GradientStops[3].Color = _sweepSoftColor;
        }
    }

    #endregion

    #region Loading Animation

    private void StartLoadingAnimation()
    {
        StopLoadingAnimation();

        LoadingIndicator.Visibility = Visibility.Visible;
        _loadingDotCount = 0;
        LoadingDots.Text = "";

        _loadingDotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _loadingDotTimer.Tick += (_, _) =>
        {
            _loadingDotCount = (_loadingDotCount + 1) % 4;
            LoadingDots.Text = new string('.', _loadingDotCount);
        };
        _loadingDotTimer.Start();

        // Sweep a blue-violet highlight around the border while the AI is thinking
        _sweepAngle = 0;
        _sweepBrush = new LinearGradientBrush
        {
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0.00),
                new GradientStop(_sweepSoftColor,    0.35),
                new GradientStop(_sweepPeakColor,    0.50),
                new GradientStop(_sweepSoftColor,    0.65),
                new GradientStop(Colors.Transparent, 1.00),
            }
        };
        UpdateSweepBrush(0);
        SuggestionBorder.BorderBrush = _sweepBrush;

        // 33ms (~30fps) — the sweep is slow-moving so 30fps is indistinguishable from 60fps,
        // but halves the software re-render cost caused by AllowsTransparency=True.
        _sweepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _sweepTimer.Tick += (_, _) =>
        {
            _sweepAngle = (_sweepAngle + 6.0) % 360.0; // 6° per tick keeps the same ~1.9s/rev
            UpdateSweepBrush(_sweepAngle);
        };
        _sweepTimer.Start();
    }

    private void StopLoadingAnimation()
    {
        _loadingDotTimer?.Stop();
        _loadingDotTimer = null;
        _sweepTimer?.Stop();
        _sweepTimer = null;
        _sweepBrush = null;
        LoadingIndicator.Visibility = Visibility.Collapsed;
        SuggestionBorder.BorderBrush = new SolidColorBrush(_normalBorderColor);
    }

    private void UpdateSweepBrush(double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double dx = Math.Cos(rad) * 0.5;
        double dy = Math.Sin(rad) * 0.5;
        _sweepBrush!.StartPoint = new Point(0.5 - dx, 0.5 - dy);
        _sweepBrush.EndPoint   = new Point(0.5 + dx, 0.5 + dy);
    }

    #endregion

    #region Word Animation

    private void SwitchToWordAnimation()
    {
        _isUsingWordAnimation = true;
        
        var words = _currentSuggestion.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        
        CompletionText.Visibility = Visibility.Collapsed;
        CompletionWordsPanel.Visibility = Visibility.Visible;
        CompletionWordsPanel.Children.Clear();
        
        foreach (var word in words)
        {
            var wordBlock = CreateWordBlock(word);
            wordBlock.Opacity = 1;
            CompletionWordsPanel.Children.Add(wordBlock);
        }
        
        _revealedWordCount = words.Length;
    }

    private void UpdateWordAnimation(string previousText, string newText)
    {
        var fullText = _currentSuggestion;
        var words = fullText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var previousWords = previousText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var previousWordCount = previousWords.Length;
        
        for (int i = CompletionWordsPanel.Children.Count; i < words.Length; i++)
        {
            var wordBlock = CreateWordBlock(words[i]);
            wordBlock.Opacity = 0;
            
            var scaleTransform = new ScaleTransform(0.9, 0.9);
            wordBlock.RenderTransform = scaleTransform;
            wordBlock.RenderTransformOrigin = new Point(0.5, 0.5);
            
            CompletionWordsPanel.Children.Add(wordBlock);
            
            var stagger = TimeSpan.FromMilliseconds((i - previousWordCount) * 20);
            
            var fadeIn = new DoubleAnimation(0, 1, WordRevealDuration)
            {
                EasingFunction = WordRevealEase,
                BeginTime = stagger
            };
            
            var scaleUp = new DoubleAnimation(0.9, 1.0, WordRevealDuration)
            {
                EasingFunction = WordRevealEase,
                BeginTime = stagger
            };
            
            wordBlock.BeginAnimation(OpacityProperty, fadeIn);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp);
        }
        
        _revealedWordCount = words.Length;
    }

    private static TextBlock CreateWordBlock(string word)
    {
        return new TextBlock
        {
            Text = word + " ",
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0)
        };
    }

    #endregion

    #region Suggestion Cycling

    private void ShowCurrentIndex()
    {
        _currentSuggestion = _suggestions[_currentIndex];
        
        _isUsingWordAnimation = false;
        CompletionWordsPanel.Visibility = Visibility.Collapsed;
        CompletionText.Visibility = Visibility.Visible;
        CompletionText.Text = _currentSuggestion;
        
        UpdateCounter();
        StopLoadingAnimation();
        
        var pulseScale = new DoubleAnimation(0.98, 1.0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
        };
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulseScale);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulseScale);
    }

    private void UpdateCounter()
    {
        if (_suggestions.Count > 1)
        {
            CounterText.Text = $"{_currentIndex + 1}/{_suggestions.Count}";
            CounterText.Visibility = Visibility.Visible;
            HintText.Text = "Tab accept · Ctrl+\u2192 word · Ctrl+\u2191\u2193 cycle · Esc dismiss";
        }
        else
        {
            CounterText.Visibility = Visibility.Collapsed;
            HintText.Text = "Tab accept · Ctrl+\u2192 word · Esc dismiss";
        }
    }

    #endregion

    #region Show/Hide Animations

    private void ShowAndAnimate()
    {
        _isAnimatingHide = false;
        Show();

        // Park off-screen while Opacity is still 0 so the layout pass (which
        // runs at Render priority) completes with real dimensions before we
        // position and animate. Without this, ActualWidth/Height are 0 on the
        // first PositionNearCursor() call, causing a visible jump when
        // OnRenderSizeChanged fires with the real size.
        Left = -9999;
        Top = -9999;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            // Guard: a hide may have been requested before the layout pass finished
            if (_isAnimatingHide) return;
            PositionNearCursor();
            AnimateShow();
        });
    }

    private void AnimateShow()
    {
        _isAnimatingHide = false;

        // Scale: restrained settle from 92% to full size
        var scaleX = new DoubleAnimation(0.92, 1.0, ShowDuration) { EasingFunction = ShowScaleEase };
        var scaleY = new DoubleAnimation(0.92, 1.0, ShowDuration) { EasingFunction = ShowScaleEase };
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        // Fade: sharp deceleration so it's opaque quickly
        var fadeIn = new DoubleAnimation(0, 1, ShowDuration) { EasingFunction = ShowPositionEase };
        BeginAnimation(OpacityProperty, fadeIn);

        // Fly in from below-right — panel sweeps up-left to settle at caret
        var slideX = new DoubleAnimation(8, 0, ShowDuration) { EasingFunction = ShowPositionEase };
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, slideX);

        var slideY = new DoubleAnimation(18, 0, ShowDuration) { EasingFunction = ShowPositionEase };
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideY);
    }

    private void AnimateHide()
    {
        _isAnimatingHide = true;

        // Clear any previous animations to prevent event handler accumulation
        BeginAnimation(OpacityProperty, null);

        // Stop X slide — should already be at 0, snap it clean
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.X = 0;

        // Scale UP slightly so the panel clears the eye without feeling floaty
        var scaleUpX = new DoubleAnimation(1.0, 1.02, HideDuration) { EasingFunction = HideEase };
        var scaleUpY = new DoubleAnimation(1.0, 1.02, HideDuration) { EasingFunction = HideEase };
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUpX);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUpY);

        var fadeOut = new DoubleAnimation(Opacity, 0, HideDuration) { EasingFunction = HideEase };
        fadeOut.Completed += (_, _) =>
        {
            if (_isAnimatingHide)
            {
                _isAnimatingHide = false;
                _isDragged = false;
                Hide();

                // Reset to fly-in starting state for next appearance
                ScaleTransform.ScaleX = 0.92;
                ScaleTransform.ScaleY = 0.92;
                SlideTransform.X = 8;
                SlideTransform.Y = 18;

                SuggestionBorder.BorderBrush = new SolidColorBrush(_normalBorderColor);
                SuggestionBorder.Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x18, 0x25));
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);

        // Drift upward as it fades — float away toward the caret
        var driftUp = new DoubleAnimation(0, -6, HideDuration) { EasingFunction = HideEase };
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, driftUp);
    }

    #endregion

    #region Hover Glow

    private void Border_MouseEnter(object sender, MouseEventArgs e)
    {
        // Only animate Opacity — BlurRadius animation forces a full software re-render
        // every frame (AllowsTransparency=True disables GPU acceleration), which is expensive.
        var glowUp = new DoubleAnimation(0.18, 0.32, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        PanelGlow.BeginAnimation(DropShadowEffect.OpacityProperty, glowUp);
    }

    private void Border_MouseLeave(object sender, MouseEventArgs e)
    {
        var glowDown = new DoubleAnimation(0.32, 0.18, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        PanelGlow.BeginAnimation(DropShadowEffect.OpacityProperty, glowDown);
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
        _isDragged = true;
        SuggestionBorder.ReleaseMouseCapture();
        e.Handled = true;
    }

    #endregion

    #region Positioning

    private void PositionNearCursor()
    {
        if (_isDragged) return;

        var cursor = CursorPositionHelper.GetMousePosition();
        var workArea = SystemParameters.WorkArea;

        double panelWidth = Math.Max(ActualWidth, 200);
        double panelHeight = Math.Max(ActualHeight, 60);

        double cursorX = cursor.X / _dpiScaleX;
        double cursorY = cursor.Y / _dpiScaleY;

        double x = cursorX;
        double y = cursorY + CaretGap;

        if (x + panelWidth > workArea.Right - ScreenEdgeMargin)
            x = workArea.Right - panelWidth - ScreenEdgeMargin;

        if (y + panelHeight > workArea.Bottom - ScreenEdgeMargin)
            y = cursorY - panelHeight - CaretGap;

        x = Math.Max(workArea.Left + ScreenEdgeMargin, x);
        y = Math.Max(workArea.Top + ScreenEdgeMargin, y);

        Left = x;
        Top = y;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsVisible)
            PositionNearCursor();
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        StopLoadingAnimation();

        // Clear all running animations so their Completed handlers don't fire on a dead window
        BeginAnimation(OpacityProperty, null);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);

        if (_isDragging)
            SuggestionBorder.ReleaseMouseCapture();

        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        EnableAcrylic();

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

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    private enum WindowCompositionAttribute { WCA_ACCENT_POLICY = 19 }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor; // ABGR: (alpha << 24) | (blue << 16) | (green << 8) | red
        public uint AnimationId;
    }

    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
    }

    private void EnableAcrylic()
    {
        // Dark tint that blends with the blurred background — ABGR for #101825 at ~75% opacity
        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 0,
            GradientColor = 0xC0251810
        };

        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = size,
                Data = ptr
            };
            SetWindowCompositionAttribute(new WindowInteropHelper(this).Handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
