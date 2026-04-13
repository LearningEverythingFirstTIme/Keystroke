using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using KeystrokeApp.Services;
using KeystrokeApp.Views;
using WindowsInput;

namespace KeystrokeApp;

/// <summary>
/// Main application entry point — manages startup, shutdown, prediction engine
/// lifecycle, and the settings/debug windows.
/// Keyboard handling: App.KeyboardHandlers.cs
/// Prediction pipeline: App.Prediction.cs
/// Tray icon + menu: App.TrayIcon.cs
/// </summary>
public partial class App : Application
{
    // ── Service instances ─────────────────────────────────────────────────────
    private InputListenerService?     _inputListener;
    private TaskbarIcon?              _trayIcon;
    private SuggestionPanel?         _suggestionPanel;
    private SettingsWindow?          _settingsWindow;
    private OnboardingWindow?        _onboardingWindow;
    private IPredictionEngine?       _predictionEngine;
    private ScreenReaderService?              _ocrService;
    private Timer?                   _ocrTimer;

    // ── App state ─────────────────────────────────────────────────────────────
    // Thread-safety notes:
    //   UI-only:        _config, _typingBuffer, _debounceTimer, _fastDebounceTimer,
    //                   _suggestionPanel, _settingsWindow, _trayIcon, tray menu items
    //   Self-locking:   _predictionCache (internal lock; safe from any thread)
    //   Interlocked:    _sessionAcceptCount, _suggestionShownAtTicks, _cycleDepth,
    //                   _activePredictionRequestId, _predictionRequestCounter
    //   volatile:       _isEnabled (written on UI/Input, read on Background/Timer)
    //                   _predictionState (written on Background, read on UI for debug)
    //   Lock-protected: _predictionCts, _lastPredictionPrefix (via _predictionCtsLock)
    private AppConfig                _config           = new();
    private TypingBuffer             _typingBuffer     = new();
    private DebounceTimer?           _debounceTimer;
    private DebounceTimer?           _fastDebounceTimer;
    private PredictionCache          _predictionCache  = new(50);
    private readonly LearningContextPreferencesService _contextPreferencesService = new();
    private readonly LearningContextMaintenanceService _contextMaintenanceService;
    private CompletionFeedbackService        _acceptanceTracker;
    private readonly UsageCounters _usageCounters = new();
    private RollingContextService    _rollingContext   = new(maxChars: 2000, timeoutMinutes: 5);
    private AcceptanceLearningService  _learningService;
    private readonly ContextFingerprintService _contextFingerprintService = new();
    private readonly LearningEventService _learningEventService;
    private readonly LearningCaptureCoordinator _learningCaptureCoordinator;
    private readonly OutboundPrivacyService _outboundPrivacy = new();
    private StyleProfileService        _styleProfileService       = new();
    private VocabularyProfileService   _vocabularyProfileService  = new();
    private CorrectionPatternService   _correctionPatternService  = new();
    private ContextAdaptiveSettingsService _contextAdaptiveSettingsService = new();
    private LearningScoreService       _learningScoreService      = new();
    private AnalyticsAggregationService _analyticsService          = new();
    private volatile bool            _isEnabled        = true;
    private int                      _sessionAcceptCount;  // Access via Interlocked only

    // ── Tray icon state (used by App.TrayIcon.cs) ─────────────────────────────
    private Icon?     _iconEnabled;
    private Icon?     _iconDisabled;
    private Timer?    _suspendTimer;
    private MenuItem? _enabledMenuItem;
    private MenuItem? _engineMenuItem;
    private MenuItem? _sessionMenuItem;
    private MenuItem? _profileMenuItem;
    private MenuItem? _setupMenuItem;
    private MenuItem? _currentAppMenuItem;
    private MenuItem? _currentAppStatusMenuItem;
    private MenuItem? _currentAppBlockMenuItem;
    private MenuItem? _currentAppAllowMenuItem;

    // ── Prediction state (used by App.Prediction.cs) ─────────────────────────
    // _predictionCts and _lastPredictionPrefix are guarded by _predictionCtsLock.
    // Always acquire the lock before reading or writing either field.
    private readonly object          _predictionCtsLock    = new();
    private CancellationTokenSource? _predictionCts;
    private string                   _lastPredictionPrefix = "";

