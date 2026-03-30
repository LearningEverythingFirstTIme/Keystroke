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

    private static readonly Duration ShowDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration HideDuration = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration WordRevealDuration = new(TimeSpan.FromMilliseconds(40));
    
    private static readonly IEasingFunction ShowEase = new BackEase 
    { 
        EasingMode = EasingMode.EaseOut, 
        Amplitude = 0.3
    };
    private static readonly IEasingFunction HideEase = new QuadraticEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction WordRevealEase = new QuadraticEase { EasingMode = EasingMode.EaseOut };

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
        {
            _isAnimatingHide = false;
            Show();
            AnimateShow();
        }

        PositionNearCaret();
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
        {
            _isAnimatingHide = false;
            Show();
            AnimateShow();
        }
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
        {
            _isAnimatingHide = false;
            Show();
            AnimateShow();
        }

        PositionNearCaret();
    }

    public void SetAlternatives(string prefix, List<string> alternatives)
    {
        if (prefix != _currentPrefix || alternatives.Count == 0)
            return;

        foreach (var alt in alternatives)
        {
            var normalized = alt.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(normalized) && !_suggestions.Contains(normalized))
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

        var flashBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x50, 0x70));
        SuggestionBorder.BorderBrush = flashBrush;

        var borderFlash = new ColorAnimation(
            Color.FromRgb(0x30, 0x50, 0x70),
            Color.FromRgb(0x30, 0xFF, 0xFF),
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

        var flashBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x50, 0x70));
        SuggestionBorder.BorderBrush = flashBrush;

        var borderFlash = new ColorAnimation(
            Color.FromRgb(0x30, 0x50, 0x70),
            Color.FromRgb(0x40, 0xC0, 0x60),
            TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            AutoReverse = true
        };
        flashBrush.BeginAnimation(SolidColorBrush.ColorProperty, borderFlash);

        var confirmScale = new DoubleAnimation(1.0, 1.02, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            AutoReverse = true
        };
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, confirmScale);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, confirmScale);
    }

    public string GetFullSuggestion() => _currentPrefix + _currentSuggestion;
    public bool HasSuggestion => IsVisible && !string.IsNullOrEmpty(_currentSuggestion);

    #endregion

    #region Loading Animation

    private void StartLoadingAnimation()
    {
        LoadingIndicator.Visibility = Visibility.Visible;
        _loadingDotCount = 0;
        LoadingDots.Text = "";
        
        _loadingDotTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _loadingDotTimer.Tick += (s, e) =>
        {
            _loadingDotCount = (_loadingDotCount + 1) % 4;
            LoadingDots.Text = new string('.', _loadingDotCount);
        };
        _loadingDotTimer.Start();
    }

    private void StopLoadingAnimation()
    {
        _loadingDotTimer?.Stop();
        _loadingDotTimer = null;
        LoadingIndicator.Visibility = Visibility.Collapsed;
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
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xF0)),
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

    private void AnimateShow()
    {
        _isAnimatingHide = false;

        var scaleX = new DoubleAnimation(0.95, 1.0, ShowDuration) { EasingFunction = ShowEase };
        var scaleY = new DoubleAnimation(0.95, 1.0, ShowDuration) { EasingFunction = ShowEase };
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);

        var fadeIn = new DoubleAnimation(0, 1, ShowDuration) { EasingFunction = ShowEase };
        BeginAnimation(OpacityProperty, fadeIn);

        var slideUp = new DoubleAnimation(8, 0, ShowDuration) { EasingFunction = ShowEase };
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    private void AnimateHide()
    {
        _isAnimatingHide = true;

        var scaleDownX = new DoubleAnimation(1.0, 0.98, HideDuration) { EasingFunction = HideEase };
        var scaleDownY = new DoubleAnimation(1.0, 0.98, HideDuration) { EasingFunction = HideEase };
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleDownX);
        ScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleDownY);

        var fadeOut = new DoubleAnimation(Opacity, 0, HideDuration) { EasingFunction = HideEase };
        fadeOut.Completed += (_, _) =>
        {
            if (_isAnimatingHide)
            {
                _isAnimatingHide = false;
                _isDragged = false;
                Hide();
                
                ScaleTransform.ScaleX = 0.95;
                ScaleTransform.ScaleY = 0.95;
                SlideTransform.Y = 8;
                
                SuggestionBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            }
        };
        BeginAnimation(OpacityProperty, fadeOut);

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
        _isDragged = true;
        SuggestionBorder.ReleaseMouseCapture();
        e.Handled = true;
    }

    #endregion

    #region Positioning

    private void PositionNearCaret()
    {
        if (_isDragged) return;

        var caret = CursorPositionHelper.GetCaretPosition();
        var workArea = SystemParameters.WorkArea;

        double panelWidth = Math.Max(ActualWidth, 200);
        double panelHeight = Math.Max(ActualHeight, 60);

        double caretX = caret.X / _dpiScaleX;
        double caretY = caret.Y / _dpiScaleY;
        double caretH = caret.Height / _dpiScaleY;

        double x = caretX;
        double y = caretY + CaretGap;

        if (x + panelWidth > workArea.Right - ScreenEdgeMargin)
            x = workArea.Right - panelWidth - ScreenEdgeMargin;

        if (y + panelHeight > workArea.Bottom - ScreenEdgeMargin)
            y = caretY - caretH - panelHeight - CaretGap;

        x = Math.Max(workArea.Left + ScreenEdgeMargin, x);
        y = Math.Max(workArea.Top + ScreenEdgeMargin, y);

        Left = x;
        Top = y;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (IsVisible)
            PositionNearCaret();
    }

    #endregion

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

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
