using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

public partial class SettingsWindow : Window
{
    private AppConfig _config;
    private AcceptanceLearningService _learningService;
    private bool _loading = true;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _learningService = new AcceptanceLearningService();
        LoadValues();
        LoadLearningStats();
        _loading = false;
    }

    private void LoadValues()
    {
        // Engine settings
        EngineCombo.SelectedIndex = _config.PredictionEngine.ToLower() switch
        {
            "gemini" => 0,
            "gpt5" => 1,
            "claude" => 2,
            _ => 3 // Dummy
        };
        UpdateEngineUI();
        
        ApiKeyBox.Text = _config.GeminiApiKey ?? "";
        Gpt5ApiKeyBox.Text = _config.OpenAiApiKey ?? "";
        ClaudeApiKeyBox.Text = _config.AnthropicApiKey ?? "";

        // Set model selections based on config (match by Tag)
        SelectModelByTag(GeminiModelCombo, _config.GeminiModel);
        SelectModelByTag(Gpt5ModelCombo, _config.Gpt5Model);
        SelectModelByTag(ClaudeModelCombo, _config.ClaudeModel);

        UpdateApiKeyStatus();

        // Behavior settings
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

        // Quality settings - feature toggles (stored in config, add properties as needed)
        OcrEnabledCheck.IsChecked = _config.OcrEnabled;
        RollingContextCheck.IsChecked = true; // Currently always on, could be config option
        LearningEnabledCheck.IsChecked = _config.LearningEnabled;

        // Advanced
        PromptBox.Text = _config.EffectiveSystemPrompt;
    }

    private void LoadLearningStats()
    {
        try
        {
            var stats = _learningService.GetStats();
            
            StatsTotalAccepted.Text = stats.TotalAccepted.ToString();
            StatsCategories.Text = stats.ByCategory.Count.ToString();
            
            if (stats.LastEntryTime.HasValue)
            {
                var timeSince = DateTime.UtcNow - stats.LastEntryTime.Value;
                if (timeSince.TotalMinutes < 1)
                    StatsLastActivity.Text = "Just now";
                else if (timeSince.TotalHours < 1)
                    StatsLastActivity.Text = $"{(int)timeSince.TotalMinutes}m ago";
                else if (timeSince.TotalDays < 1)
                    StatsLastActivity.Text = $"{(int)timeSince.TotalHours}h ago";
                else
                    StatsLastActivity.Text = $"{(int)timeSince.TotalDays}d ago";
            }
            else
            {
                StatsLastActivity.Text = "Never";
            }

            // Build category breakdown bars
            CategoryBreakdownPanel.Children.Clear();
            if (stats.TotalAccepted > 0)
            {
                foreach (var kvp in stats.ByCategory.OrderByDescending(x => x.Value))
                {
                    var percent = (double)kvp.Value / stats.TotalAccepted;
                    var bar = CreateCategoryBar(kvp.Key, kvp.Value, percent);
                    CategoryBreakdownPanel.Children.Add(bar);
                }
            }
            else
            {
                CategoryBreakdownPanel.Children.Add(new TextBlock
                {
                    Text = "No data yet. Accept some suggestions to start learning!",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                    FontSize = 12,
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }
        }
        catch { }
    }

    private UIElement CreateCategoryBar(string category, int count, double percent)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        // Icon based on category
        var icon = category.ToLower() switch
        {
            "chat" => "💬",
            "email" => "📧",
            "code" => "💻",
            "document" => "📝",
            "browser" => "🌐",
            "terminal" => "⌨️",
            _ => "📱"
        };

        var nameBlock = new TextBlock
        {
            Text = $"{icon} {category}",
            Foreground = new SolidColorBrush(Color.FromRgb(240, 246, 252)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameBlock, 0);

        var countBlock = new TextBlock
        {
            Text = count.ToString(),
            Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(countBlock, 1);

        var barBg = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(33, 38, 45)),
            CornerRadius = new CornerRadius(3),
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(barBg, 2);

        var barFill = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(47, 129, 247)),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0 // Animated below
        };
        Grid.SetColumn(barFill, 2);

        grid.Children.Add(nameBlock);
        grid.Children.Add(countBlock);
        grid.Children.Add(barBg);
        grid.Children.Add(barFill);

        // Animate the bar fill
        Dispatcher.BeginInvoke(() =>
        {
            var anim = new DoubleAnimation(0, barBg.ActualWidth * percent, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            barFill.BeginAnimation(WidthProperty, anim);
        }, System.Windows.Threading.DispatcherPriority.Render);

        return grid;
    }

    /// <summary>
    /// Select a ComboBox item by matching its Tag property to the config value.
    /// Falls back to the first item if no match is found.
    /// </summary>
    private static void SelectModelByTag(ComboBox combo, string configValue)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), configValue, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private void UpdateApiKeyStatus()
    {
        var hasGeminiKey = !string.IsNullOrWhiteSpace(ApiKeyBox.Text) && ApiKeyBox.Text.Length > 10;
        ApiKeyStatus.Foreground = new SolidColorBrush(hasGeminiKey 
            ? Color.FromRgb(35, 134, 54)  // Success green
            : Color.FromRgb(139, 148, 158)); // Muted gray
        ApiKeyStatus.ToolTip = hasGeminiKey ? "API key is set" : "API key required for Gemini";
        
        var hasGpt5Key = !string.IsNullOrWhiteSpace(Gpt5ApiKeyBox.Text) && Gpt5ApiKeyBox.Text.Length > 10;
        Gpt5ApiKeyStatus.Foreground = new SolidColorBrush(hasGpt5Key 
            ? Color.FromRgb(35, 134, 54)  // Success green
            : Color.FromRgb(139, 148, 158)); // Muted gray
        Gpt5ApiKeyStatus.ToolTip = hasGpt5Key ? "API key is set" : "API key required for GPT-5";
        
        var hasClaudeKey = !string.IsNullOrWhiteSpace(ClaudeApiKeyBox.Text) && ClaudeApiKeyBox.Text.Length > 10;
        ClaudeApiKeyStatus.Foreground = new SolidColorBrush(hasClaudeKey 
            ? Color.FromRgb(35, 134, 54)  // Success green
            : Color.FromRgb(139, 148, 158)); // Muted gray
        ClaudeApiKeyStatus.ToolTip = hasClaudeKey ? "API key is set" : "API key required for Claude";
    }

    private void UpdateEngineUI()
    {
        var selectedEngine = EngineCombo.SelectedIndex;
        // 0 = Gemini, 1 = GPT-5, 2 = Claude, 3 = Dummy
        GeminiApiKeyPanel.Visibility = selectedEngine == 0 ? Visibility.Visible : Visibility.Collapsed;
        Gpt5ApiKeyPanel.Visibility = selectedEngine == 1 ? Visibility.Visible : Visibility.Collapsed;
        ClaudeApiKeyPanel.Visibility = selectedEngine == 2 ? Visibility.Visible : Visibility.Collapsed;
        GeminiModelPanel.Visibility = selectedEngine == 0 ? Visibility.Visible : Visibility.Collapsed;
        Gpt5ModelPanel.Visibility = selectedEngine == 1 ? Visibility.Visible : Visibility.Collapsed;
        ClaudeModelPanel.Visibility = selectedEngine == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowSaveIndicator()
    {
        SaveStatus.Opacity = 1;
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(2))
        {
            BeginTime = TimeSpan.FromMilliseconds(500)
        };
        SaveStatus.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void SaveSettings()
    {
        if (_loading) return;

        _config.PredictionEngine = EngineCombo.SelectedIndex switch
        {
            0 => "gemini",
            1 => "gpt5",
            2 => "claude",
            _ => "dummy"
        };
        _config.GeminiApiKey = ApiKeyBox.Text;
        _config.OpenAiApiKey = Gpt5ApiKeyBox.Text;
        _config.AnthropicApiKey = ClaudeApiKeyBox.Text;

        // Save model selections from Tag values
        _config.GeminiModel = (GeminiModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gemini-3.1-flash-lite-preview";
        _config.Gpt5Model = (Gpt5ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-5.4-nano";
        _config.ClaudeModel = (ClaudeModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "claude-haiku-4-5-20251001";

        _config.CompletionPreset = (LengthCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "extended";
        _config.Temperature = TempSlider.Value;
        _config.MinBufferLength = (int)MinCharsSlider.Value;
        _config.DebounceMs = (int)DebounceSlider.Value;
        _config.FastDebounceMs = (int)FastDebounceSlider.Value;
        _config.OcrEnabled = OcrEnabledCheck.IsChecked == true;
        _config.LearningEnabled = LearningEnabledCheck.IsChecked == true;

        var promptText = PromptBox.Text.Trim();
        _config.CustomSystemPrompt = (promptText == AppConfig.DefaultSystemPrompt) ? null : promptText;

        _config.Save();
        UpdateApiKeyStatus();
        ShowSaveIndicator();
    }

    // Preset buttons
    private void PresetMinimal_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        
        LengthCombo.SelectedIndex = 0; // Brief
        MinCharsSlider.Value = 5;
        DebounceSlider.Value = 500;
        FastDebounceSlider.Value = 200;
        OcrEnabledCheck.IsChecked = false;
        RollingContextCheck.IsChecked = false;
        LearningEnabledCheck.IsChecked = false;
        TempSlider.Value = 0.2;

        _loading = false;
        SaveSettings();
    }

    private void PresetBalanced_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;

        LengthCombo.SelectedIndex = 2; // Extended
        MinCharsSlider.Value = 3;
        DebounceSlider.Value = 300;
        FastDebounceSlider.Value = 100;
        OcrEnabledCheck.IsChecked = true;
        RollingContextCheck.IsChecked = true;
        LearningEnabledCheck.IsChecked = false;
        TempSlider.Value = 0.3;
        
        _loading = false;
        SaveSettings();
    }

    private void PresetMaximum_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;

        LengthCombo.SelectedIndex = 3; // Unlimited
        MinCharsSlider.Value = 2;
        DebounceSlider.Value = 150;
        FastDebounceSlider.Value = 80;
        OcrEnabledCheck.IsChecked = true;
        RollingContextCheck.IsChecked = true;
        LearningEnabledCheck.IsChecked = false;
        TempSlider.Value = 0.4;
        
        _loading = false;
        SaveSettings();
    }

    private void ResetLearning_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will clear all learning data. The app will forget your writing style and start fresh.\n\nContinue?",
            "Reset Learning Data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var trackingPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Keystroke", "tracking.jsonl");
                
                if (File.Exists(trackingPath))
                {
                    File.WriteAllText(trackingPath, "");
                }
                
                LoadLearningStats();
                MessageBox.Show("Learning data has been reset.", "Reset Complete", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to reset: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // Event handlers
    private void Setting_Changed(object sender, RoutedEventArgs e) => SaveSettings();
    private void Setting_Changed(object sender, TextChangedEventArgs e) => SaveSettings();
    private void Setting_Changed(object sender, SelectionChangedEventArgs e) => SaveSettings();

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

    private void Setting_CheckChanged(object sender, RoutedEventArgs e) => SaveSettings();

    private void EngineCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateEngineUI();
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