    // ── Sub-Phase A: interaction signal capture ───────────────────────────────
    // Written on the UI thread (streaming complete / cache hit path in App.Prediction.cs);
    // read on the input-listener thread (Tab accept in App.KeyboardHandlers.cs).
    // Use Interlocked so no explicit locking is needed across threads.
    //   _suggestionShownAtTicks  — DateTime.UtcNow.Ticks when the full suggestion became visible;
    //                              0 means no suggestion is currently shown.
    //   _cycleDepth              — count of Ctrl+Up/Down presses since the current suggestion appeared;
    //                              reset to 0 each time a new suggestion arrives.
    private readonly CorrectionDetector _postEditDetector = new();
    private readonly ReliabilityTraceService _reliabilityTrace = new();
    private readonly OnboardingStateService _onboardingStateService = new();
    private readonly GeminiApiKeyValidationService _geminiApiKeyValidationService = new();
    private readonly ITextInjector _textInjector;
    private readonly SuggestionLifecycleController _suggestionLifecycle = new();
    private readonly SemaphoreSlim _acceptanceGate = new(1, 1);
    private long _predictionRequestCounter;
    private long _activePredictionRequestId;
    private long _lastBufferTraceTicks;
    private int _lastTracedBufferLength;
    private volatile string _predictionState = "Idle";
    private string _lastSuppressedProcessName = "";
    private string _lastExternalProcessName = "";
    private string _lastExternalWindowTitle = "";
    private string _lastAcceptanceStatus = "Ready";
    private bool _runtimeActivated;
    private bool _isSetupIncomplete;
    private bool _sessionFreeLimitWarningShown;
    private bool _sessionDailyLimitReachedShown;
    private bool _isProTier;
    private string _setupIncompleteReason = "Finish onboarding to start completions.";

