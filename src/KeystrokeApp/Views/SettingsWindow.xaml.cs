using System.Windows;
using System.Windows.Controls;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

public partial class SettingsWindow : Window
{
    private AppConfig _config;
    private bool _loading = true;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        LoadValues();
        _loading = false;
    }

    private void LoadValues()
    {
        EngineCombo.SelectedIndex = _config.PredictionEngine.ToLower() == "gemini" ? 0 : 1;
        ApiKeyBox.Text = _config.GeminiApiKey ?? "";
        ModelCombo.Text = _config.GeminiModel;

        LengthCombo.SelectedIndex = _config.CompletionPreset switch
        {
            "brief" => 0,
            "standard" => 1,
            "extended" => 2,
            "unlimited" => 3,
            _ => 2
        };

        TempSlider.Value = _config.Temperature;
        TempLabel.Text = _config.Temperature.ToString("0.0");

        MinCharsSlider.Value = _config.MinBufferLength;
        MinCharsLabel.Text = _config.MinBufferLength.ToString();

        DebounceSlider.Value = _config.DebounceMs;
        DebounceLabel.Text = $"{_config.DebounceMs}ms";

        FastDebounceSlider.Value = _config.FastDebounceMs;
        FastDebounceLabel.Text = $"{_config.FastDebounceMs}ms";

        OcrEnabledCheck.IsChecked = _config.OcrEnabled;

        PromptBox.Text = _config.EffectiveSystemPrompt;
    }

    private void SaveSettings()
    {
        if (_loading) return;

        _config.PredictionEngine = EngineCombo.SelectedIndex == 0 ? "gemini" : "dummy";
        _config.GeminiApiKey = ApiKeyBox.Text;
        _config.GeminiModel = ModelCombo.Text;
        _config.CompletionPreset = (LengthCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "extended";
        _config.Temperature = TempSlider.Value;
        _config.MinBufferLength = (int)MinCharsSlider.Value;
        _config.DebounceMs = (int)DebounceSlider.Value;
        _config.FastDebounceMs = (int)FastDebounceSlider.Value;
        _config.OcrEnabled = OcrEnabledCheck.IsChecked == true;

        var promptText = PromptBox.Text.Trim();
        _config.CustomSystemPrompt = (promptText == AppConfig.DefaultSystemPrompt) ? null : promptText;

        _config.Save();
    }

    // Event handlers referenced by XAML

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void Setting_Changed(object sender, TextChangedEventArgs e)
    {
        SaveSettings();
    }

    private void Setting_Changed(object sender, SelectionChangedEventArgs e)
    {
        SaveSettings();
    }

    private void TempSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TempLabel != null)
            TempLabel.Text = e.NewValue.ToString("0.0");
        SaveSettings();
    }

    private void MinCharsSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinCharsLabel != null)
            MinCharsLabel.Text = ((int)e.NewValue).ToString();
        SaveSettings();
    }

    private void DebounceSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DebounceLabel != null)
            DebounceLabel.Text = $"{(int)e.NewValue}ms";
        SaveSettings();
    }

    private void FastDebounceSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FastDebounceLabel != null)
            FastDebounceLabel.Text = $"{(int)e.NewValue}ms";
        SaveSettings();
    }

    private void Setting_CheckChanged(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void ResetPrompt_Click(object sender, RoutedEventArgs e)
    {
        PromptBox.Text = AppConfig.DefaultSystemPrompt;
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
