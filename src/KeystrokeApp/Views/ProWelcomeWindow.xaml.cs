using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

public partial class ProWelcomeWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBarHelper.Apply(this);
    }

    private int _currentStep;

    public ProWelcomeWindow()
    {
        InitializeComponent();
        _currentStep = 0;
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
        WhatChangedStep.Visibility = _currentStep == 0 ? Visibility.Visible : Visibility.Collapsed;
        HowItLearnsStep.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        AllSetStep.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;

        SetStepIndicator(StepIndicator1, 0);
        SetStepIndicator(StepIndicator2, 1);
        SetStepIndicator(StepIndicator3, 2);

        SubtitleText.Text = _currentStep switch
        {
            0 => "Your license is active. Here's what just changed.",
            1 => "A quick look at how Keystroke builds your writing profile.",
            _ => "Everything is running. Close this window and keep typing."
        };

        BackButton.Visibility = _currentStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _currentStep < 2 ? Visibility.Visible : Visibility.Collapsed;

        NextButton.Content = _currentStep == 2 ? "Start typing" : "Next";
        NextButton.Style = _currentStep == 2
            ? (Style)FindResource("SuccessButton")
            : (Style)FindResource("PrimaryButton");
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep < 2)
        {
            _currentStep++;
            UpdateUi();
            return;
        }

        Close();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 0)
        {
            _currentStep--;
            UpdateUi();
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