    private string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Keystroke",
        "debug.log"
    );

    public App()
    {
        _acceptanceTracker = new CompletionFeedbackService(
            preferences: _contextPreferencesService,
            fingerprints: _contextFingerprintService);
        _learningEventService = new LearningEventService(
            preferences: _contextPreferencesService);
        _contextMaintenanceService = new LearningContextMaintenanceService(
            fingerprints: _contextFingerprintService,
            eventWriteLock: _learningEventService.WriteLock,
            legacyWriteLock: _acceptanceTracker.WriteLock);
        _learningService = new AcceptanceLearningService(
            new LearningRepository(_contextFingerprintService, _contextPreferencesService),
            new LearningRetrievalService(new LearningReranker()),
            _contextPreferencesService);
        _textInjector = new ClipboardTextInjector(new InputSimulator(), _reliabilityTrace);
        _learningCaptureCoordinator = new LearningCaptureCoordinator(_learningEventService);
        _reliabilityTrace.EventRecorded += evt => LogToDebug(
            $"[reliability:{evt.Area}] {evt.EventName} - {evt.Message}");
    }

    // ==================== Startup / Shutdown ====================

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handlers to prevent silent crashes
        DispatcherUnhandledException += (_, args) =>
        {
            Log($"UNHANDLED UI EXCEPTION: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log($"UNHANDLED DOMAIN EXCEPTION: {ex}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log($"UNOBSERVED TASK EXCEPTION: {args.Exception}");
            args.SetObserved();
        };

        // Ensure directories and config exist
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        AppConfig.EnsureExists();

        try
        {
            // Load config
            _config = AppConfig.Load();
            _usageCounters.GetSnapshot();
            Log($"Config loaded: Engine={_config.PredictionEngine}, Debounce={_config.DebounceMs}ms");
            RefreshLicenseStatus();
            _reliabilityTrace.Trace("startup", "config_loaded", "Loaded app configuration.", new Dictionary<string, string>
            {
                ["engine"] = _config.PredictionEngine,
                ["ocrEnabled"] = _config.OcrEnabled.ToString(),
                ["licenseTier"] = _isProTier ? "Pro" : "Free"
            });

            // Prune completions file if it's grown too large
            _acceptanceTracker.PruneIfNeeded(maxLines: 2000);
            _learningEventService.PruneIfNeeded(maxLines: 4000);

            // Sub-Phase D: wire LearningScoreService to the learning stack.
            // It holds references to all three services so Recompute() can pull
            // a fresh snapshot from each without coupling them to each other.
            _learningScoreService.LearningService         = _learningService;
            _learningScoreService.StyleProfileService     = _styleProfileService;
            _learningScoreService.VocabularyProfileService = _vocabularyProfileService;

            // Recompute scores after each style profile generation.
            // ProfileUpdated fires on a background thread — marshal to UI for balloon.
            _styleProfileService.ProfileUpdated += () =>
            {
                var scores = _learningScoreService.Recompute();
                Log($"Scores recomputed after profile update: {scores.Categories.Count} categories");
            };

            // Show a tray balloon when the model detects sustained drift in a category.
            _learningScoreService.DriftDetected += (category, oldScore, newScore) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _trayIcon?.ShowBalloonTip(
                        "Writing Pattern Shift Detected",
                        $"Your {category} predictions have become less accurate (score {oldScore}→{newScore}). " +
                        "Accept a few suggestions to help Keystroke recalibrate to your current style.",
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                    Log($"Drift balloon shown: {category} {oldScore}→{newScore}");
                });
            };

            // Feed score snapshots into the analytics store for extended history.
            _learningScoreService.ScoreComputed += (category, score) =>
            {
                _analyticsService.RecordScoreSnapshot(category, score);
            };

            if (_isProTier && _config.StyleProfileEnabled)
            {
                _styleProfileService.Start(_config.StyleProfileInterval);
                _vocabularyProfileService.Start(_config.StyleProfileInterval);
                _correctionPatternService.Start(Math.Max(3, _config.StyleProfileInterval / 6));
                _contextAdaptiveSettingsService.Start(Math.Max(5, _config.StyleProfileInterval / 3));
            }

            // Prune log files to prevent unbounded growth (keep last ~5000 lines each).
            // Run on a background thread so file I/O doesn't block the UI thread at startup.
            _ = Task.Run(PruneLogFiles);

            _typingBuffer.BufferChanged += OnBufferChanged;
            _typingBuffer.BufferCleared += OnBufferCleared;

            // Create system tray icon
            _isEnabled = false;
            CreateTrayIcon();
            Log("Tray icon created.");

            if (_onboardingStateService.TryCompleteOnboardingFromExistingSetup(_config))
            {
                _config.Save();
                Log("Existing valid provider setup detected. Marked onboarding complete.");
            }

            RefreshShellStatus();

            if (_config.LegacyTierMigrated)
            {
                _trayIcon?.ShowBalloonTip(
                    "Keystroke now uses license keys",
                    "Enter your license key in Settings to restore Personalized AI and unlimited completions.",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                Log("Legacy tier migration notification shown.");
            }

            if (ShouldRunOnboarding())
            {
                var continueApp = RunOnboardingFlow(isStartup: true);
                if (!continueApp)
                {
                    Log("User exited during onboarding. Exiting.");
                    Shutdown();
                    return;
                }
            }

            EnsureRuntimeStateFromConfig();

            Log("App ready. Right-click tray icon for options.");
            _reliabilityTrace.Trace("startup", "ready", "App startup completed successfully.");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex}");
            _reliabilityTrace.Trace("startup", "failed", "App startup failed.", new Dictionary<string, string>
            {
                ["error"] = ex.Message
            });
            MessageBox.Show($"Startup error: {ex.Message}", "Keystroke Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("App exiting...");
        DeactivateRuntime();
        _postEditDetector.Dispose();
        _trayIcon?.Dispose();
        _suspendTimer?.Dispose();
        _geminiApiKeyValidationService.Dispose();
        base.OnExit(e);
    }

    private bool ShouldRunOnboarding()
    {
        if (!_config.ConsentAccepted)
            return true;

        if (_config.OnboardingCompleted)
            return false;

        return !_onboardingStateService.HasUsableProviderSetup(_config);
    }

    private bool RunOnboardingFlow(bool isStartup)
    {
        _onboardingStateService.ApplyRecommendedGeminiDefaults(_config);

        _onboardingWindow = new OnboardingWindow(_config, _geminiApiKeyValidationService);
        _onboardingWindow.ShowDialog();
        var result = _onboardingWindow.Result;
        _onboardingWindow = null;

        var dialogWasDismissed = !result.ExitApplication &&
            !result.StartPaused &&
            !result.OpenSettingsRequested &&
            !result.OnboardingCompleted &&
            !result.ConsentAccepted;
        if (dialogWasDismissed)
            return !isStartup || _config.ConsentAccepted;

        if (result.ExitApplication)
            return false;

        if (result.ConsentAccepted)
            _config.ConsentAccepted = true;

        if (!string.IsNullOrWhiteSpace(result.GeminiApiKey))
            _config.GeminiApiKey = result.GeminiApiKey.Trim();

        _config.PredictionEngine = "gemini";
        _config.GeminiModel = AppConfig.DefaultGeminiModel;
        _config.OcrEnabled = true;
        _config.RollingContextEnabled = true;
        _config.LearningEnabled = false;
        _config.OnboardingCompleted = result.OnboardingCompleted;
        _config.Save();

        if (result.OpenSettingsRequested)
            Dispatcher.BeginInvoke(ShowSettingsWindow);

        return true;
    }

    private void EnsureRuntimeStateFromConfig()
    {
        if (_onboardingStateService.TryCompleteOnboardingFromExistingSetup(_config))
            _config.Save();

        if (_onboardingStateService.CanActivateRuntime(_config))
        {
            ActivateRuntimeFromConfig();
            return;
        }

        EnterSetupIncompleteState(_onboardingStateService.GetSetupIncompleteReason(_config));
    }

    private void ActivateRuntimeFromConfig()
    {
        if (!_onboardingStateService.CanActivateRuntime(_config))
        {
            EnterSetupIncompleteState(_onboardingStateService.GetSetupIncompleteReason(_config));
            return;
        }

        DeactivateRuntime();

        _predictionEngine = CreatePredictionEngine();
        _styleProfileService.Engine = _predictionEngine;
        Log($"Prediction engine: {_predictionEngine?.GetType().Name ?? "none"}");

        _debounceTimer = new DebounceTimer(_config.DebounceMs);
        _debounceTimer.DebounceComplete += OnDebounceComplete;
        _fastDebounceTimer = new DebounceTimer(_config.FastDebounceMs);
        _fastDebounceTimer.DebounceComplete += OnDebounceComplete;

        _inputListener = new InputListenerService();
        _inputListener.CharacterTyped += OnCharacterTyped;
        _inputListener.SpecialKeyPressed += OnSpecialKeyPressed;
        _inputListener.InputDiagnostic += msg => LogToDebug(msg);
        _inputListener.Start();
        Log("Input listener started.");

        _suggestionPanel = new SuggestionPanel();
        _suggestionPanel.ApplyTheme(ThemeDefinitions.Get(_config.ThemeId));
        Log("Suggestion panel created.");

        if (_config.OcrEnabled)
        {
            _ocrService = new ScreenReaderService();
            _ocrTimer = new Timer(OnOcrTimerTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            Log("OCR service initialized.");
        }
        else
        {
            Log("OCR disabled in settings.");
        }

        _runtimeActivated = true;
        _isSetupIncomplete = false;
        _setupIncompleteReason = "";
        _isEnabled = true;
        _lastAcceptanceStatus = "Ready";

        _styleProfileService.CancelGeneration();
        _styleProfileService.UpdateInterval(_config.StyleProfileInterval);
        _vocabularyProfileService.CancelGeneration();
        _vocabularyProfileService.UpdateInterval(_config.StyleProfileInterval);
        _correctionPatternService.CancelGeneration();
        _correctionPatternService.UpdateInterval(Math.Max(3, _config.StyleProfileInterval / 6));
        _contextAdaptiveSettingsService.CancelGeneration();
        _contextAdaptiveSettingsService.UpdateInterval(Math.Max(5, _config.StyleProfileInterval / 3));

        RefreshLicenseStatus();
        if (_isProTier && _config.StyleProfileEnabled)
        {
            _styleProfileService.Start(_config.StyleProfileInterval);
            _vocabularyProfileService.Start(_config.StyleProfileInterval);
            _correctionPatternService.Start(Math.Max(3, _config.StyleProfileInterval / 6));
            _contextAdaptiveSettingsService.Start(Math.Max(5, _config.StyleProfileInterval / 3));
        }

        RefreshShellStatus();
    }

    private void DeactivateRuntime()
    {
        CancelPendingPrediction();
        _fastDebounceTimer?.Cancel();
        _debounceTimer?.Cancel();
        _suggestionPanel?.HideSuggestion();
        ClearActiveSuggestion();

        if (!_typingBuffer.IsEmpty)
            _typingBuffer.Clear();

        _ocrTimer?.Dispose();
        _ocrTimer = null;
        _ocrService?.Dispose();
        _ocrService = null;

        _inputListener?.Dispose();
        _inputListener = null;

        (_predictionEngine as IDisposable)?.Dispose();
        _predictionEngine = null;
        _styleProfileService.Engine = null;

        _suggestionPanel?.Close();
        _suggestionPanel = null;

        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _fastDebounceTimer?.Dispose();
        _fastDebounceTimer = null;

        _runtimeActivated = false;
    }

    private void EnterSetupIncompleteState(string reason)
    {
        DeactivateRuntime();
        _isEnabled = false;
        _isSetupIncomplete = true;
        _setupIncompleteReason = reason;
        _lastAcceptanceStatus = "Setup incomplete";
        RefreshShellStatus();
    }

    private void RefreshShellStatus()
    {
        if (_trayIcon != null)
        {
            _trayIcon.Icon = GetTrayIcon(_isEnabled && !_isSetupIncomplete);
            _trayIcon.ToolTipText = BuildToolTip();
        }

        if (_enabledMenuItem != null)
        {
            _enabledMenuItem.IsChecked = _isEnabled && !_isSetupIncomplete;
            _enabledMenuItem.IsEnabled = !_isSetupIncomplete;
        }

        if (_engineMenuItem != null)
            _engineMenuItem.Header = $"Engine: {_config.PredictionEngine} ({GetCurrentModelName()})";
        if (_sessionMenuItem != null)
            _sessionMenuItem.Header = BuildSessionMenuHeader();
        if (_profileMenuItem != null)
            _profileMenuItem.Header = BuildProfileMenuHeader();

        UpdateTrayCurrentAppActions();
    }

    // ==================== Engine creation ====================

    private IPredictionEngine CreatePredictionEngine()
    {
        IPredictionEngine engine = _config.PredictionEngine.ToLower() switch
        {
            "gemini" when !string.IsNullOrWhiteSpace(_config.GeminiApiKey)
                => new GeminiPredictionEngine(_config.GeminiApiKey, _config.GeminiModel),
            "gemini" => new DummyPredictionEngine(),
            "gpt5" when !string.IsNullOrWhiteSpace(_config.OpenAiApiKey)
                => new Gpt5PredictionEngine(_config.OpenAiApiKey, _config.Gpt5Model),
            "gpt5" => new DummyPredictionEngine(),
            "claude" when !string.IsNullOrWhiteSpace(_config.AnthropicApiKey)
                => new ClaudePredictionEngine(_config.AnthropicApiKey, _config.ClaudeModel),
            "claude" => new DummyPredictionEngine(),
            "ollama"
                => new OllamaPredictionEngine(_config.OllamaModel, _config.OllamaEndpoint),
            "openrouter" when !string.IsNullOrWhiteSpace(_config.OpenRouterApiKey)
                => new OpenRouterPredictionEngine(_config.OpenRouterApiKey, _config.OpenRouterModel),
            "openrouter" => new DummyPredictionEngine(),
            _ => new DummyPredictionEngine()
        };

        // Apply common configuration to all real engines
        if (engine is PredictionEngineBase baseEngine)
        {
            baseEngine.SystemPrompt      = _config.EffectiveSystemPrompt;
            baseEngine.LengthInstruction = _config.CompletionLengthInstruction;
            baseEngine.Temperature       = _config.Temperature;
            baseEngine.MaxOutputTokens   = _config.PresetMaxOutputTokens;
            baseEngine.LearningService          = _isProTier ? _learningService : null;
            baseEngine.StyleProfileService      = _isProTier && _config.StyleProfileEnabled ? _styleProfileService : null;
            baseEngine.VocabularyProfileService = _isProTier && _config.StyleProfileEnabled ? _vocabularyProfileService : null;
            baseEngine.CorrectionPatternService = _isProTier && _config.StyleProfileEnabled ? _correctionPatternService : null;
            baseEngine.ContextAdaptiveSettingsService = _isProTier && _config.StyleProfileEnabled ? _contextAdaptiveSettingsService : null;
        }

        // Ollama uses a fixed low temperature for local models
        if (engine is OllamaPredictionEngine ollama)
            ollama.Temperature = 0.1;

        return engine;
    }

    // ==================== Window management ====================

    private void ShowSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(
                _config,
                _learningService,
                _styleProfileService,
                _vocabularyProfileService,
                _learningScoreService,
                _usageCounters,
                _contextPreferencesService,
                _contextMaintenanceService,
                GetLastExternalWindowOrCurrent,
                CreatePromptPreviewSnapshot,
                _correctionPatternService,
                _contextAdaptiveSettingsService,
                _analyticsService);
            _settingsWindow.ThemeChanged += themeId =>
                _suggestionPanel?.ApplyTheme(ThemeDefinitions.Get(themeId));
            _settingsWindow.Closed += (s, e) =>
            {
                _settingsWindow = null;
                SyncSettingsFromConfig();
            };
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Called when the settings window closes. Re-reads config and updates runtime state.
    /// </summary>
    private void SyncSettingsFromConfig()
    {
        _config = AppConfig.Load();
        Log("Settings updated from config.");

        if (_onboardingStateService.TryCompleteOnboardingFromExistingSetup(_config))
            _config.Save();

        EnsureRuntimeStateFromConfig();

        if (_runtimeActivated)
            RefreshPerAppAvailability();
    }

    // ==================== Helpers ====================

    private ContextSnapshot CreateContextSnapshot(string typedText, string processName, string windowTitle)
    {
        var screenText = _outboundPrivacy.SanitizeForPrompt(_ocrService?.CachedText);
        var rollingContext = _config.RollingContextEnabled
            ? _outboundPrivacy.SanitizeForPrompt(_rollingContext.GetContext(processName, windowTitle))
            : null;
        var fingerprint = _contextFingerprintService.Create(processName, windowTitle, screenText, rollingContext);

        return new ContextSnapshot
        {
            TypedText = typedText,
            ProcessName = processName,
            WindowTitle = windowTitle,
            SafeContextLabel = fingerprint.SafeContextLabel,
            Category = fingerprint.Category,
            ProcessKey = fingerprint.ProcessKey,
            WindowKey = fingerprint.WindowKey,
            SubcontextKey = fingerprint.SubcontextKey,
            ProcessLabel = fingerprint.ProcessLabel,
            WindowLabel = fingerprint.WindowLabel,
            SubcontextLabel = fingerprint.SubcontextLabel,
            ContextConfidence = fingerprint.Confidence,
            ScreenText = screenText,
            RollingContext = rollingContext
        };
    }

    private bool IsProcessEnabled(string processName) => PerAppSettings.IsEnabled(_config, processName);

    private void RememberExternalWindow(string processName, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return;

        if (string.Equals(processName, "KeystrokeApp", StringComparison.OrdinalIgnoreCase))
            return;

        _lastExternalProcessName = processName;
        _lastExternalWindowTitle = windowTitle;
    }

    private (string ProcessName, string WindowTitle) GetLastExternalWindowOrCurrent()
    {
        var activeWindow = AppContextService.GetActiveWindow();
        RememberExternalWindow(activeWindow.ProcessName, activeWindow.WindowTitle);

        if (!string.IsNullOrWhiteSpace(activeWindow.ProcessName) &&
            !string.Equals(activeWindow.ProcessName, "KeystrokeApp", StringComparison.OrdinalIgnoreCase))
        {
            return activeWindow;
        }

        if (!string.IsNullOrWhiteSpace(_lastExternalProcessName))
            return (_lastExternalProcessName, _lastExternalWindowTitle);

        return activeWindow;
    }

    private bool TryGetEligibleActiveWindow(out string processName, out string windowTitle)
    {
        var activeWindow = AppContextService.GetActiveWindow();
        processName = activeWindow.ProcessName;
        windowTitle = activeWindow.WindowTitle;
        RememberExternalWindow(processName, windowTitle);

        if (IsProcessEnabled(processName))
        {
            _lastSuppressedProcessName = "";
            return true;
        }

        SuppressForFilteredApp(processName);
        return false;
    }

    private void SuppressForFilteredApp(string processName)
    {
        _fastDebounceTimer?.Cancel();
        _debounceTimer?.Cancel();
        CancelPendingPrediction();
        _suggestionPanel?.HideSuggestion();
        ClearActiveSuggestion();

        if (!_typingBuffer.IsEmpty)
            _typingBuffer.Clear();

        var normalizedProcess = PerAppSettings.NormalizeProcessName(processName);
        if (!string.Equals(_lastSuppressedProcessName, normalizedProcess, StringComparison.Ordinal))
        {
            var reason = PerAppSettings.NormalizeMode(_config.AppFilteringMode) == PerAppSettings.AllowListedOnly
                ? "not in allow list"
                : "blocked";
            LogToDebug($"Per-app filter suppressed suggestions in {normalizedProcess} ({reason}).");
            _lastSuppressedProcessName = normalizedProcess;
        }
    }

    private void RefreshPerAppAvailability()
    {
        var (processName, _) = AppContextService.GetActiveWindow();
        if (IsProcessEnabled(processName))
        {
            _lastSuppressedProcessName = "";
            return;
        }

        SuppressForFilteredApp(processName);
    }

    private void RegisterVisibleSuggestion(long requestId, ContextSnapshot context, string prefix, string completion)
        => RegisterVisibleSuggestion(requestId, context, prefix, completion, null);

    private void RegisterVisibleSuggestion(
        long requestId,
        ContextSnapshot context,
        string prefix,
        string completion,
        string? retainedSuggestionId)
    {
        var transition = _suggestionLifecycle.ShowSuggestion(requestId, context, prefix, completion, retainedSuggestionId);
        if (!string.IsNullOrWhiteSpace(transition.ClearedSuggestionId))
            _learningCaptureCoordinator.ClearSuggestion(transition.ClearedSuggestionId);
        _learningCaptureCoordinator.OnSuggestionShown(
            transition.State.SuggestionId,
            requestId,
            context,
            prefix,
            completion);
    }

    private void ClearActiveSuggestion()
    {
        var idToClear = _suggestionLifecycle.ClearSuggestion();
        if (!string.IsNullOrWhiteSpace(idToClear))
            _learningCaptureCoordinator.ClearSuggestion(idToClear);
    }

    /// <summary>
    /// Atomically snapshots the current active suggestion state for use in closures
    /// that may execute after the suggestion has changed.
    /// </summary>
    private (string SuggestionId, long RequestId, ContextSnapshot? Context) SnapshotActiveSuggestion()
    {
        var state = _suggestionLifecycle.Snapshot();
        return (state.SuggestionId, state.SuggestionRequestId, state.Context);
    }

    private PromptPreviewSnapshot CreatePromptPreviewSnapshot()
    {
        var (processName, windowTitle) = GetLastExternalWindowOrCurrent();
        var providerLabel = $"{_config.PredictionEngine} ({GetCurrentModelName()})";
        var rollingContext = _config.RollingContextEnabled
            ? _rollingContext.GetContext(processName, windowTitle)
            : null;

        return PromptPreviewBuilder.Build(
            _config,
            providerLabel,
            _typingBuffer.CurrentText,
            processName,
            windowTitle,
            IsProcessEnabled(processName),
            _outboundPrivacy,
            _isProTier ? _learningService : null,
            _isProTier && _config.StyleProfileEnabled ? _styleProfileService : null,
            _isProTier && _config.StyleProfileEnabled ? _vocabularyProfileService : null,
            _isProTier && _config.StyleProfileEnabled ? _correctionPatternService : null,
            _ocrService?.CachedText,
            rollingContext);
    }

    private bool IsFreeTierLimitActive() => !_isProTier;

    private void RefreshLicenseStatus()
        => _isProTier = LicenseService.IsPro(_config.LicenseKeyEncrypted);

    private bool IsPredictionBlockedByDailyLimit(string buffer)
    {
        var usage = _usageCounters.GetSnapshot();
        if (!IsFreeTierLimitActive() || !usage.IsDailyLimitReached)
            return false;

        ShowDailyLimitReachedBalloonOnce();
        TracePredictionSuppressed("Daily free limit reached", buffer, new Dictionary<string, string>
        {
            ["dailyAccepted"] = usage.DailyAcceptedSuggestions.ToString()
        });
        return true;
    }

    private void RecordAcceptedSuggestionUsage(string suggestionId)
    {
        var result = _usageCounters.RecordAcceptedSuggestion(suggestionId);
        if (!result.Counted)
            return;

        Interlocked.Increment(ref _sessionAcceptCount);
        MaybeShowFreeTierWarning(result.Snapshot);
        MaybeShowDailyLimitReachedBalloon(result.Snapshot);
        MaybeShowLearningNudge(result.Snapshot);
        UpdateTraySessionInfo();
        NotifySettingsWindowUsageChanged();
    }

    private void MaybeShowFreeTierWarning(UsageCountersSnapshot snapshot)
    {
        if (_sessionFreeLimitWarningShown || !IsFreeTierLimitActive())
            return;

        if (snapshot.DailyAcceptedSuggestions < 20 || snapshot.IsDailyLimitReached)
            return;

        _sessionFreeLimitWarningShown = true;
        var remaining = snapshot.RemainingFreeSuggestions;
        _trayIcon?.ShowBalloonTip(
            "Free completions running low",
            $"Heads up — only {remaining} free completions left today",
            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
    }

    private void MaybeShowDailyLimitReachedBalloon(UsageCountersSnapshot snapshot)
    {
        if (!IsFreeTierLimitActive() || !snapshot.IsDailyLimitReached)
            return;

        ShowDailyLimitReachedBalloonOnce();
    }

    private void ShowDailyLimitReachedBalloonOnce()
    {
        if (_sessionDailyLimitReachedShown)
            return;

        _sessionDailyLimitReachedShown = true;
        _trayIcon?.ShowBalloonTip(
            "Free limit reached",
            "Daily limit reached — completions resume at midnight. Enter a license key for unlimited ($20 once).",
            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
    }

    private void MaybeShowLearningNudge(UsageCountersSnapshot snapshot)
    {
        if (_isProTier || snapshot.TotalAcceptedSuggestions < 15)
            return;

        if (!_usageCounters.MarkLearningNudgeShown())
            return;

        _trayIcon?.ShowBalloonTip(
            "Your AI profile is building",
            "15 completions tracked — enter a license key in Settings to unlock Personalized AI ($20 once)",
            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        NotifySettingsWindowUsageChanged();
    }

    private string BuildSessionMenuHeader()
    {
        var usage = _usageCounters.GetSnapshot();
        if (_isProTier)
            return $"Accepted: {_sessionAcceptCount} this session (unlimited)";
        return usage.IsDailyLimitReached
            ? "⚠ Daily limit reached — unlock unlimited"
            : $"Accepted: {_sessionAcceptCount} this session ({usage.DailyAcceptedSuggestions}/{UsageCounters.DailyFreeLimit} today)";
    }

    private string BuildUsageTooltipSummary()
    {
        var usage = _usageCounters.GetSnapshot();
        if (_isProTier)
            return $"{_sessionAcceptCount} this session (unlimited)";
        return usage.IsDailyLimitReached
            ? "⚠ Daily limit reached — unlock unlimited"
            : $"{_sessionAcceptCount} this session ({usage.DailyAcceptedSuggestions}/{UsageCounters.DailyFreeLimit} today)";
    }

    private void NotifySettingsWindowUsageChanged()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            return;

        Dispatcher.BeginInvoke(() => _settingsWindow?.RefreshUsageState());
    }

    private int GetSuggestionLatencyMs()
    {
        var shownTicks = _suggestionLifecycle.Snapshot().ShownAtTicks;
        if (shownTicks == 0)
            return -1;

        return (int)Math.Min(
            (DateTime.UtcNow.Ticks - shownTicks) / TimeSpan.TicksPerMillisecond,
            int.MaxValue);
    }

    private int ConsumeCycleDepth()
    {
        var state = _suggestionLifecycle.Snapshot();
        return state.CycleDepth;
    }

    private static readonly object _logLock = new();
    private void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Prune all log files at startup to prevent unbounded growth.
    /// Keeps the most recent lines of each log file.
    /// </summary>
    private void PruneLogFiles()
    {
        var logDir = Path.GetDirectoryName(_logPath)!;
        string[] logFiles = ["debug.log", "gemini.log", "claude.log", "gpt5.log", "ocr.log", "learning.log", "style-profile.log", "reliability.log"];
        const int maxLines = 5000;

        foreach (var fileName in logFiles)
        {
            try
            {
                var path = Path.Combine(logDir, fileName);
                if (!File.Exists(path)) continue;

                var lines = File.ReadAllLines(path);
                if (lines.Length <= maxLines) continue;

                File.WriteAllLines(path, lines[^maxLines..]);
                Log($"Pruned {fileName}: {lines.Length} → {maxLines} lines");
            }
            catch (Exception) { /* Non-critical: pruning a log file failing is safe to ignore */ }
        }
    }

    // Verbose per-keystroke diagnostics — no-op in production builds.
    // Restore the debug window (ShowDebugWindow + _debugWindow field) to re-enable.
    private void LogToDebug(string message) { }

    private long NextPredictionRequestId() => Interlocked.Increment(ref _predictionRequestCounter);

    private bool IsPredictionRequestCurrent(long requestId)
        => Interlocked.Read(ref _activePredictionRequestId) == requestId;

    private void SetPredictionState(string state, long? requestId = null, IReadOnlyDictionary<string, string>? data = null)
    {
        _predictionState = state;
        _reliabilityTrace.Trace(
            "prediction",
            "state",
            requestId.HasValue ? $"Request {requestId.Value} -> {state}" : $"Prediction state -> {state}",
            data);
    }

    private void TracePredictionSuppressed(string reason, string buffer, IReadOnlyDictionary<string, string>? data = null)
    {
        var payload = new Dictionary<string, string>(data ?? new Dictionary<string, string>())
        {
            ["reason"] = reason,
            ["bufferLength"] = buffer.Length.ToString()
        };
        _reliabilityTrace.Trace("prediction", "suppressed", $"Prediction suppressed: {reason}", payload);
    }
}
