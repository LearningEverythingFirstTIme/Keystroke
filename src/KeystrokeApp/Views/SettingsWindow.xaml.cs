using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KeystrokeApp.Services;

namespace KeystrokeApp.Views;

public partial class SettingsWindow : Window
{
    private AppConfig _config;
    private AcceptanceLearningService?  _learningService;
    private StyleProfileService?        _styleProfileService;
    private VocabularyProfileService?   _vocabularyProfileService;
    private LearningScoreService?       _learningScoreService;
    private bool _loading = true;
    private DispatcherTimer? _saveDebounceTimer;
    private CancellationTokenSource? _modelFetchCts;

    /// <summary>Fired immediately when the user picks a theme swatch.</summary>
    public event Action<string>? ThemeChanged;

    public SettingsWindow(
        AppConfig config,
        StyleProfileService?      styleProfileService      = null,
        VocabularyProfileService? vocabularyProfileService = null,
        LearningScoreService?     learningScoreService     = null)
    {
        InitializeComponent();
        _config                   = config;
        _styleProfileService      = styleProfileService;
        _vocabularyProfileService = vocabularyProfileService;
        _learningScoreService     = learningScoreService;
        _learningService          = new AcceptanceLearningService();
        LoadValues();
        LoadLearningStats();
        LoadVocabularyProfileStatus();
        UpdateSwatchSelection(_config.ThemeId);
        _loading = false;
        Closed += OnWindowClosed;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _saveDebounceTimer?.Stop();
        _saveDebounceTimer = null;
        _modelFetchCts?.Cancel();
        _modelFetchCts?.Dispose();
        _modelFetchCts = null;
        _learningService = null;
    }

    private void LoadValues()
    {
        // Engine settings
        // Populate all model/endpoint fields BEFORE calling UpdateEngineUI(),
        // because UpdateEngineUI() triggers CheckOllamaStatusAsync() which reads
        // OllamaModelCombo and OllamaEndpointBox — they must be set first.
        GeminiApiKeyBox.Text = _config.GeminiApiKey ?? "";
        Gpt5ApiKeyBox.Text = _config.OpenAiApiKey ?? "";
        ClaudeApiKeyBox.Text = _config.AnthropicApiKey ?? "";
        OpenRouterApiKeyBox.Text = _config.OpenRouterApiKey ?? "";

        SelectModelByTag(GeminiModelCombo, _config.GeminiModel);
        SelectModelByTag(Gpt5ModelCombo, _config.Gpt5Model);
        SelectModelByTag(ClaudeModelCombo, _config.ClaudeModel);
        SelectModelByTag(OllamaModelCombo, _config.OllamaModel);
        OllamaEndpointBox.Text = _config.OllamaEndpoint;

        EngineCombo.SelectedIndex = _config.PredictionEngine.ToLower() switch
        {
            "gemini"      => 0,
            "gpt5"        => 1,
            "claude"      => 2,
            "ollama"      => 3,
            "openrouter"  => 4,
            _             => 5 // Dummy
        };
        UpdateEngineUI();

        UpdateGeminiApiKeyStatus();

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

        // Quality settings - suggestions count
        SuggestionsSlider.Value = _config.MaxSuggestions;
        SuggestionsLabel.Text = _config.MaxSuggestions.ToString();

        // Quality settings - feature toggles (stored in config, add properties as needed)
        OcrEnabledCheck.IsChecked = _config.OcrEnabled;
        RollingContextCheck.IsChecked = _config.RollingContextEnabled;
        LearningEnabledCheck.IsChecked = _config.LearningEnabled;

        StyleProfileCheck.IsChecked = _config.StyleProfileEnabled;
        StyleProfileIntervalSlider.Value = _config.StyleProfileInterval;
        StyleProfileIntervalLabel.Text = _config.StyleProfileInterval.ToString();
        LoadStyleProfileStatus();
        LoadStyleProfileProgress();

        // Advanced
        PromptBox.Text = _config.EffectiveSystemPrompt;
    }

    private void LoadLearningStats()
    {
        try
        {
            var stats = _learningService?.GetStats();
            if (stats == null) return;

            StatsTotalAccepted.Text = stats.TotalAccepted.ToString();

            // Overall acceptance rate: accepted / (accepted + dismissed) as a percentage.
            int totalShown = stats.TotalAccepted + stats.TotalDismissed;
            if (totalShown > 0)
            {
                var rate = (int)Math.Round((double)stats.TotalAccepted / totalShown * 100);
                StatsAcceptRate.Text = $"{rate}%";

                // Colour-code: green ≥ 50%, amber 25–49%, muted < 25%
                StatsAcceptRate.Foreground = new SolidColorBrush(rate >= 50
                    ? Color.FromRgb(63, 185, 80)   // green
                    : rate >= 25
                        ? Color.FromRgb(240, 136, 62)  // amber
                        : Color.FromRgb(139, 148, 158)); // muted
            }
            else
            {
                StatsAcceptRate.Text = "—";
                StatsAcceptRate.Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158));
            }

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

            // Sub-Phase D: build intelligence cards — one per category with a 0-100
            // score, trend indicator, animated score bar, and stat pills.
            // Recompute scores now so the panel always reflects the latest data.
            var scores = _learningScoreService?.Recompute() ?? new LearningScores();

