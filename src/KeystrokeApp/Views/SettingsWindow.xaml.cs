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
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBarHelper.Apply(this);
    }

    private const double PreferredWindowWidth = 1440;
    private const double PreferredWindowHeight = 920;
    private const double MinimumWindowWidth = 1180;
    private const double MinimumWindowHeight = 780;
    private const double MaxWorkAreaWidthRatio = 0.94;
    private const double MaxWorkAreaHeightRatio = 0.92;
    private const double HeroWideModeMinWidth = 1180;
    private const double PreviewCardsTwoColumnMinWidth = 900;

    private sealed class AppChoice
    {
        public string ProcessName { get; init; } = "";
        public string WindowTitle { get; init; } = "";
        public string FriendlyName => GetFriendlyProcessName(ProcessName);
        public AppCategory.Category Category => AppCategory.GetEffectiveCategory(ProcessName, WindowTitle);
        public string CategoryLabel => GetCategoryDisplay(Category);
        public string ShortWindowTitle => TruncateWindowTitle(WindowTitle);

        public string DisplayLabel =>
            string.IsNullOrWhiteSpace(CategoryLabel)
                ? FriendlyName
                : $"{FriendlyName} ({CategoryLabel})";

        public string DetailLabel =>
            string.IsNullOrWhiteSpace(ShortWindowTitle)
                ? $"Process: {PerAppSettings.NormalizeProcessName(ProcessName)}"
                : $"{ShortWindowTitle}";

        public override string ToString() => DisplayLabel;
    }

    private sealed record ProfileSummary(
        bool PersonalizedAiEnabled,
        int AcceptedSignals,
        int DismissedSignals,
        int ContextCount,
        bool HasStyleProfile,
        bool HasVoiceProfile)
    {
        public bool HasSignals => AcceptedSignals > 0;
    }

    private AppConfig _config;
    private AcceptanceLearningService?  _learningService;
    private StyleProfileService?        _styleProfileService;
    private VocabularyProfileService?   _vocabularyProfileService;
    private LearningScoreService?       _learningScoreService;
    private readonly UsageCounters _usageCounters;
    private readonly LearningContextPreferencesService _contextPreferencesService;
    private readonly LearningContextMaintenanceService _contextMaintenanceService;
    private readonly Func<(string ProcessName, string WindowTitle)> _appPicker;
    private readonly Func<PromptPreviewSnapshot>? _promptPreviewProvider;
    private AppChoice? _lastExternalApp;
    private bool _loading = true;
    private DispatcherTimer? _saveDebounceTimer;
    private DispatcherTimer? _previewTimingTimer;
    private DateTime _previewTimingCycleStartedUtc = DateTime.UtcNow;
    private CancellationTokenSource? _modelFetchCts;

    // Shared HttpClient for Ollama status checks — avoids socket exhaustion from
    // creating/disposing clients on every engine switch or settings change.
    private static readonly System.Net.Http.HttpClient OllamaStatusClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    /// <summary>Fired immediately when the user picks a theme swatch.</summary>
    public event Action<string>? ThemeChanged;

    public SettingsWindow(
        AppConfig config,
        AcceptanceLearningService? learningService          = null,
        StyleProfileService?      styleProfileService      = null,
        VocabularyProfileService? vocabularyProfileService = null,
        LearningScoreService?     learningScoreService     = null,
        UsageCounters? usageCounters = null,
        LearningContextPreferencesService? contextPreferencesService = null,
        LearningContextMaintenanceService? contextMaintenanceService = null,
        Func<(string ProcessName, string WindowTitle)>? appPicker = null,
        Func<PromptPreviewSnapshot>? promptPreviewProvider = null)
    {
        InitializeComponent();
        _config                   = config;
        _styleProfileService      = styleProfileService;
        _vocabularyProfileService = vocabularyProfileService;
        _learningScoreService     = learningScoreService;
        _learningService          = learningService;
        _usageCounters = usageCounters ?? new UsageCounters();
        _contextPreferencesService = contextPreferencesService ?? new LearningContextPreferencesService();
        _contextMaintenanceService = contextMaintenanceService ?? new LearningContextMaintenanceService();
        _appPicker = appPicker ?? AppContextService.GetActiveWindow;
        _promptPreviewProvider = promptPreviewProvider;
        LoadValues();
        LoadLearningStats();
        LoadVocabularyProfileStatus();
        UpdateSwatchSelection(_config.ThemeId);
        ShowSection("Overview");
        _loading = false;
        Loaded += OnLoaded;
        Closed += OnWindowClosed;
        SizeChanged += OnWindowSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowSizing();
        Dispatcher.BeginInvoke(UpdateResponsiveLayout, DispatcherPriority.Loaded);
        RestartPreviewTimingSimulation();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _saveDebounceTimer?.Stop();
        _saveDebounceTimer = null;
        _previewTimingTimer?.Stop();
        _previewTimingTimer = null;
        _modelFetchCts?.Cancel();
        _modelFetchCts?.Dispose();
        _modelFetchCts = null;
        _learningService = null;
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout();

    private void ApplyWindowSizing()
    {
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinimumWindowWidth, workArea.Width * MaxWorkAreaWidthRatio);
        var maxHeight = Math.Max(MinimumWindowHeight, workArea.Height * MaxWorkAreaHeightRatio);

        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        MinWidth = Math.Min(MinimumWindowWidth, maxWidth);
        MinHeight = Math.Min(MinimumWindowHeight, maxHeight);
        Width = Math.Min(PreferredWindowWidth, maxWidth);
        Height = Math.Min(PreferredWindowHeight, maxHeight);
    }

    private void ShowSection(string section)
    {
        OverviewSection.Visibility = section == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        SuggestionsSection.Visibility = section == "Suggestions" ? Visibility.Visible : Visibility.Collapsed;
        PersonalizationSection.Visibility = section == "Personalization" ? Visibility.Visible : Visibility.Collapsed;
        AppControlSection.Visibility = section == "AppControl" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceSection.Visibility = section == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        AdvancedSection.Visibility = section == "Advanced" ? Visibility.Visible : Visibility.Collapsed;

        if (section == "AppControl")
            RefreshAppPickerOptions();

        UpdatePrivacyInspector();
        Dispatcher.BeginInvoke(() =>
        {
            MainSectionScrollViewer?.ScrollToTop();
            UpdateResponsiveLayout();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateResponsiveLayout()
    {
        if (HeroHeaderGrid == null || HeroSummaryPanel == null || PreviewBorder == null || PreviewCardsGrid == null)
            return;

        bool compactHero = HeroHeaderGrid.ActualWidth > 0 && HeroHeaderGrid.ActualWidth < HeroWideModeMinWidth;
        bool stackPreviewCards = compactHero || (PreviewBorder.ActualWidth > 0 && PreviewBorder.ActualWidth < PreviewCardsTwoColumnMinWidth);

        if (compactHero)
        {
            HeroHeaderGrid.ColumnDefinitions[1].Width = new GridLength(0);
            HeroHeaderGrid.RowDefinitions[1].Height = GridLength.Auto;

            Grid.SetColumn(HeroSummaryPanel, 0);
            Grid.SetRow(HeroSummaryPanel, 0);
            Grid.SetColumnSpan(HeroSummaryPanel, 2);
            Grid.SetRowSpan(HeroSummaryPanel, 1);
            HeroSummaryPanel.Margin = new Thickness(0, 0, 0, 18);

            Grid.SetColumn(PreviewBorder, 0);
            Grid.SetRow(PreviewBorder, 1);
            Grid.SetColumnSpan(PreviewBorder, 2);
            Grid.SetRowSpan(PreviewBorder, 1);
        }
        else
        {
            HeroHeaderGrid.ColumnDefinitions[1].Width = new GridLength(500);
            HeroHeaderGrid.RowDefinitions[1].Height = new GridLength(0);

            Grid.SetColumn(HeroSummaryPanel, 0);
            Grid.SetRow(HeroSummaryPanel, 0);
            Grid.SetColumnSpan(HeroSummaryPanel, 1);
            Grid.SetRowSpan(HeroSummaryPanel, 2);
            HeroSummaryPanel.Margin = new Thickness(0, 0, 24, 0);

            Grid.SetColumn(PreviewBorder, 1);
            Grid.SetRow(PreviewBorder, 0);
            Grid.SetColumnSpan(PreviewBorder, 1);
            Grid.SetRowSpan(PreviewBorder, 2);
        }

        if (stackPreviewCards)
        {
            PreviewCardsGrid.ColumnDefinitions[1].Width = new GridLength(0);
            PreviewCardsGrid.RowDefinitions[1].Height = GridLength.Auto;

            Grid.SetColumn(PreviewTimingCard, 0);
            Grid.SetRow(PreviewTimingCard, 0);
            Grid.SetColumnSpan(PreviewTimingCard, 2);
            PreviewTimingCard.Margin = new Thickness(0, 0, 0, 12);

            Grid.SetColumn(PreviewCompletionCard, 0);
            Grid.SetRow(PreviewCompletionCard, 1);
            Grid.SetColumnSpan(PreviewCompletionCard, 2);
            PreviewCompletionCard.Margin = new Thickness(0);
        }
        else
        {
            PreviewCardsGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            PreviewCardsGrid.RowDefinitions[1].Height = new GridLength(0);

            Grid.SetColumn(PreviewTimingCard, 0);
            Grid.SetRow(PreviewTimingCard, 0);
            Grid.SetColumnSpan(PreviewTimingCard, 1);
            PreviewTimingCard.Margin = new Thickness(0, 0, 10, 0);

            Grid.SetColumn(PreviewCompletionCard, 1);
            Grid.SetRow(PreviewCompletionCard, 0);
            Grid.SetColumnSpan(PreviewCompletionCard, 1);
            PreviewCompletionCard.Margin = new Thickness(10, 0, 0, 0);
        }
    }

    private void UpdateExperienceSummary()
    {
        string length = LengthCombo.SelectedItem is ComboBoxItem lengthItem
            ? lengthItem.Tag?.ToString() ?? "extended"
            : "extended";

        string lengthLabel = length switch
        {
            "brief" => "brief suggestions",
            "standard" => "standard suggestions",
            "extended" => "balanced suggestions",
            "unlimited" => "deep suggestions",
            _ => "balanced suggestions"
        };

        string speedLabel = DebounceSlider.Value <= 200 ? "quick to appear"
            : DebounceSlider.Value <= 400 ? "paced for flow"
            : "deliberate and cautious";

        HeroTitleText.Text = $"Keystroke is tuned for {lengthLabel}";
        HeroSubtitleText.Text = $"It feels {speedLabel}, with {SuggestionsSlider.Value:0} option{(SuggestionsSlider.Value == 1 ? "" : "s")} ready when you pause.";

        var engineName = (EngineCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Gemini";
        var contextOn = RollingContextCheck.IsChecked == true || OcrEnabledCheck.IsChecked == true;

        HeroEngineStatusText.Text = engineName;
        HeroContextStatusText.Text = contextOn ? "Context on" : "Context light";
        HeroThemeStatusText.Text = $"Theme: {ThemeDefinitions.Get(_config.ThemeId).DisplayName}";

        OverviewFeelText.Text = $"Configured for {lengthLabel} that feel {speedLabel}.";
        OverviewLengthSummaryText.Text = char.ToUpper(lengthLabel[0]) + lengthLabel[1..];
        OverviewSpeedSummaryText.Text = $"{(int)DebounceSlider.Value}ms after words";
        OverviewSuggestionSummaryText.Text = $"{(int)SuggestionsSlider.Value} suggestion{(SuggestionsSlider.Value == 1 ? "" : "s")} shown";

        OverviewEngineStatusText.Text = $"{engineName} is active.";

        var privacyBits = new List<string>();
        privacyBits.Add(OcrEnabledCheck.IsChecked == true ? "screen context on" : "screen context off");
        privacyBits.Add(RollingContextCheck.IsChecked == true ? "recent text memory on" : "recent text memory off");
        OverviewPrivacyStatusText.Text = string.Join(", ", privacyBits) + ".";
        UpdateProfileMessaging();
    }

    private void UpdateFeatureCards()
    {
        RollingContextStatusText.Text = RollingContextCheck.IsChecked == true
            ? "Recent text is being remembered so suggestions stay coherent."
            : "Only the immediate text is used, so suggestions reset faster between thoughts.";

        OcrStatusText.Text = OcrEnabledCheck.IsChecked == true
            ? "Visible text on screen helps ground suggestions in what you are looking at."
            : "No screen text is captured, which keeps privacy tighter but reduces grounding.";

        bool learningOn = LearningEnabledCheck.IsChecked == true;
        StyleProfileCheck.IsEnabled = learningOn;
        StyleProfileIntervalSlider.IsEnabled = learningOn && StyleProfileCheck.IsChecked == true;
        StyleProfileDependencyText.Visibility = learningOn ? Visibility.Collapsed : Visibility.Visible;
        StyleProfileCard.Opacity = learningOn ? 1.0 : 0.72;
        UpdateProfileMessaging();
    }

    private UsageCountersSnapshot GetUsageSnapshot() => _usageCounters.GetSnapshot();

    private ProfileSummary GetProfileSummary()
    {
        var stats = _learningService?.GetStats();
        var styleProfile = _styleProfileService?.GetProfile();
        var vocabProfile = _vocabularyProfileService?.GetProfile();

        return new ProfileSummary(
            LearningEnabledCheck.IsChecked == true,
            stats?.TotalAccepted ?? 0,
            stats?.TotalDismissed ?? 0,
            stats?.ContextSummaries.Count ?? 0,
            styleProfile != null && !string.IsNullOrWhiteSpace(styleProfile.GeneralProfile),
            vocabProfile != null && vocabProfile.Categories.Count > 0);
    }

    private void UpdateProfileMessaging()
    {
        var summary = GetProfileSummary();
        var usage = GetUsageSnapshot();
        var acceptedText = $"{summary.AcceptedSignals} accepted signal{(summary.AcceptedSignals == 1 ? "" : "s")}";
        var trackedText = $"{usage.TotalAcceptedSuggestions} completion{(usage.TotalAcceptedSuggestions == 1 ? "" : "s")} tracked";
        var contextText = summary.ContextCount > 0
            ? $"{summary.ContextCount} context{(summary.ContextCount == 1 ? "" : "s")}"
            : "no repeated contexts yet";

        HeroLearningStatusText.Text = summary.PersonalizedAiEnabled
            ? "AI profile active"
            : usage.TotalAcceptedSuggestions > 0
                ? "AI profile building"
                : "AI profile off";

        OverviewLearningStatusText.Text = summary.PersonalizedAiEnabled
            ? summary.HasSignals
                ? $"{acceptedText} are actively shaping future completions."
                : "Personalized AI is on, but it needs a few accepted suggestions before it can adapt."
            : usage.TotalAcceptedSuggestions > 0
                ? $"Your AI profile is quietly building from {trackedText}, but none of it is used in completions yet."
                : "Personalized AI is off. Accept a few suggestions and Keystroke will start building a profile you can activate later.";

        OverviewPrivacyDetailText.Text = $"Screen reading is {(OcrEnabledCheck.IsChecked == true ? "on" : "off")}. AI profile is {(summary.PersonalizedAiEnabled ? "active" : summary.HasSignals ? "building quietly" : "idle")}.";

        OverviewProfileSummaryText.Text = summary.PersonalizedAiEnabled
            ? summary.HasSignals
                ? $"{acceptedText} are live, with {contextText} helping Keystroke match your tone."
                : "Personalized AI is on and waiting for your first accepted suggestion."
            : usage.TotalAcceptedSuggestions > 0
                ? $"{trackedText} — your AI profile is already building."
                : "Keystroke has not started building your profile yet.";

        OverviewProfileDetailText.Text = summary.PersonalizedAiEnabled
            ? summary.HasStyleProfile || summary.HasVoiceProfile
                ? "Writing style and voice hints are ready in at least some contexts."
                : "Keystroke is collecting enough signal to generate richer writing-style hints."
            : usage.TotalAcceptedSuggestions > 0
                ? "Profile signals stay local until you turn Personalized AI on."
                : "Open Your AI Profile to see what Keystroke collects before any personalization turns on.";

        ProfileStatusTitleText.Text = summary.PersonalizedAiEnabled
            ? summary.HasSignals
                ? "Personalized AI is active."
                : "Personalized AI is on and waiting for signal."
            : usage.TotalAcceptedSuggestions > 0
                ? "Your AI profile is already building."
                : "Your AI profile has not started yet.";

        ProfileStatusBodyText.Text = summary.PersonalizedAiEnabled
            ? summary.HasSignals
                ? $"{acceptedText} across {contextText} can now influence completions, while screen and recent-text context keep suggestions grounded."
                : "Keystroke will start adapting after your first few accepted suggestions."
            : usage.TotalAcceptedSuggestions > 0
                ? $"{trackedText} are already stored locally across {contextText}. Keystroke will not send those hints upstream until you turn Personalized AI on."
                : "Accept a few suggestions and Keystroke will start building a profile you can keep passive or turn on for active personalization.";

        ProfileSignalsBadgeText.Text = summary.PersonalizedAiEnabled ? acceptedText : trackedText;
        ProfileContextsBadgeText.Text = summary.ContextCount > 0
            ? $"{summary.ContextCount} context{(summary.ContextCount == 1 ? "" : "s")}"
            : "No contexts yet";
        ProfileModeBadgeText.Text = summary.PersonalizedAiEnabled ? "Personalized AI active" : "Passive capture only";
        ProfileStatusActionButton.Visibility = summary.PersonalizedAiEnabled ? Visibility.Collapsed : Visibility.Visible;

        LearningFeatureStatusText.Text = summary.PersonalizedAiEnabled
            ? summary.HasSignals
                ? $"{acceptedText} are helping Keystroke better match your phrasing."
                : "Personalized AI is ready. Accept a few suggestions and Keystroke will start adapting."
            : usage.TotalAcceptedSuggestions > 0
                ? $"Keystroke is still collecting {trackedText}, but they stay local until you turn Personalized AI on."
                : "Keystroke can start by quietly collecting accepted suggestions, then use them later if you turn Personalized AI on.";

        StyleProfileFeatureText.Text = summary.PersonalizedAiEnabled
            ? "Builds a writing-style summary so phrasing and tone feel more like you over time."
            : "Keystroke only generates a writing-style summary when Personalized AI is on.";

        UpdateMonetizationPanels(usage, summary.PersonalizedAiEnabled);
    }

    private void UpdateMonetizationPanels(UsageCountersSnapshot usage, bool personalizedAiEnabled)
    {
        var freeUser = !personalizedAiEnabled;
        var trackedText = $"{usage.TotalAcceptedSuggestions} completion{(usage.TotalAcceptedSuggestions == 1 ? "" : "s")} tracked";

        if (PersonalizationTeaserCard != null)
            PersonalizationTeaserCard.Visibility = freeUser ? Visibility.Visible : Visibility.Collapsed;
        if (LearningUpgradeBanner != null)
            LearningUpgradeBanner.Visibility = freeUser ? Visibility.Visible : Visibility.Collapsed;
        if (ProfileIntelligencePanel != null)
            ProfileIntelligencePanel.Visibility = freeUser ? Visibility.Collapsed : Visibility.Visible;
        if (CategoryBreakdownLocked != null)
            CategoryBreakdownLocked.Visibility = freeUser ? Visibility.Visible : Visibility.Collapsed;

        if (CategoryBreakdownLockedBodyText != null)
        {
            CategoryBreakdownLockedBodyText.Text = usage.TotalAcceptedSuggestions > 0
                ? $"{trackedText} — activate Personalized AI to unlock category intelligence, writing style, and voice guidance."
                : "No completions tracked yet — accept a few suggestions and Keystroke will start building your AI profile.";
        }
    }

    private void UpdateAppFilteringUi()
    {
        var mode = GetSelectedAppFilteringMode();
        var allowListOnly = mode == PerAppSettings.AllowListedOnly;
        int blockedCount = PerAppSettings.ParseProcessList(BlockedAppsBox.Text).Count;
        int allowedCount = PerAppSettings.ParseProcessList(AllowedAppsBox.Text).Count;

        AppFilteringDescriptionText.Text = allowListOnly
            ? "Keystroke will only appear in the apps listed on the right. This is best when you want a very small, intentional allow list."
            : "Keystroke stays available everywhere except the apps listed on the left. This is the easiest way to keep it out of games and other noisy contexts.";

        BlockedAppsCard.Opacity = allowListOnly ? 0.72 : 1.0;
        AllowedAppsCard.Opacity = allowListOnly ? 1.0 : 0.9;

        HeroAppControlStatusText.Text = allowListOnly
            ? $"App control: {allowedCount} allowed"
            : $"App control: {blockedCount} blocked";

        OverviewAppControlSummaryText.Text = allowListOnly
            ? allowedCount > 0
                ? $"Keystroke only appears in {allowedCount} allowed app{(allowedCount == 1 ? "" : "s")}."
                : "Keystroke is set to allow-list mode, but no apps are listed yet."
            : blockedCount > 0
                ? $"Keystroke stays available broadly, with {blockedCount} blocked app{(blockedCount == 1 ? "" : "s")}."
                : "Keystroke is available everywhere right now.";

        OverviewAppControlDetailText.Text = allowListOnly
            ? "Great when you only want Keystroke in a small trusted set like chat or email apps."
            : "Open App Control to block noisy apps, games, terminals, or anywhere the overlay should stay quiet.";

        if (AppPresetSummaryText != null)
        {
            AppPresetSummaryText.Text = allowListOnly
                ? allowedCount > 0
                    ? $"Allow-list mode is active with {allowedCount} app{(allowedCount == 1 ? "" : "s")} ready."
                    : "Allow-list mode is active. Add the apps where Keystroke should appear."
                : blockedCount > 0
                    ? $"Broad mode is active with {blockedCount} blocked app{(blockedCount == 1 ? "" : "s")}."
                    : "Keystroke is available broadly. Block only the apps where it should stay quiet.";
        }
    }

    private void UpdatePreview()
    {
        var preset = (LengthCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "extended";
        var suggestionText = GetPreviewSuggestionText(preset);
        var wordCount = CountWords(suggestionText);
        var charCount = suggestionText.Trim().Length;
        var creativityLabel = GetCreativityLabel(TempSlider.Value);
        var targetRange = GetPreviewLengthRange(preset);
        var debounceMs = (int)Math.Round(DebounceSlider.Value);
        var fastDebounceMs = (int)Math.Round(FastDebounceSlider.Value);
        var debounceDelta = Math.Abs(debounceMs - fastDebounceMs);

        PreviewSuggestionText.Text = suggestionText;
        PreviewLengthBadgeText.Text = targetRange.Label;
        PreviewWordCountText.Text = $"{wordCount} word{(wordCount == 1 ? "" : "s")}";
        PreviewCharCountText.Text = $"{charCount} char{(charCount == 1 ? "" : "s")}";
        PreviewMetaText.Text = $"{(LengthCombo.SelectedItem as ComboBoxItem)?.Content} | {(int)SuggestionsSlider.Value} suggestion{(SuggestionsSlider.Value == 1 ? "" : "s")} | {creativityLabel}";
        PreviewLengthDetailText.Text = $"This preview is showing about {wordCount} words ({charCount} characters), which sits in the expected {targetRange.RangeText} range for this length setting.";
        PreviewDebounceStatusText.Text = fastDebounceMs < debounceMs
            ? $"Mid-thought suggestions reach the trigger about {debounceDelta}ms sooner than sentence-end suggestions."
            : fastDebounceMs > debounceMs
                ? $"Sentence-end suggestions reach the trigger about {debounceDelta}ms sooner than mid-thought suggestions."
                : "Both timing modes are currently set to trigger at the same speed.";
        PreviewDebounceDetailText.Text = $"The preview bars loop continuously so you can feel the difference between a {debounceMs}ms punctuation pause and a {fastDebounceMs}ms in-line pause.";

        var theme = ThemeDefinitions.Get(_config.ThemeId);
        var accentBrush = new SolidColorBrush(theme.ShadowColor);
        var bubbleBrush = new SolidColorBrush(Color.FromArgb(255,
            (byte)Math.Min(255, theme.ShadowColor.R / 2 + 28),
            (byte)Math.Min(255, theme.ShadowColor.G / 2 + 32),
            (byte)Math.Min(255, theme.ShadowColor.B / 2 + 54)));

        PreviewAccentDot.Fill = accentBrush;
        PreviewBorder.BorderBrush = new SolidColorBrush(theme.NormalBorder);
        PreviewSuggestionBubble.BorderBrush = accentBrush;
        PreviewSuggestionBubble.Background = bubbleBrush;
        PreviewDebounceProgressBar.Foreground = accentBrush;
        PreviewFastDebounceProgressBar.Foreground = accentBrush;
        UpdateResponsiveLayout();
        RestartPreviewTimingSimulation();
    }

    private void RestartPreviewTimingSimulation()
    {
        if (PreviewDebounceProgressBar == null || PreviewFastDebounceProgressBar == null)
            return;

        if (_previewTimingTimer == null)
        {
            _previewTimingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _previewTimingTimer.Tick += (_, _) => AdvancePreviewTimingSimulation();
        }

        _previewTimingCycleStartedUtc = DateTime.UtcNow;
        AdvancePreviewTimingSimulation();
        _previewTimingTimer.Start();
    }

    private void AdvancePreviewTimingSimulation()
    {
        if (PreviewDebounceProgressBar == null ||
            PreviewFastDebounceProgressBar == null ||
            PreviewDebounceProgressText == null ||
            PreviewFastDebounceProgressText == null ||
            PreviewSuggestionBubble == null)
        {
            return;
        }

        var debounceMs = Math.Max(1, (int)Math.Round(DebounceSlider.Value));
        var fastDebounceMs = Math.Max(1, (int)Math.Round(FastDebounceSlider.Value));
        var elapsedMs = (DateTime.UtcNow - _previewTimingCycleStartedUtc).TotalMilliseconds;
        var cycleLengthMs = Math.Max(debounceMs, fastDebounceMs) + 900;

        if (elapsedMs >= cycleLengthMs)
        {
            _previewTimingCycleStartedUtc = DateTime.UtcNow;
            elapsedMs = 0;
        }

        UpdatePreviewTimingLane(PreviewDebounceProgressBar, PreviewDebounceProgressText, elapsedMs, debounceMs);
        UpdatePreviewTimingLane(PreviewFastDebounceProgressBar, PreviewFastDebounceProgressText, elapsedMs, fastDebounceMs);

        var earliestTriggerMs = Math.Min(debounceMs, fastDebounceMs);
        PreviewSuggestionBubble.Opacity = elapsedMs >= earliestTriggerMs ? 1.0 : 0.45;
    }

    private static void UpdatePreviewTimingLane(
        ProgressBar progressBar,
        TextBlock statusText,
        double elapsedMs,
        int targetMs)
    {
        var progress = Math.Clamp(elapsedMs / targetMs, 0.0, 1.0);
        progressBar.Value = progress * 100;

        if (elapsedMs >= targetMs)
        {
            statusText.Text = "Appears now";
            return;
        }

        var remainingMs = Math.Max(0, targetMs - (int)Math.Round(elapsedMs));
        statusText.Text = $"{remainingMs}ms left";
    }

    private static (string Label, string RangeText) GetPreviewLengthRange(string preset) => preset switch
    {
        "brief" => ("Brief range", "3-5 words"),
        "standard" => ("Standard range", "8-15 words"),
        "unlimited" => ("Unlimited range", "30-50 words"),
        _ => ("Extended range", "15-30 words")
    };

    private static string GetPreviewSuggestionText(string preset) => preset switch
    {
        "brief" => ", will send it shortly.",
        "standard" => ", and I'll send the cleaned-up draft over later this afternoon.",
        "unlimited" => ", and I'll tighten the draft, smooth the tone, double-check the wording, add a cleaner opening, clarify the ask, and send a polished version before the meeting so everyone has a copy that's easy to review and ready to forward.",
        _ => ", and I'll tighten the draft, smooth the tone, and send a polished version this afternoon so it reads cleanly."
    };

    private static string GetCreativityLabel(double temperature)
    {
        if (temperature < 0.2)
            return "Very focused";
        if (temperature < 0.45)
            return "Balanced creativity";
        if (temperature < 0.75)
            return "More expressive";

        return "Most exploratory";
    }

    private static int CountWords(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count = 0;

        foreach (var part in parts)
        {
            foreach (var ch in part)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
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
        LimitEnabledCheck.IsChecked = _config.LimitEnabled;

        StyleProfileCheck.IsChecked = _config.StyleProfileEnabled;
        StyleProfileIntervalSlider.Value = _config.StyleProfileInterval;
        StyleProfileIntervalLabel.Text = _config.StyleProfileInterval.ToString();
        LoadStyleProfileStatus();
        LoadStyleProfileProgress();

        SelectComboItemByTag(AppFilteringModeCombo, _config.AppFilteringMode);
        BlockedAppsBox.Text = PerAppSettings.FormatProcessList(_config.BlockedProcesses);
        AllowedAppsBox.Text = PerAppSettings.FormatProcessList(_config.AllowedProcesses);

        PromptBox.Text = _config.EffectiveSystemPrompt;
        UpdateAppFilteringUi();
        RefreshAppPickerOptions();
        UpdateFeatureCards();
        UpdateExperienceSummary();
        UpdatePreview();
        UpdatePrivacyInspector();
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
            ContextBreakdownPanel.Children.Clear();
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

                if (_config.LearningUiV2Enabled && stats.ContextSummaries.Count > 0)
                {
                    if (stats.LegacyEvidenceCount > 0)
                    {
                        ContextBreakdownPanel.Children.Add(new TextBlock
                        {
                            Text = stats.DedupedLegacyCount > 0
                                ? $"Imported {stats.LegacyEvidenceCount} legacy learning records at lower confidence. Filtered {stats.DedupedLegacyCount} dual-written duplicates from the V2 rollout."
                                : $"Imported {stats.LegacyEvidenceCount} legacy learning records at lower confidence while the V2 event log builds up.",
                            Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 8)
                        });
                    }

                    foreach (var summary in stats.ContextSummaries)
                        ContextBreakdownPanel.Children.Add(CreateContextCard(summary));
                }
                else
                {
                    ContextBreakdownPanel.Children.Add(new TextBlock
                    {
                        Text = _config.LearningUiV2Enabled
                            ? "Context summaries will appear after Keystroke sees repeated patterns in a specific thread, document, or workspace."
                            : "Context-aware learning UI is disabled.",
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                        FontSize = 12,
                        FontStyle = FontStyles.Italic,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                }
            }
            else
            {
                CategoryBreakdownPanel.Children.Add(new TextBlock
                {
                    Text       = "No profile data yet. Accept a few suggestions and Keystroke will start mapping what feels natural to you.",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                    FontSize   = 12,
                    FontStyle  = FontStyles.Italic,
                    Margin     = new Thickness(0, 8, 0, 0)
                });

                if (_config.LearningUiV2Enabled && stats.ContextSummaries.Count > 0)
                {
                    foreach (var summary in stats.ContextSummaries)
                        ContextBreakdownPanel.Children.Add(CreateContextCard(summary));
                }
                else
                {
                    ContextBreakdownPanel.Children.Add(new TextBlock
                    {
                        Text = "No context data yet. Keystroke will start grouping repeated patterns after a few accepted suggestions or manual continuations.",
                        Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
                        FontSize = 12,
                        FontStyle = FontStyles.Italic,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                }
            }
        }
        catch (Exception) { /* Learning stats display is non-critical — failure is safe to ignore */ }
    }

    private void LoadStyleProfileStatus()
    {
        try
        {
            if (LearningEnabledCheck.IsChecked != true)
            {
                StyleProfileStatus.Text = "Personalized AI is off. Keystroke can still collect profile signals quietly, but it will not generate a writing-style summary yet.";
                return;
            }

            var profilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Keystroke", "style-profile.json");

            if (!File.Exists(profilePath))
            {
                StyleProfileStatus.Text = "No writing-style summary generated yet. Accept suggestions to build one.";
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
            var showProgress = LearningEnabledCheck.IsChecked == true && StyleProfileCheck.IsChecked == true;
            StyleProgressPanel.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;

            if (!showProgress || _styleProfileService == null)
            {
                LoadCategoryBadges([]);
                return;
            }

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
            if (LearningEnabledCheck.IsChecked != true)
            {
                VocabProfileStatus.Text = "Personalized AI is off. Keystroke is not generating a voice fingerprint yet, even if profile signals are already being collected.";
                VocabCategoryBadgesPanel.Children.Clear();
                return;
            }

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

    private UIElement CreateContextCard(LearningContextSummary summary)
    {
        var confidencePercent = (int)Math.Round(summary.Confidence * 100);
        var matchRatePercent = (int)Math.Round(summary.MatchRate * 100);
        var qualityPercent = (int)Math.Round(summary.AverageQuality * 100);
        var accent = summary.IsDisabled
            ? Color.FromRgb(139, 148, 158)
            : confidencePercent >= 75
                ? Color.FromRgb(63, 185, 80)
                : confidencePercent >= 50
                    ? Color.FromRgb(47, 129, 247)
                    : confidencePercent >= 30
                        ? Color.FromRgb(240, 136, 62)
                        : Color.FromRgb(139, 148, 158);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 27, 34)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(96, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8),
            Opacity = summary.IsDisabled ? 0.82 : 1.0
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = summary.ContextLabel,
            Foreground = new SolidColorBrush(Color.FromRgb(240, 246, 252)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"{summary.Category} context · {FormatRelativeTime(summary.LastActivity)}",
            Foreground = new SolidColorBrush(Color.FromRgb(139, 148, 158)),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 8)
        });

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 8)
        };
        actionRow.Children.Add(CreateContextActionButton(
            summary.IsPinned ? "Unpin" : "Pin",
            accent,
            (_, _) => ToggleContextPinned(summary)));
        actionRow.Children.Add(CreateContextActionButton(
            summary.IsDisabled ? "Enable" : "Disable",
            accent,
            (_, _) => ToggleContextDisabled(summary)));
        actionRow.Children.Add(CreateContextActionButton(
            "Clear",
            Color.FromRgb(240, 136, 62),
            (_, _) => ClearContextData(summary)));
        panel.Children.Add(actionRow);

        if (summary.IsDisabled)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Learning is paused here. Keystroke will not log new evidence or use this context to shape suggestions until you re-enable it.",
                Foreground = new SolidColorBrush(Color.FromRgb(173, 181, 189)),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var pillRow = new WrapPanel { Orientation = Orientation.Horizontal };
        if (summary.IsPinned)
        {
            AddPill(
                pillRow,
                "Pinned",
                Color.FromArgb(40, 47, 129, 247),
                Color.FromRgb(79, 172, 254),
                "Pinned contexts stay at the top of the learning dashboard.");
        }
        if (summary.IsDisabled)
        {
            AddPill(
                pillRow,
                "Learning off",
                Color.FromArgb(40, 139, 148, 158),
                Color.FromRgb(139, 148, 158),
                "New evidence from this context is ignored until you re-enable it.");
        }
        AddPill(
            pillRow,
            $"{confidencePercent}% confidence",
            Color.FromArgb(40, accent.R, accent.G, accent.B),
            accent,
            "How strongly Keystroke trusts this context-specific pattern set.");
        AddPill(
            pillRow,
            $"{matchRatePercent}% match rate",
            Color.FromArgb(40, 47, 129, 247),
            Color.FromRgb(79, 172, 254),
            "Accepted and committed evidence versus rejected patterns in this context.");
        AddPill(
            pillRow,
            $"{summary.NativeCount} native / {summary.AssistCount} assist",
            Color.FromArgb(40, 99, 110, 123),
            Color.FromRgb(201, 209, 217),
            "Native writing is weighted more heavily than accepted model text.");
        if (summary.LegacyCount > 0)
        {
            AddPill(
                pillRow,
                $"{summary.LegacyCount} legacy",
                Color.FromArgb(40, 139, 148, 158),
                Color.FromRgb(173, 181, 189),
                "Imported from completions.jsonl at lower confidence during the V2 migration.");
        }
        if (summary.NegativeCount > 0)
        {
            AddPill(
                pillRow,
                $"{summary.NegativeCount} rejected",
                Color.FromArgb(40, 240, 136, 62),
                Color.FromRgb(240, 136, 62),
                "Dismissed or typed-past suggestions are tracked as negative evidence.");
        }
        if (summary.AverageQuality > 0)
        {
            AddPill(
                pillRow,
                $"{qualityPercent}% quality",
                Color.FromArgb(40, 63, 185, 80),
                Color.FromRgb(63, 185, 80),
                "Average quality of positive evidence kept for this context.");
        }

        panel.Children.Add(pillRow);
        card.Child = panel;
        return card;
    }

    private Button CreateContextActionButton(string text, Color accent, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Background = new SolidColorBrush(Color.FromArgb(24, accent.R, accent.G, accent.B)),
            Foreground = new SolidColorBrush(Color.FromRgb(240, 246, 252)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11,
            MinWidth = 56,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += onClick;
        return button;
    }

    private void ToggleContextPinned(LearningContextSummary summary)
    {
        _contextPreferencesService.SetPinned(
            summary.ContextKey,
            summary.ContextLabel,
            summary.Category,
            !summary.IsPinned);
        RefreshLearningViews();
    }

    private void ToggleContextDisabled(LearningContextSummary summary)
    {
        var isDisabling = !summary.IsDisabled;
        if (isDisabling)
        {
            var result = MessageBox.Show(
                $"Pause learning for \"{summary.ContextLabel}\"?\n\nKeystroke will stop logging new evidence here until you re-enable it.",
                "Disable Context Learning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
                return;
        }

        _contextPreferencesService.SetDisabled(
            summary.ContextKey,
            summary.ContextLabel,
            summary.Category,
            isDisabling);
        RefreshLearningViews(invalidateDerivedArtifacts: true);
    }

    private void ClearContextData(LearningContextSummary summary)
    {
        var result = MessageBox.Show(
            $"Clear everything Keystroke has learned for \"{summary.ContextLabel}\"?\n\nThis removes stored examples for that context but keeps the rest of your learning history.",
            "Clear Context Data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        _contextMaintenanceService.ClearContext(summary.ContextKey);
        RefreshLearningViews(invalidateDerivedArtifacts: true);
    }

    private void RefreshLearningViews(bool invalidateDerivedArtifacts = false)
    {
        if (invalidateDerivedArtifacts)
        {
            _contextMaintenanceService.InvalidateDerivedArtifacts();
            _styleProfileService?.InvalidateProfile();
            _vocabularyProfileService?.InvalidateProfile();
        }

        _learningService?.Refresh();
        LoadLearningStats();
        LoadStyleProfileStatus();
        LoadStyleProfileProgress();
        LoadVocabularyProfileStatus();
        UpdateProfileMessaging();
        UpdateExperienceSummary();
        ShowSaveIndicator();
    }

    private static string FormatRelativeTime(DateTime timestampUtc)
    {
        var delta = DateTime.UtcNow - timestampUtc;
        if (delta.TotalMinutes < 1)
            return "active just now";
        if (delta.TotalHours < 1)
            return $"active {(int)delta.TotalMinutes}m ago";
        if (delta.TotalDays < 1)
            return $"active {(int)delta.TotalHours}h ago";
        return $"active {(int)delta.TotalDays}d ago";
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
        ProviderDetailIntroText.Text = e switch
        {
            0 => "Gemini is active. This is the recommended default provider, and Gemini 3.1 Flash-Lite Preview is the default model because it has given the best speed, accuracy, and cost balance in our testing.",
            1 => "GPT-5 is active. Add your OpenAI key and pick the model size you want.",
            2 => "Claude is active. Add your Anthropic key and choose the tradeoff between speed and quality.",
            3 => "Ollama is active. Make sure your local endpoint and pulled model are ready.",
            4 => "OpenRouter is active. Use one key to access many hosted models.",
            _ => "Dummy mode is active for testing only."
        };

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
        _config.GeminiModel = (GeminiModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? AppConfig.DefaultGeminiModel;
        _config.Gpt5Model = (Gpt5ModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? AppConfig.DefaultGpt5Model;
        _config.ClaudeModel = (ClaudeModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? AppConfig.DefaultClaudeModel;
        _config.OllamaModel = (OllamaModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? AppConfig.DefaultOllamaModel;
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
        _config.LimitEnabled = LimitEnabledCheck.IsChecked == true;
        _config.StyleProfileEnabled = StyleProfileCheck.IsChecked == true;
        _config.StyleProfileInterval = (int)StyleProfileIntervalSlider.Value;
        _config.MaxSuggestions = (int)SuggestionsSlider.Value;
        _config.AppFilteringMode = GetSelectedAppFilteringMode();
        _config.BlockedProcesses = PerAppSettings.ParseProcessList(BlockedAppsBox.Text);
        _config.AllowedProcesses = PerAppSettings.ParseProcessList(AllowedAppsBox.Text);
        var promptText = PromptBox.Text.Trim();
        _config.CustomSystemPrompt = (promptText == AppConfig.DefaultSystemPrompt) ? null : promptText;

        _config.Save();
        UpdateGeminiApiKeyStatus();
        UpdateAppFilteringUi();
        UpdateFeatureCards();
        UpdateExperienceSummary();
        LoadStyleProfileStatus();
        LoadStyleProfileProgress();
        LoadVocabularyProfileStatus();
        UpdatePreview();
        UpdatePrivacyInspector();
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
            StyleProfileCheck.IsChecked = false;
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
            "This will clear your AI profile data. Keystroke will forget the patterns it has learned about your writing and start fresh.\n\nContinue?",
            "Reset AI Profile",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                var appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Keystroke");
                var resetTargets = new[]
                {
                    Path.Combine(appDataPath, "completions.jsonl"),
                    Path.Combine(appDataPath, "tracking.jsonl"),
                    Path.Combine(appDataPath, "learning-events.v2.jsonl"),
                    Path.Combine(appDataPath, "learning-context-preferences.json"),
                    Path.Combine(appDataPath, "usage.json"),
                    Path.Combine(appDataPath, "style-profile.json"),
                    Path.Combine(appDataPath, "vocabulary-profile.json"),
                    Path.Combine(appDataPath, "learning-scores.json")
                };

                foreach (var path in resetTargets)
                {
                    if (!File.Exists(path))
                        continue;

                    if (Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
                        File.WriteAllText(path, "");
                    else
                        File.Delete(path);
                }

                _usageCounters.Reset();
                LoadLearningStats();
                LoadStyleProfileStatus();
                LoadStyleProfileProgress();
                LoadVocabularyProfileStatus();
                UpdateProfileMessaging();
                UpdateExperienceSummary();
                MessageBox.Show("Your AI profile has been reset.", "Reset Complete",
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
    private void NavigateSection_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string section })
            ShowSection(section);
    }

    private void PersonalizationTeaser_Click(object sender, RoutedEventArgs e)
    {
        NavPersonalizationButton.IsChecked = true;
        ShowSection("Personalization");
    }

    private void OpenAiProfile_Click(object sender, RoutedEventArgs e) => PersonalizationTeaser_Click(sender, e);

    private void ActivateLearning_Click(object sender, RoutedEventArgs e)
    {
        if (LearningEnabledCheck.IsChecked == true)
            return;

        _loading = true;
        try
        {
            LearningEnabledCheck.IsChecked = true;
        }
        finally
        {
            _loading = false;
        }

        SaveSettings();
        RefreshUsageState();
    }

    private void ActivatePersonalizedAi_Click(object sender, RoutedEventArgs e) => ActivateLearning_Click(sender, e);

    public void RefreshUsageState()
    {
        _learningService?.Refresh();
        LoadLearningStats();
        LoadStyleProfileStatus();
        LoadStyleProfileProgress();
        LoadVocabularyProfileStatus();
        UpdateProfileMessaging();
        UpdateExperienceSummary();
        UpdatePreview();
        UpdatePrivacyInspector();
    }

    private void OpenAppControl_Click(object sender, RoutedEventArgs e)
    {
        NavAppControlButton.IsChecked = true;
        ShowSection("AppControl");
    }

    private void ApplyAppPreset(string presetId)
    {
        _loading = true;
        try
        {
            PerAppSettings.ApplyPreset(_config, presetId);
            SelectComboItemByTag(AppFilteringModeCombo, _config.AppFilteringMode);
            BlockedAppsBox.Text = PerAppSettings.FormatProcessList(_config.BlockedProcesses);
            AllowedAppsBox.Text = PerAppSettings.FormatProcessList(_config.AllowedProcesses);
        }
        finally
        {
            _loading = false;
        }

        SaveSettings();
    }

    private void AppPresetEverywhere_Click(object sender, RoutedEventArgs e) =>
        ApplyAppPreset(PerAppSettings.PresetEverywhereExceptBlocked);

    private void AppPresetChatOnly_Click(object sender, RoutedEventArgs e) =>
        ApplyAppPreset(PerAppSettings.PresetChatAndEmailOnly);

    private void AppPresetWritingOnly_Click(object sender, RoutedEventArgs e) =>
        ApplyAppPreset(PerAppSettings.PresetWritingAppsOnly);

    private void AppPresetManualAllowList_Click(object sender, RoutedEventArgs e) =>
        ApplyAppPreset(PerAppSettings.PresetManualAllowList);

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
        UpdateFeatureCards();
        LoadStyleProfileProgress();
    }

    private void AppFilteringModeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateAppFilteringUi();
        SaveSettings();
    }

    private void AddCurrentBlockedApp_Click(object sender, RoutedEventArgs e)
    {
        AddLastKnownAppToLists(BlockedAppsBox, AllowedAppsBox);
    }

    private void AddCurrentAllowedApp_Click(object sender, RoutedEventArgs e)
    {
        AddLastKnownAppToLists(AllowedAppsBox, BlockedAppsBox);
    }

    private void ClearBlockedApps_Click(object sender, RoutedEventArgs e)
    {
        BlockedAppsBox.Text = "";
        SaveSettings();
    }

    private void ClearAllowedApps_Click(object sender, RoutedEventArgs e)
    {
        AllowedAppsBox.Text = "";
        SaveSettings();
    }

    private void AddLastKnownAppToLists(TextBox primaryBox, TextBox otherBox)
    {
        var choice = GetLastKnownApp();
        if (choice == null)
        {
            MessageBox.Show(
                "Keystroke could not find a recent non-Keystroke app yet. Focus another app first, or use the running-app picker.",
                "Recent App Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        AddAppChoiceToLists(choice, primaryBox, otherBox);
    }

    private void AddLastAppToBlocked_Click(object sender, RoutedEventArgs e)
    {
        AddLastKnownAppToLists(BlockedAppsBox, AllowedAppsBox);
    }

    private void AddLastAppToAllowed_Click(object sender, RoutedEventArgs e)
    {
        AddLastKnownAppToLists(AllowedAppsBox, BlockedAppsBox);
    }

    private void AddSelectedAppToBlocked_Click(object sender, RoutedEventArgs e)
    {
        AddSelectedAppToLists(BlockedAppsBox, AllowedAppsBox);
    }

    private void AddSelectedAppToAllowed_Click(object sender, RoutedEventArgs e)
    {
        AddSelectedAppToLists(AllowedAppsBox, BlockedAppsBox);
    }

    private void RefreshAppPicker_Click(object sender, RoutedEventArgs e)
    {
        RefreshAppPickerOptions();
    }

    private void AddSelectedAppToLists(TextBox primaryBox, TextBox otherBox)
    {
        if (RunningAppsCombo.SelectedItem is not AppChoice choice)
        {
            MessageBox.Show(
                "Pick an app from the running-app list first.",
                "No App Selected",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        AddAppChoiceToLists(choice, primaryBox, otherBox);
    }

    private void AddAppChoiceToLists(AppChoice choice, TextBox primaryBox, TextBox otherBox)
    {
        var normalized = PerAppSettings.NormalizeProcessName(choice.ProcessName);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var primary = PerAppSettings.ParseProcessList(primaryBox.Text);
        if (!primary.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            primary.Add(normalized);

        var other = PerAppSettings.ParseProcessList(otherBox.Text)
            .Where(p => !string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));

        primaryBox.Text = PerAppSettings.FormatProcessList(primary);
        otherBox.Text = PerAppSettings.FormatProcessList(other);
        SaveSettings();
    }

    private void RefreshAppPickerOptions()
    {
        RememberLastExternalApp(_appPicker());
        UpdateLastExternalAppUi();

        var visibleApps = AppContextService.GetVisibleApps("KeystrokeApp")
            .Select(app => new AppChoice
            {
                ProcessName = app.ProcessName,
                WindowTitle = app.WindowTitle
            })
            .OrderBy(app => app.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.CategoryLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_lastExternalApp != null &&
            !visibleApps.Any(app => string.Equals(
                PerAppSettings.NormalizeProcessName(app.ProcessName),
                PerAppSettings.NormalizeProcessName(_lastExternalApp.ProcessName),
                StringComparison.OrdinalIgnoreCase)))
        {
            visibleApps.Insert(0, _lastExternalApp);
        }

        RunningAppsCombo.Items.Clear();
        foreach (var app in visibleApps)
            RunningAppsCombo.Items.Add(app);

        if (RunningAppsCombo.Items.Count > 0)
            RunningAppsCombo.SelectedIndex = 0;

        UpdateSelectedAppDetailUi();
        UpdatePrivacyInspector();
    }

    private void RememberLastExternalApp((string ProcessName, string WindowTitle) app)
    {
        var normalized = PerAppSettings.NormalizeProcessName(app.ProcessName);
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(normalized, "keystrokeapp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastExternalApp = new AppChoice
        {
            ProcessName = app.ProcessName,
            WindowTitle = app.WindowTitle
        };
    }

    private AppChoice? GetLastKnownApp()
    {
        RememberLastExternalApp(_appPicker());
        return _lastExternalApp;
    }

    private void UpdateLastExternalAppUi()
    {
        if (_lastExternalApp == null)
        {
            LastExternalAppText.Text = "No recent app captured yet.";
            LastExternalAppMetaText.Text = "";
            LastExternalAppHintText.Text = "Focus another app before opening Settings, or use the running-app picker on the right.";
            return;
        }

        LastExternalAppText.Text = _lastExternalApp.DisplayLabel;
        LastExternalAppMetaText.Text = _lastExternalApp.DetailLabel;
        LastExternalAppHintText.Text = "This is the last non-Keystroke app Keystroke remembers seeing.";
    }

    private void RunningAppsCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedAppDetailUi();
    }

    private void UpdateSelectedAppDetailUi()
    {
        if (RunningAppsCombo.SelectedItem is not AppChoice choice)
        {
            RunningAppsSelectionDetailText.Text = "Select an app to see more detail here.";
            return;
        }

        var normalized = PerAppSettings.NormalizeProcessName(choice.ProcessName);
        RunningAppsSelectionDetailText.Text = string.IsNullOrWhiteSpace(choice.ShortWindowTitle)
            ? $"{choice.DisplayLabel} · process {normalized}"
            : $"{choice.DetailLabel} · process {normalized}";
    }

    private void UpdatePrivacyInspector()
    {
        if (_promptPreviewProvider == null ||
            PromptProviderStatusText == null ||
            PromptPreviewText == null)
        {
            return;
        }

        var snapshot = _promptPreviewProvider();
        PromptProviderStatusText.Text = snapshot.ProviderLabel;
        PromptAppStatusText.Text = snapshot.AppAvailabilityLabel;
        PromptAppReasonText.Text = $"{snapshot.ActiveAppLabel} · {snapshot.AppAvailabilityReason}";
        PromptFilteringStatusText.Text = snapshot.AppFilteringModeLabel;
        PromptTypedStatusText.Text = snapshot.TypedInputStatus;
        PromptWillSendText.Text = snapshot.WouldSendPrediction
            ? "Prediction would be sent if the debounce fires now."
            : "Prediction would stay local right now.";
        PromptTypedPreviewText.Text = snapshot.TypedTextPreview;
        PromptScreenPreviewText.Text = snapshot.ScreenContextPreview;
        PromptRollingPreviewText.Text = snapshot.RollingContextPreview;
        PromptLearningPreviewText.Text = snapshot.LearningHintsPreview;
        var profile = GetProfileSummary();
        PromptLearningStatusText.Text = snapshot.LearningHintsIncluded
            ? "AI profile hints are included in this request."
            : profile.PersonalizedAiEnabled
                ? "Personalized AI is on, but no profile hints are ready for this context yet."
                : profile.HasSignals
                    ? "Profile signals exist, but Personalized AI is off so they stay local."
                    : "No AI profile hints yet.";
        PromptPreviewText.Text = snapshot.UserPromptPreview;
    }

    private static string GetFriendlyProcessName(string processName)
    {
        var normalized = PerAppSettings.NormalizeProcessName(processName);
        if (string.IsNullOrWhiteSpace(normalized))
            return "Unknown app";

        return normalized switch
        {
            "code" => "VS Code",
            "devenv" => "Visual Studio",
            "msedge" => "Microsoft Edge",
            "pwsh" => "PowerShell",
            "windowsterminal" => "Windows Terminal",
            "notepad++" => "Notepad++",
            "winword" => "Microsoft Word",
            "olk" => "Outlook",
            "idea64" => "IntelliJ IDEA",
            "idea" => "IntelliJ IDEA",
            "pycharm" => "PyCharm",
            "webstorm" => "WebStorm",
            "goland" => "GoLand",
            "clion" => "CLion",
            "sublime_text" => "Sublime Text",
            _ => HumanizeProcessName(normalized)
        };
    }

    private static string HumanizeProcessName(string normalized)
    {
        var parts = normalized
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return normalized;

        return string.Join(" ", parts.Select(part =>
            part.Length <= 3 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string GetCategoryDisplay(AppCategory.Category category) => category switch
    {
        AppCategory.Category.Chat => "Chat",
        AppCategory.Category.Email => "Email",
        AppCategory.Category.Code => "Code",
        AppCategory.Category.Document => "Document",
        AppCategory.Category.Browser => "Browser",
        AppCategory.Category.Terminal => "Terminal",
        _ => ""
    };

    private static string TruncateWindowTitle(string windowTitle)
    {
        var trimmed = windowTitle?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(trimmed))
            return "";

        const int maxLength = 72;
        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..(maxLength - 1)] + "...";
    }

    private string GetSelectedAppFilteringMode() =>
        (AppFilteringModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
        ?? PerAppSettings.AllowAllExceptBlocked;

    private static void SelectComboItemByTag(ComboBox comboBox, string? tag)
    {
        var desired = tag ?? "";
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), desired, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
            comboBox.SelectedIndex = 0;
    }

    /// <summary>
    /// Check if Ollama is running and the selected model is available.
    /// </summary>
    private async Task CheckOllamaStatusAsync()
    {
        try
        {
            var endpoint = OllamaEndpointBox.Text.Trim().TrimEnd('/');
            var model = (OllamaModelCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? AppConfig.DefaultOllamaModel;

            var response = await OllamaStatusClient.GetAsync($"{endpoint}/api/tags");

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
        UpdateExperienceSummary();
        UpdatePreview();
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
