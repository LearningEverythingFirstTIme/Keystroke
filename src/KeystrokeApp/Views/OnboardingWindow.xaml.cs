using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

public partial class OnboardingWindow : Window
{
    public sealed record Outcome(
        bool ExitApplication,
        bool StartPaused,
        bool OpenSettingsRequested,
        bool ConsentAccepted,
        bool OnboardingCompleted,
        string GeminiApiKey);

    private readonly GeminiApiKeyValidationService _validator;
    private readonly AppConfig _config;
    private int _currentStep;
    private bool _isBusy;
    private bool _keyVerified;

    public Outcome Result { get; private set; } = new(
        ExitApplication: false,
        StartPaused: false,
        OpenSettingsRequested: false,
        ConsentAccepted: false,
        OnboardingCompleted: false,
        GeminiApiKey: "");

    public OnboardingWindow(AppConfig config, GeminiApiKeyValidationService validator)
    {
        InitializeComponent();
        _config = config;
        _validator = validator;
        ConsentCheck.IsChecked = config.ConsentAccepted;
        GeminiApiKeyBox.Text = config.GeminiApiKey ?? "";
        _keyVerified = false;
        _currentStep = config.ConsentAccepted ? 2 : 0;
        UpdateUi();
    }

    private void SetStepIndicator(Border border, int stepIndex)
    {
        bool isActive = _currentStep == stepIndex;
        bool isComplete = _currentStep > stepIndex;

        border.Background = new SolidColorBrush(
            isActive
                ? Color.FromRgb(23, 38, 59)
                : isComplete
                    ? Color.FromRgb(20, 32, 51)
                    : Color.FromRgb(16, 25, 42));

        border.BorderBrush = new SolidColorBrush(
            isActive
                ? Color.FromRgb(94, 166, 255)
                : isComplete
                    ? Color.FromRgb(57, 80, 110)
                    : Color.FromRgb(34, 50, 74));
    }

    private void UpdateUi()
    {
        WelcomeStep.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivacyStep.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyStep.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        ReadyStep.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

        SetStepIndicator(StepIndicator1, 0);
        SetStepIndicator(StepIndicator2, 1);
        SetStepIndicator(StepIndicator3, 2);
        SetStepIndicator(StepIndicator4, 3);

        SubtitleText.Text = _currentStep switch
        {
            0 => "A quick setup will get you to live completions fast.",
            1 => "Review privacy and advanced feature defaults before Keystroke starts.",
            2 => "Connect Gemini so Keystroke can start generating completions.",
            _ => "You are one click away from using Keystroke."
        };

        ExitButton.Visibility = _config.ConsentAccepted ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = _currentStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        VerifyButton.IsEnabled = !_isBusy;
        GeminiApiKeyBox.IsEnabled = !_isBusy;

        if (_currentStep == 3)
        {
            var consentAccepted = ConsentCheck.IsChecked == true || _config.ConsentAccepted;
            var canStart = consentAccepted && _keyVerified;
            ReadySummaryText.Text = canStart
                ? "Keystroke is ready to start with Gemini, Gemini 3.1 Flash-Lite Preview, OCR on, rolling context on, and Personalized AI off."
                : "You can still start Keystroke later, but it will stay paused until you add and verify a Gemini API key.";
            ReadyActiveText.Text = canStart
                ? "Gemini completions will start immediately with the recommended default setup."
                : "Keystroke will open in a safe paused state with the recommended Gemini setup saved for later.";
            PausedSummaryCard.Visibility = canStart ? Visibility.Collapsed : Visibility.Visible;
        }

        LaterButton.Visibility = _currentStep == 3 && ConsentCheck.IsChecked == true && !_keyVerified
            ? Visibility.Visible
            : Visibility.Collapsed;

        NextButton.Content = _currentStep == 3
            ? (_keyVerified ? "Start Keystroke" : "Review again")
            : "Next";

        NextButton.IsEnabled = !_isBusy && CanAdvance();
    }

    private bool CanAdvance()
    {
        return _currentStep switch
        {
            0 => true,
            1 => ConsentCheck.IsChecked == true,
            2 => true,
            3 => _keyVerified,
            _ => false
        };
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new Outcome(
            ExitApplication: true,
            StartPaused: false,
            OpenSettingsRequested: false,
            ConsentAccepted: false,
            OnboardingCompleted: false,
            GeminiApiKey: "");
        Close();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 0)
            return;

        _currentStep--;
        UpdateUi();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep < 3)
        {
            _currentStep++;
            UpdateUi();
            return;
        }

        Result = new Outcome(
            ExitApplication: false,
            StartPaused: false,
            OpenSettingsRequested: false,
            ConsentAccepted: true,
            OnboardingCompleted: true,
            GeminiApiKey: GeminiApiKeyBox.Text.Trim());
        Close();
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        Result = new Outcome(
            ExitApplication: false,
            StartPaused: true,
            OpenSettingsRequested: false,
            ConsentAccepted: true,
            OnboardingCompleted: false,
            GeminiApiKey: GeminiApiKeyBox.Text.Trim());
        Close();
    }

    private void OpenSettingsInstead_Click(object sender, RoutedEventArgs e)
    {
        Result = new Outcome(
            ExitApplication: false,
            StartPaused: true,
            OpenSettingsRequested: true,
            ConsentAccepted: ConsentCheck.IsChecked == true || _config.ConsentAccepted,
            OnboardingCompleted: false,
            GeminiApiKey: GeminiApiKeyBox.Text.Trim());
        Close();
    }

    private void OpenAiStudio_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = OnboardingStateService.GeminiApiKeyUrl,
            UseShellExecute = true
        });
    }

    private async void VerifyKey_Click(object sender, RoutedEventArgs e)
    {
        _isBusy = true;
        VerificationStatusText.Text = "Verifying Gemini key...";
        VerificationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225));
        UpdateUi();

        try
        {
            var result = await _validator.ValidateAsync(GeminiApiKeyBox.Text, _config.GeminiModel);
            _keyVerified = result.IsValid;
            VerificationStatusText.Text = result.Message;
            VerificationStatusText.Foreground = new SolidColorBrush(
                result.IsValid
                    ? Color.FromRgb(52, 195, 143)
                    : result.Status is GeminiApiKeyValidationStatus.NetworkError or GeminiApiKeyValidationStatus.Timeout
                        ? Color.FromRgb(243, 167, 79)
                        : Color.FromRgb(244, 114, 182));

            if (result.IsValid)
                _currentStep = 3;
        }
        finally
        {
            _isBusy = false;
            UpdateUi();
        }
    }

    private void ConsentCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateUi();
    }

    private void GeminiApiKeyBox_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _keyVerified = false;
        if (string.IsNullOrWhiteSpace(GeminiApiKeyBox.Text))
        {
            VerificationStatusText.Text = "Paste a key to verify it.";
            VerificationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(140, 160, 187));
        }
        UpdateUi();
    }
}