            CategoryBreakdownPanel.Children.Clear();
            if (stats.TotalAccepted > 0)
            {
                var allCategories = stats.ByCategory.Keys
                    .Union(stats.DismissedByCategory.Keys)
                    .OrderByDescending(cat =>
                        scores.Categories.TryGetValue(cat, out var ci) ? ci.Score : 0);

                foreach (var cat in allCategories)
                {
                    int   accepted   = stats.ByCategory.GetValueOrDefault(cat, 0);
                    int   dismissed  = stats.DismissedByCategory.GetValueOrDefault(cat, 0);
                    float avgQuality = stats.AvgQualityByCategory.GetValueOrDefault(cat, 0f);

                    scores.Categories.TryGetValue(cat, out var intel);
                    int    score = intel?.Score          ?? 0;
                    string trend = intel?.Trend          ?? "Stable";
                    int    delta = intel?.DeltaSinceLast ?? 0;

                    var card = CreateIntelligenceCard(cat, score, trend, delta,
                                                     accepted, dismissed, avgQuality);
                    CategoryBreakdownPanel.Children.Add(card);
                }
            }
            else
            {
                CategoryBreakdownPanel.Children.Add(new TextBlock
                {
                    Text       = "No data yet. Accept some suggestions to start building intelligence!",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                    FontSize   = 12,
                    FontStyle  = FontStyles.Italic,
                    Margin     = new Thickness(0, 8, 0, 0)
                });
            }
        }
        catch (Exception) { /* Learning stats display is non-critical — failure is safe to ignore */ }
    }

    private void LoadStyleProfileStatus()
    {
        try
        {
            var profilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Keystroke", "style-profile.json");

            if (!File.Exists(profilePath))
            {
                StyleProfileStatus.Text = "No profile generated yet. Accept suggestions to build one.";
                return;
            }

            var json = File.ReadAllText(profilePath);
            var data = System.Text.Json.JsonSerializer.Deserialize<StyleProfileData>(json);
            if (data == null)
            {
                StyleProfileStatus.Text = "Profile data is empty.";
                return;
            }

            var age = DateTime.UtcNow - data.LastUpdated;
            string ageText = age.TotalDays < 1 ? "today" : age.TotalDays < 2 ? "yesterday" : $"{(int)age.TotalDays}d ago";
            StyleProfileStatus.Text = $"{data.CategoryProfiles.Count} categories, general profile {data.GeneralProfile.Length} chars. Updated {ageText}.";
        }
        catch (Exception)
        {
            StyleProfileStatus.Text = "";
        }
    }

    private void LoadStyleProfileProgress()
    {
        try
        {
            var showProgress = StyleProfileCheck.IsChecked == true;
            StyleProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;

            if (!showProgress || _styleProfileService == null) return;

            var (current, _) = _styleProfileService.GetProgress();
            var target = (int)StyleProfileIntervalSlider.Value;
            var profiled = _styleProfileService.GetProfiledCategories();

            ProgressCount.Text = $"{current}/{target}";
            var pct = target > 0 ? (double)current / target : 0;

            var circumference = 2 * Math.PI * 37;
            var totalUnits = circumference / 6;
            var filledUnits = totalUnits * pct;
            ProgressArc.StrokeDashArray = new DoubleCollection { filledUnits, totalUnits - filledUnits };

            if (pct >= 1.0)
            {
                ProgressMessage.Text = "profile update in progress...";
                ProgressLabel.Text = "generating";
            }
            else
            {
                var remaining = target - current;
                ProgressMessage.Text = $"{remaining} more acceptance{(remaining != 1 ? "s" : "")} until next update";
                ProgressLabel.Text = "acceptances";
            }

            var profile = _styleProfileService.GetProfile();
            if (profile != null && !string.IsNullOrEmpty(profile.GeneralProfile))
            {
                var age = DateTime.UtcNow - profile.LastUpdated;
                string ageText = age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
                    : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
                    : $"{(int)age.TotalDays}d ago";
                ProgressGenerated.Text = $"Last updated {ageText}";
            }
            else
            {
                ProgressGenerated.Text = "";
            }

            LoadCategoryBadges(profiled);
        }
        catch (Exception) { }
    }

    private void LoadVocabularyProfileStatus()
    {
        try
        {
            var profile = _vocabularyProfileService?.GetProfile();

            if (profile == null || profile.Categories.Count == 0)
            {
                VocabProfileStatus.Text = $"No fingerprint yet — needs {15} accepted completions per category.";
                VocabCategoryBadgesPanel.Children.Clear();
                return;
            }

            var age = DateTime.UtcNow - profile.LastUpdated;
            string ageText = age.TotalHours < 1
                ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
                : $"{(int)age.TotalDays}d ago";

            VocabProfileStatus.Text =
                $"{profile.Categories.Count} categor{(profile.Categories.Count == 1 ? "y" : "ies")} fingerprinted " +
                $"from {profile.EntriesProcessed} completions · Updated {ageText}";

            // Category badges
            VocabCategoryBadgesPanel.Children.Clear();
            var allCategories = new (string name, string icon)[]
            {
                ("Chat", "💬"), ("Email", "📧"), ("Code", "💻"),
                ("Document", "📝"), ("Browser", "🌐"), ("Terminal", "⌨️"), ("Unknown", "📱"),
            };

            foreach (var (name, icon) in allCategories)
            {
                bool hasFingerprint = profile.Categories.ContainsKey(name);
                var border = new Border
                {
                    Background   = new SolidColorBrush(hasFingerprint
                        ? Color.FromArgb(40, 47, 129, 247)   // blue tint — vocabulary/data
                        : Color.FromArgb(40, 48, 54, 88)),
                    CornerRadius = new CornerRadius(12),
                    Padding      = new Thickness(10, 4, 10, 4),
                    Margin       = new Thickness(0, 0, 6, 4)
                };

                string suffix = "";
                if (hasFingerprint && profile.Categories.TryGetValue(name, out var vocab))
                    suffix = $" · {vocab.TopWords.Count}w";

                var text = new TextBlock
                {
                    FontSize   = 11,
                    Foreground = new SolidColorBrush(hasFingerprint
                        ? Color.FromRgb(79, 172, 254)    // blue text
                        : Color.FromRgb(139, 148, 158)),
                    Text = $"{icon} {name}{suffix}"
                };

                border.Child = text;
                VocabCategoryBadgesPanel.Children.Add(border);
            }
        }
        catch (Exception) { /* non-critical */ }
    }

    private void LoadCategoryBadges(Dictionary<string, bool> profiledCategories)
    {
        CategoryBadgesPanel.Children.Clear();

        var allCategories = new (string name, string icon)[]
        {
            ("Chat", "💬"),
            ("Email", "📧"),
            ("Code", "💻"),
            ("Document", "📝"),
            ("Browser", "🌐"),
            ("Terminal", "⌨️"),
            ("Unknown", "📱"),
        };

        foreach (var (name, icon) in allCategories)
        {
            bool isProfiled = profiledCategories.ContainsKey(name);

            var border = new Border
            {
                Background = new SolidColorBrush(isProfiled
                    ? Color.FromArgb(40, 35, 134, 54)
                    : Color.FromArgb(40, 48, 54, 88)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 4)
            };

            var text = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(isProfiled
                    ? Color.FromRgb(63, 185, 80)
                    : Color.FromRgb(139, 148, 158)),
            };
            var run1 = new System.Windows.Documents.Run($"{icon} {name}");
            var run2 = new System.Windows.Documents.Run(isProfiled ? " ✓" : "")
            {
                FontSize = 10
            };
            text.Inlines.Add(run1);
            text.Inlines.Add(run2);

            border.Child = text;
            CategoryBadgesPanel.Children.Add(border);
        }
    }

    /// <summary>
    /// Builds a category row showing: icon+name | accepted count | animated fill bar | accept-rate pill | quality pill.
    /// <paramref name="fillPercent"/> is the share of total accepted entries (drives bar width).
    /// <paramref name="acceptPercent"/> is accepted/(accepted+dismissed) for this category (drives the accept-rate pill).
    /// <paramref name="avgQuality"/> is the mean quality score (0–1) for this category's accepted entries.
    /// </summary>
    private UIElement CreateCategoryBar(
        string category, int accepted, int dismissed,
        double fillPercent, double acceptPercent, float avgQuality = 0f)
    {
        // Outer stack so we can place label row above bar row if needed.
        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        // ── Row 1: name | count | accept-rate pill ────────────────────────────
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = category.ToLower() switch
        {
            "chat"     => "💬",
            "email"    => "📧",
            "code"     => "💻",
            "document" => "📝",
            "browser"  => "🌐",
            "terminal" => "⌨️",
            _          => "📱"
        };

        var nameBlock = new TextBlock
        {
            Text              = $"{icon} {category}",
            Foreground        = new SolidColorBrush(Color.FromRgb(240, 246, 252)),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameBlock, 0);

        int total = accepted + dismissed;
        var countBlock = new TextBlock
        {
            Text              = $"{accepted}{(dismissed > 0 ? $" / {total}" : "")}",
            Foreground        = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
            FontSize          = 11,
            Margin            = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = dismissed > 0
                ? $"{accepted} accepted, {dismissed} dismissed"
                : $"{accepted} accepted"
        };
        Grid.SetColumn(countBlock, 1);

        // Accept-rate pill — only shown if there is dismissal data to compare against.
        if (dismissed > 0)
        {
            int rateInt = (int)Math.Round(acceptPercent * 100);

            // Colour: green ≥ 50%, amber 25–49%, muted < 25%
            var pillColor = rateInt >= 50
                ? Color.FromArgb(40, 63, 185, 80)
                : rateInt >= 25
                    ? Color.FromArgb(40, 240, 136, 62)
                    : Color.FromArgb(40, 139, 148, 158);
            var textColor = rateInt >= 50
                ? Color.FromRgb(63, 185, 80)
                : rateInt >= 25
                    ? Color.FromRgb(240, 136, 62)
                    : Color.FromRgb(139, 148, 158);

            var pill = new Border
            {
                Background    = new SolidColorBrush(pillColor),
                CornerRadius  = new CornerRadius(10),
                Padding       = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip       = $"{rateInt}% of suggestions accepted in {category}"
            };
            pill.Child = new TextBlock
            {
                Text       = $"{rateInt}%",
                Foreground = new SolidColorBrush(textColor),
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(pill, 3);
            headerGrid.Children.Add(pill);
        }

        // Quality pill — shown when avgQuality > 0 (i.e. any enriched entries exist).
        // Uses a star icon so it's visually distinct from the acceptance-rate pill.
        if (avgQuality > 0f)
        {
            int qPct = (int)Math.Round(avgQuality * 100);

            // Colour mirrors the accept-rate scale: green = confident data, amber = mixed, muted = poor
            var qPillColor = qPct >= 70
                ? Color.FromArgb(40, 63, 185, 80)
                : qPct >= 45
                    ? Color.FromArgb(40, 240, 136, 62)
                    : Color.FromArgb(40, 139, 148, 158);
            var qTextColor = qPct >= 70
                ? Color.FromRgb(63, 185, 80)
                : qPct >= 45
                    ? Color.FromRgb(240, 136, 62)
                    : Color.FromRgb(139, 148, 158);

            var qPill = new Border
            {
                Background        = new SolidColorBrush(qPillColor),
                CornerRadius      = new CornerRadius(10),
                Padding           = new Thickness(7, 2, 7, 2),
                Margin            = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip           = $"Average match quality: {qPct}% (based on accept speed, cycling, and post-edit corrections)"
            };
            qPill.Child = new TextBlock
            {
                Text       = $"★ {qPct}%",
                Foreground = new SolidColorBrush(qTextColor),
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold
            };

            // Add a 5th column to the header grid for the quality pill
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(qPill, 4);
            headerGrid.Children.Add(qPill);
        }

        headerGrid.Children.Add(nameBlock);
        headerGrid.Children.Add(countBlock);
        outer.Children.Add(headerGrid);

        // ── Row 2: animated fill bar ──────────────────────────────────────────
        var barGrid = new Grid();
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) }); // spacer to align with name
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var barBg = new Border
        {
            Background        = new SolidColorBrush(Color.FromRgb(33, 38, 45)),
            CornerRadius      = new CornerRadius(3),
            Height            = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(barBg, 1);

        var barFill = new Border
        {
            Background          = new SolidColorBrush(Color.FromRgb(47, 129, 247)),
            CornerRadius        = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Height              = 6,
            Width               = 0 // animated below
        };
        Grid.SetColumn(barFill, 1);

        barGrid.Children.Add(barBg);
        barGrid.Children.Add(barFill);
        outer.Children.Add(barGrid);

        // Animate bar width after layout so we have a real ActualWidth to work with.
        barBg.SizeChanged += (_, _) =>
        {
            if (barBg.ActualWidth <= 0) return;
            var anim = new DoubleAnimation(0, barBg.ActualWidth * fillPercent, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            barFill.BeginAnimation(WidthProperty, anim);
        };

        return outer;
    }

    // ── Sub-Phase D: Intelligence Card ───────────────────────────────────────

    /// <summary>
    /// Builds a rich intelligence card for one app category showing:
    ///   • Colour-coded 0–100 score with trend arrow + delta
    ///   • Animated score bar (width proportional to score / 100)
    ///   • Pill row: accepted count | accept-rate % | avg quality ★
    /// Replaces the simpler CreateCategoryBar() from earlier phases.
    /// </summary>
    private UIElement CreateIntelligenceCard(
        string category, int score, string trend, int delta,
        int accepted, int dismissed, float avgQuality)
    {
        // ── Score colour (matches the bar fill colour below) ──────────────────
        var scoreColor = score >= 80
            ? Color.FromRgb(63, 185, 80)    // green  — excellent
            : score >= 60
                ? Color.FromRgb(47, 129, 247)   // blue   — good
                : score >= 40
                    ? Color.FromRgb(240, 136, 62)   // amber  — building
                    : Color.FromRgb(139, 148, 158);  // muted  — early stage

        var icon = category.ToLower() switch
        {
            "chat"     => "💬",
            "email"    => "📧",
            "code"     => "💻",
            "document" => "📝",
            "browser"  => "🌐",
            "terminal" => "⌨️",
            _          => "📱"
        };

        // ── Trend text & colour ───────────────────────────────────────────────
        string trendArrow = trend == "Improving" ? "↑" : trend == "Drifting" ? "↓" : "→";
        string deltaStr   = delta == 0 ? "" : delta > 0 ? $"+{delta}" : $"{delta}";
        string trendLabel = $"{trendArrow} {trend}{(deltaStr != "" ? $" ({deltaStr})" : "")}";

        var trendColor = trend == "Improving"
            ? Color.FromRgb(63, 185, 80)
            : trend == "Drifting"
                ? Color.FromRgb(240, 136, 62)
                : Color.FromRgb(139, 148, 158);

        // ── Outer card ────────────────────────────────────────────────────────
        var card = new Border
        {
            Background    = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            CornerRadius  = new CornerRadius(8),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(48, 54, 61)),
            BorderThickness = new Thickness(1),
            Padding       = new Thickness(12),
            Margin        = new Thickness(0, 0, 0, 8)
        };

        var outerGrid = new Grid();
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── Col 0: Score box ──────────────────────────────────────────────────
        var scoreBox = new Border
        {
            Background    = new SolidColorBrush(Color.FromArgb(30,
                scoreColor.R, scoreColor.G, scoreColor.B)),
            CornerRadius  = new CornerRadius(8),
            BorderBrush   = new SolidColorBrush(Color.FromArgb(80,
                scoreColor.R, scoreColor.G, scoreColor.B)),
            BorderThickness = new Thickness(1),
            Margin        = new Thickness(0, 0, 12, 0),
            Padding       = new Thickness(4, 8, 4, 8)
        };

        var scorePanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        scorePanel.Children.Add(new TextBlock
        {
            Text              = score.ToString(),
            FontSize          = 26,
            FontWeight        = FontWeights.Bold,
            Foreground        = new SolidColorBrush(scoreColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip           = $"Intelligence score: {score}/100"
        });
        scorePanel.Children.Add(new TextBlock
        {
            Text              = trendLabel,
            FontSize          = 10,
            Foreground        = new SolidColorBrush(trendColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping      = TextWrapping.Wrap,
            TextAlignment     = TextAlignment.Center,
            Margin            = new Thickness(0, 2, 0, 0),
            ToolTip           = trend == "Improving"
                ? "Score is rising — the learning system is gaining confidence in this category"
                : trend == "Drifting"
                    ? "Score is falling — your writing patterns may have shifted; keep accepting suggestions to recalibrate"
                    : "Score is stable"
        });
        scoreBox.Child = scorePanel;
        Grid.SetColumn(scoreBox, 0);

        // ── Col 1: Name + bar + pills ─────────────────────────────────────────
        var contentPanel = new StackPanel();

        // Row 0: category name
        contentPanel.Children.Add(new TextBlock
        {
            Text       = $"{icon} {category}",
            Foreground = new SolidColorBrush(Color.FromRgb(240, 246, 252)),
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 6)
        });

        // Row 1: animated score bar (0–100 scale, not relative to other categories)
        var barBg = new Border
        {
            Background   = new SolidColorBrush(Color.FromRgb(33, 38, 45)),
            CornerRadius = new CornerRadius(3),
            Height       = 6,
            Margin       = new Thickness(0, 0, 0, 8)
        };

        var barFill = new Border
        {
            Background          = new SolidColorBrush(scoreColor),
            CornerRadius        = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Height              = 6,
            Width               = 0 // animated below
        };

        // Use a Grid to overlay fill on top of background
        var barHost = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        barHost.Children.Add(barBg);
        barHost.Children.Add(barFill);
        contentPanel.Children.Add(barHost);

        barBg.SizeChanged += (_, _) =>
        {
            if (barBg.ActualWidth <= 0) return;
            double targetWidth = barBg.ActualWidth * (score / 100.0);
            var anim = new System.Windows.Media.Animation.DoubleAnimation(
                0, targetWidth, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            barFill.BeginAnimation(WidthProperty, anim);
        };

        // Row 2: pills
        var pillRow = new WrapPanel { Orientation = Orientation.Horizontal };

        // Accepted count pill
        AddPill(pillRow,
            $"{accepted} accepted",
            Color.FromArgb(40, 47, 129, 247),
            Color.FromRgb(79, 172, 254),
            $"{accepted} completions accepted in {category}");

        // Accept-rate pill (only when there is dismissal data)
        if (dismissed > 0)
        {
            int rate = (int)Math.Round((double)accepted / (accepted + dismissed) * 100);
            var rateColor = rate >= 50
                ? Color.FromRgb(63, 185, 80)
                : rate >= 25 ? Color.FromRgb(240, 136, 62) : Color.FromRgb(139, 148, 158);
            var rateBg = Color.FromArgb(40, rateColor.R, rateColor.G, rateColor.B);
            AddPill(pillRow, $"{rate}% rate", rateBg, rateColor,
                $"{rate}% of {category} suggestions accepted vs dismissed");
        }

        // Quality pill (when quality data exists)
        if (avgQuality > 0f)
        {
            int qPct = (int)Math.Round(avgQuality * 100);
            var qColor = qPct >= 70
                ? Color.FromRgb(63, 185, 80)
                : qPct >= 45 ? Color.FromRgb(240, 136, 62) : Color.FromRgb(139, 148, 158);
            AddPill(pillRow, $"★ {qPct}% quality",
                Color.FromArgb(40, qColor.R, qColor.G, qColor.B), qColor,
                $"Average match quality: {qPct}% (based on accept speed, cycling, and post-edit corrections)");
        }

        contentPanel.Children.Add(pillRow);
        Grid.SetColumn(contentPanel, 1);

        outerGrid.Children.Add(scoreBox);
        outerGrid.Children.Add(contentPanel);
        card.Child = outerGrid;
        return card;
    }

    /// <summary>Adds a styled pill (badge) to a container panel.</summary>
    private static void AddPill(Panel parent, string text,
        Color bgColor, Color fgColor, string? tooltip = null)
    {
        var pill = new Border
        {
            Background    = new SolidColorBrush(bgColor),
            CornerRadius  = new CornerRadius(10),
            Padding       = new Thickness(8, 2, 8, 2),
            Margin        = new Thickness(0, 0, 6, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (tooltip != null) pill.ToolTip = tooltip;
        pill.Child = new TextBlock
        {
            Text       = text,
            Foreground = new SolidColorBrush(fgColor),
            FontSize   = 11,
            FontWeight = FontWeights.SemiBold
        };
        parent.Children.Add(pill);
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

    private void UpdateGeminiApiKeyStatus()
    {
        var hasGeminiKey = !string.IsNullOrWhiteSpace(GeminiApiKeyBox.Text) && GeminiApiKeyBox.Text.Length > 10;
        GeminiApiKeyStatus.Foreground = new SolidColorBrush(hasGeminiKey 
            ? Color.FromRgb(35, 134, 54)  // Success green
            : Color.FromRgb(139, 148, 158)); // Muted gray
        GeminiApiKeyStatus.ToolTip = hasGeminiKey ? "API key is set" : "API key required for Gemini";
        
        var hasGpt5Key = !string.IsNullOrWhiteSpace(Gpt5ApiKeyBox.Text) && Gpt5ApiKeyBox.Text.Length > 10;
        Gpt5ApiKeyStatus.Foreground = new SolidColorBrush(hasGpt5Key 
            ? Color.FromRgb(35, 134, 54)  // Success green
            : Color.FromRgb(139, 148, 158)); // Muted gray
        Gpt5ApiKeyStatus.ToolTip = hasGpt5Key ? "API key is set" : "API key required for GPT-5";
        
        var hasClaudeKey = !string.IsNullOrWhiteSpace(ClaudeApiKeyBox.Text) && ClaudeApiKeyBox.Text.Length > 10;
        ClaudeApiKeyStatus.Foreground = new SolidColorBrush(hasClaudeKey
            ? Color.FromRgb(35, 134, 54)
            : Color.FromRgb(139, 148, 158));
        ClaudeApiKeyStatus.ToolTip = hasClaudeKey ? "API key is set" : "API key required for Claude";

        var hasOrKey = !string.IsNullOrWhiteSpace(OpenRouterApiKeyBox.Text) && OpenRouterApiKeyBox.Text.Length > 10;
        OpenRouterApiKeyStatus.Foreground = new SolidColorBrush(hasOrKey
            ? Color.FromRgb(35, 134, 54)
            : Color.FromRgb(139, 148, 158));
        OpenRouterApiKeyStatus.ToolTip = hasOrKey ? "API key is set" : "API key required for OpenRouter";
    }

    private void UpdateEngineUI()
    {
        var e = EngineCombo.SelectedIndex;
        // 0=Gemini  1=GPT-5  2=Claude  3=Ollama  4=OpenRouter  5=Dummy
        GeminiApiKeyPanel.Visibility  = e == 0 ? Visibility.Visible : Visibility.Collapsed;
        Gpt5ApiKeyPanel.Visibility    = e == 1 ? Visibility.Visible : Visibility.Collapsed;
        ClaudeApiKeyPanel.Visibility  = e == 2 ? Visibility.Visible : Visibility.Collapsed;
        GeminiModelPanel.Visibility   = e == 0 ? Visibility.Visible : Visibility.Collapsed;
        Gpt5ModelPanel.Visibility     = e == 1 ? Visibility.Visible : Visibility.Collapsed;
        ClaudeModelPanel.Visibility   = e == 2 ? Visibility.Visible : Visibility.Collapsed;
        OllamaPanel.Visibility        = e == 3 ? Visibility.Visible : Visibility.Collapsed;
        OpenRouterPanel.Visibility    = e == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (e == 3) _ = CheckOllamaStatusAsync();
        if (e == 4) _ = LoadOpenRouterModelsAsync();
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
            3 => "ollama",
            4 => "openrouter",
            _ => "dummy"
        };
        _config.GeminiApiKey     = GeminiApiKeyBox.Text;
        _config.OpenAiApiKey     = Gpt5ApiKeyBox.Text;
        _config.AnthropicApiKey  = ClaudeApiKeyBox.Text;
        _config.OpenRouterApiKey = OpenRouterApiKeyBox.Text;

        // Save model selections from Tag values
        _config.GeminiModel = (GeminiModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gemini-3.1-flash-lite-preview";
        _config.Gpt5Model = (Gpt5ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "gpt-5.4-nano";
        _config.ClaudeModel = (ClaudeModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "claude-haiku-4-5-20251001";
        _config.OllamaModel = (OllamaModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "qwen3:30b-a3b";
        _config.OllamaEndpoint = OllamaEndpointBox.Text.Trim();

        // Only overwrite the saved OpenRouter model when the combo has a real selection;
        // skip when it's empty (e.g. mid-population) to avoid clobbering the saved value.
        if (OpenRouterModelCombo.SelectedItem is ComboBoxItem orItem && orItem.Tag is string orModelId)
            _config.OpenRouterModel = orModelId;

        _config.CompletionPreset = (LengthCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "extended";
        _config.Temperature = TempSlider.Value;
        _config.MinBufferLength = (int)MinCharsSlider.Value;
        _config.DebounceMs = (int)DebounceSlider.Value;
        _config.FastDebounceMs = (int)FastDebounceSlider.Value;
        _config.OcrEnabled = OcrEnabledCheck.IsChecked == true;
        _config.RollingContextEnabled = RollingContextCheck.IsChecked == true;
        _config.LearningEnabled = LearningEnabledCheck.IsChecked == true;
        _config.StyleProfileEnabled = StyleProfileCheck.IsChecked == true;
        _config.StyleProfileInterval = (int)StyleProfileIntervalSlider.Value;
        _config.MaxSuggestions = (int)SuggestionsSlider.Value;

        var promptText = PromptBox.Text.Trim();
        _config.CustomSystemPrompt = (promptText == AppConfig.DefaultSystemPrompt) ? null : promptText;

        _config.Save();
        UpdateGeminiApiKeyStatus();
        ShowSaveIndicator();
    }

    // Preset buttons
    private void PresetMinimal_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            LengthCombo.SelectedIndex = 0; // Brief
            MinCharsSlider.Value = 5;
            DebounceSlider.Value = 500;
            FastDebounceSlider.Value = 200;
            OcrEnabledCheck.IsChecked = false;
            RollingContextCheck.IsChecked = false;
            LearningEnabledCheck.IsChecked = false;
            StyleProfileCheck.IsChecked = true;
            StyleProfileIntervalSlider.Value = 30;
            StyleProfileIntervalLabel.Text = "30";
            TempSlider.Value = 0.2;
            SuggestionsSlider.Value = 1;
            SuggestionsLabel.Text = "1";
        }
        finally { _loading = false; }
        SaveSettings();
    }

    private void PresetBalanced_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            LengthCombo.SelectedIndex = 2; // Extended
            MinCharsSlider.Value = 3;
            DebounceSlider.Value = 300;
            FastDebounceSlider.Value = 100;
            OcrEnabledCheck.IsChecked = true;
            RollingContextCheck.IsChecked = true;
            LearningEnabledCheck.IsChecked = false;
            StyleProfileCheck.IsChecked = false;
            TempSlider.Value = 0.3;
            SuggestionsSlider.Value = 3;
            SuggestionsLabel.Text = "3";
        }
        finally { _loading = false; }
        SaveSettings();
    }

    private void PresetMaximum_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            LengthCombo.SelectedIndex = 3; // Unlimited
            MinCharsSlider.Value = 2;
            DebounceSlider.Value = 150;
            FastDebounceSlider.Value = 80;
            OcrEnabledCheck.IsChecked = true;
            RollingContextCheck.IsChecked = true;
            LearningEnabledCheck.IsChecked = false;
            StyleProfileCheck.IsChecked = false;
            TempSlider.Value = 0.4;
            SuggestionsSlider.Value = 5;
            SuggestionsLabel.Text = "5";
        }
        finally { _loading = false; }
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
                var dataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Keystroke", "completions.jsonl");
                var styleProfilePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Keystroke", "style-profile.json");

                if (File.Exists(dataPath))
                    File.WriteAllText(dataPath, "");
                if (File.Exists(styleProfilePath))
                    File.Delete(styleProfilePath);

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

    /// <summary>
    /// Schedule a debounced save for text inputs to avoid writing on every keystroke.
    /// Reuses a single timer — stopping and restarting it resets the interval without
    /// allocating a new timer and closure on every keystroke.
    /// </summary>
    private void DebouncedSave()
    {
        if (_loading) return;

        if (_saveDebounceTimer == null)
        {
            _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveDebounceTimer.Tick += (_, _) =>
            {
                _saveDebounceTimer?.Stop();
                SaveSettings();
            };
        }

        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    // Event handlers
    private void Setting_Changed(object sender, RoutedEventArgs e) => SaveSettings();
    private void Setting_Changed(object sender, TextChangedEventArgs e) => DebouncedSave();
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
    private void SuggestionsSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SuggestionsLabel != null)
            SuggestionsLabel.Text = ((int)e.NewValue).ToString();
        SaveSettings();
    }

    private void StyleProfileIntervalSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StyleProfileIntervalLabel != null)
            StyleProfileIntervalLabel.Text = ((int)e.NewValue).ToString();
        SaveSettings();
        LoadStyleProfileProgress();
    }

    private void Setting_CheckChanged(object sender, RoutedEventArgs e) => SaveSettings();

    private void StyleProfileCheck_Changed(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        LoadStyleProfileProgress();
    }

    /// <summary>
    /// Check if Ollama is running and the selected model is available.
    /// </summary>
    private async Task CheckOllamaStatusAsync()
    {
        try
        {
            var endpoint = OllamaEndpointBox.Text.Trim().TrimEnd('/');
            var model = (OllamaModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "qwen2.5:0.5b";

            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var response = await client.GetAsync($"{endpoint}/api/tags");

            if (!response.IsSuccessStatusCode)
            {
                OllamaStatus.Text = "⚠️ Ollama is not responding";
                OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(240, 136, 62)); // Warning
                return;
            }

            var body = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("models", out var models))
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name) &&
                        string.Equals(name.GetString(), model, StringComparison.OrdinalIgnoreCase))
                    {
                        OllamaStatus.Text = $"✅ Connected — {name.GetString()} ready";
                        OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(35, 134, 54)); // Success
                        return;
                    }
                }
            }

            OllamaStatus.Text = $"⚠️ Model '{model}' not found. Run: ollama pull {model}";
            OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(240, 136, 62)); // Warning
        }
        catch (Exception)
        {
            OllamaStatus.Text = "⚠️ Ollama is not running. Start it first.";
            OllamaStatus.Foreground = new SolidColorBrush(Color.FromRgb(240, 136, 62)); // Warning
        }
    }

    /// <summary>
    /// Fetches the OpenRouter model list (using the in-memory cache when valid)
    /// and populates OpenRouterModelCombo with ⭐-marked recommended models at the top.
    /// Re-selects the previously saved model ID if it appears in the new list.
    /// </summary>
    private async Task LoadOpenRouterModelsAsync(bool forceRefresh = false)
    {
        // Cancel any in-flight fetch started by a previous panel activation
        _modelFetchCts?.Cancel();
        _modelFetchCts?.Dispose();
        _modelFetchCts = new CancellationTokenSource();
        var ct = _modelFetchCts.Token;

        OpenRouterModelStatus.Text       = "⟳ Loading models...";
        OpenRouterModelStatus.Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158));
        OpenRouterModelCombo.IsEnabled   = false;

        if (forceRefresh)
            OpenRouterModelService.InvalidateCache();

        try
        {
            var models = await OpenRouterModelService.GetModelsAsync(ct);
            if (ct.IsCancellationRequested) return;

            // Populate the combo on the UI thread
            OpenRouterModelCombo.Items.Clear();
            foreach (var m in models)
            {
                var prefix = m.IsRecommended ? "⭐  " : "    ";
                var label  = $"{prefix}{m.Provider} › {m.DisplayName}";
                var tooltip = m.InputPricePer1M > 0
                    ? $"{m.Id}  ·  Input: ${m.InputPricePer1M:F2} / 1M tokens"
                    : m.Id;
                OpenRouterModelCombo.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag     = m.Id,
                    ToolTip = tooltip
                });
            }

            // Re-select the previously saved model; fall back to first recommended
            bool reselected = false;
            for (int i = 0; i < OpenRouterModelCombo.Items.Count; i++)
            {
                if (OpenRouterModelCombo.Items[i] is ComboBoxItem ci &&
                    string.Equals(ci.Tag?.ToString(), _config.OpenRouterModel,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    OpenRouterModelCombo.SelectedIndex = i;
                    reselected = true;
                    break;
                }
            }
            if (!reselected)
            {
                for (int i = 0; i < OpenRouterModelCombo.Items.Count; i++)
                {
                    var tag = (OpenRouterModelCombo.Items[i] as ComboBoxItem)?.Tag?.ToString() ?? "";
                    if (OpenRouterModelService.IsRecommended(tag))
                    {
                        OpenRouterModelCombo.SelectedIndex = i;
                        break;
                    }
                }
                if (OpenRouterModelCombo.SelectedIndex < 0 && OpenRouterModelCombo.Items.Count > 0)
                    OpenRouterModelCombo.SelectedIndex = 0;
            }

            OpenRouterModelStatus.Text       = models.Count > 0 ? "" : "⚠ No models returned";
            OpenRouterModelStatus.Foreground = new SolidColorBrush(Color.FromRgb(240, 136, 62));
            OpenRouterModelCombo.IsEnabled   = true;
            UpdateReasoningWarning();
        }
        catch (OperationCanceledException) { /* panel switched away — normal */ }
        catch (Exception)
        {
            if (!ct.IsCancellationRequested)
            {
                OpenRouterModelStatus.Text       = "⚠ Failed to load — check your connection";
                OpenRouterModelStatus.Foreground = new SolidColorBrush(Color.FromRgb(240, 136, 62));
                OpenRouterModelCombo.IsEnabled   = true;
            }
        }
    }

    private void OpenRouterModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveSettings();
        UpdateReasoningWarning();
    }

    private void UpdateReasoningWarning()
    {
        var modelId = (OpenRouterModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        OpenRouterReasoningWarning.Visibility =
            OpenRouterModelService.IsReasoningFirstModel(modelId)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void OpenRouterRefresh_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadOpenRouterModelsAsync(forceRefresh: true);
    }

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

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string themeId }) return;
        _config.ThemeId = themeId;
        _config.Save();
        UpdateSwatchSelection(themeId);
        ThemeChanged?.Invoke(themeId);
        ShowSaveIndicator();
    }

    private void UpdateSwatchSelection(string themeId)
    {
        var accent = new SolidColorBrush(ThemeDefinitions.Get(themeId).ShadowColor);
        SwatchRingMidnight.Stroke = themeId == "midnight" ? accent : Brushes.Transparent;
        SwatchRingEmber.Stroke    = themeId == "ember"    ? accent : Brushes.Transparent;
        SwatchRingForest.Stroke   = themeId == "forest"   ? accent : Brushes.Transparent;
        SwatchRingRose.Stroke     = themeId == "rose"     ? accent : Brushes.Transparent;
        SwatchRingSlate.Stroke    = themeId == "slate"    ? accent : Brushes.Transparent;
    }
}
