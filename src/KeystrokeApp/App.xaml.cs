using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using KeystrokeApp.Services;
using KeystrokeApp.Views;

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
    private DebugWindow?              _debugWindow;
    private SuggestionPanel?         _suggestionPanel;
    private SettingsWindow?          _settingsWindow;
    private IPredictionEngine?       _predictionEngine;
    private ScreenReaderService?              _ocrService;
    private Timer?                   _ocrTimer;

    // ── App state ─────────────────────────────────────────────────────────────
    private AppConfig                _config           = new();
    private TypingBuffer             _typingBuffer     = new();
    private DebounceTimer?           _debounceTimer;
    private DebounceTimer?           _fastDebounceTimer;
    private PredictionCache          _predictionCache  = new(50);
    private CompletionFeedbackService        _acceptanceTracker = new();
    private RollingContextService    _rollingContext   = new(maxChars: 2000, timeoutMinutes: 5);
    private AcceptanceLearningService  _learningService          = new();
    private StyleProfileService        _styleProfileService       = new();
    private VocabularyProfileService   _vocabularyProfileService  = new();
    private LearningScoreService       _learningScoreService      = new();
    private bool                     _isEnabled        = true;
    private int                      _sessionAcceptCount;

    // ── Tray icon state (used by App.TrayIcon.cs) ─────────────────────────────
    private Icon?     _iconEnabled;
    private Icon?     _iconDisabled;
    private Timer?    _suspendTimer;
    private MenuItem? _enabledMenuItem;
    private MenuItem? _engineMenuItem;
    private MenuItem? _sessionMenuItem;

    // ── Prediction state (used by App.Prediction.cs) ─────────────────────────
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
    private long _suggestionShownAtTicks = 0;
    private int  _cycleDepth             = 0;
    private readonly CorrectionDetector _postEditDetector = new();

    private string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Keystroke",
        "debug.log"
    );

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
            Log($"Config loaded: Engine={_config.PredictionEngine}, Debounce={_config.DebounceMs}ms");

            // First-launch consent — must accept before input processing activates
            if (!_config.ConsentAccepted)
            {
                var consent = new Views.ConsentDialog();
                consent.ShowDialog();

                if (!consent.Accepted)
                {
                    Log("User declined consent. Exiting.");
                    Shutdown();
                    return;
                }

                _config.ConsentAccepted = true;
                _config.Save();
                Log("User accepted consent.");
            }

            // Prune completions file if it's grown too large
            _acceptanceTracker.PruneIfNeeded(maxLines: 2000);

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

            if (_config.LearningEnabled && _config.StyleProfileEnabled)
            {
                _styleProfileService.Start(_config.StyleProfileInterval);
                _vocabularyProfileService.Start(_config.StyleProfileInterval);
            }

            // Prune log files to prevent unbounded growth (keep last ~5000 lines each)
            PruneLogFiles();

            // Initialize prediction engine based on config
            _predictionEngine = CreatePredictionEngine();
            _styleProfileService.Engine = _predictionEngine;
            Log($"Prediction engine: {_predictionEngine?.GetType().Name ?? "none"}");

            // Initialize typing buffer
            _typingBuffer.BufferChanged += OnBufferChanged;
            _typingBuffer.BufferCleared += OnBufferCleared;

            // Initialize debounce timers
            // Normal debounce: user's configured delay (used for word boundaries as fallback)
            _debounceTimer = new DebounceTimer(_config.DebounceMs);
            _debounceTimer.DebounceComplete += OnDebounceComplete;
            // Fast debounce: short delay for mid-word typing so predictions fire while typing
            _fastDebounceTimer = new DebounceTimer(_config.FastDebounceMs);
            _fastDebounceTimer.DebounceComplete += OnDebounceComplete;

            // Initialize input listener
            _inputListener = new InputListenerService();
            _inputListener.CharacterTyped  += OnCharacterTyped;
            _inputListener.SpecialKeyPressed += OnSpecialKeyPressed;
            _inputListener.InputDiagnostic  += msg => LogToDebug(msg);
            _inputListener.Start();
            Log("Input listener started.");

            // Create suggestion panel (hidden initially) and apply saved theme
            _suggestionPanel = new SuggestionPanel();
            _suggestionPanel.ApplyTheme(ThemeDefinitions.Get(_config.ThemeId));
            Log("Suggestion panel created.");

            // Initialize OCR service with periodic capture (every 3 seconds)
            if (_config.OcrEnabled)
            {
                _ocrService = new ScreenReaderService();
                _ocrTimer   = new Timer(OnOcrTimerTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
                Log("OCR service initialized.");
            }
            else
            {
                Log("OCR disabled in settings.");
            }

            // Create system tray icon
            CreateTrayIcon();
            Log("Tray icon created.");

            Log("App ready. Right-click tray icon for options.");
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex}");
            MessageBox.Show($"Startup error: {ex.Message}", "Keystroke Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("App exiting...");
        CancelPendingPrediction();
        _postEditDetector.Dispose();
        _ocrTimer?.Dispose();
        _ocrService?.Dispose();
        _inputListener?.Dispose();
        (_predictionEngine as IDisposable)?.Dispose();
        _trayIcon?.Dispose();
        _suggestionPanel?.Close();
        _debounceTimer?.Dispose();
        _fastDebounceTimer?.Dispose();
        _suspendTimer?.Dispose();
        base.OnExit(e);
    }

    // ==================== Engine creation ====================

    private IPredictionEngine CreatePredictionEngine()
    {
        return _config.PredictionEngine.ToLower() switch
        {
            "gemini" when !string.IsNullOrWhiteSpace(_config.GeminiApiKey)
                => new GeminiPredictionEngine(_config.GeminiApiKey, _config.GeminiModel)
                {
                    SystemPrompt      = _config.EffectiveSystemPrompt,
                    LengthInstruction = _config.CompletionLengthInstruction,
                    Temperature       = _config.Temperature,
                    MaxOutputTokens   = _config.PresetMaxOutputTokens,
                    LearningService          = _learningService,
                    StyleProfileService      = _config.StyleProfileEnabled ? _styleProfileService      : null,
                    VocabularyProfileService = _config.StyleProfileEnabled ? _vocabularyProfileService : null
                },
            "gemini"
                => throw new InvalidOperationException("Gemini API key not set. Please configure it in Settings."),
            "gpt5" when !string.IsNullOrWhiteSpace(_config.OpenAiApiKey)
                => new Gpt5PredictionEngine(_config.OpenAiApiKey, _config.Gpt5Model)
                {
                    SystemPrompt      = _config.EffectiveSystemPrompt,
                    LengthInstruction = _config.CompletionLengthInstruction,
                    Temperature       = _config.Temperature,
                    MaxOutputTokens   = _config.PresetMaxOutputTokens,
                    LearningService          = _learningService,
                    StyleProfileService      = _config.StyleProfileEnabled ? _styleProfileService      : null,
                    VocabularyProfileService = _config.StyleProfileEnabled ? _vocabularyProfileService : null
                },
            "gpt5"
                => throw new InvalidOperationException("OpenAI API key not set. Please configure it in Settings."),
            "claude" when !string.IsNullOrWhiteSpace(_config.AnthropicApiKey)
                => new ClaudePredictionEngine(_config.AnthropicApiKey, _config.ClaudeModel)
                {
                    SystemPrompt      = _config.EffectiveSystemPrompt,
                    LengthInstruction = _config.CompletionLengthInstruction,
                    Temperature       = _config.Temperature,
                    MaxOutputTokens   = _config.PresetMaxOutputTokens,
                    LearningService          = _learningService,
                    StyleProfileService      = _config.StyleProfileEnabled ? _styleProfileService      : null,
                    VocabularyProfileService = _config.StyleProfileEnabled ? _vocabularyProfileService : null
                },
            "claude"
                => throw new InvalidOperationException("Claude API key not set. Please configure it in Settings."),
            "ollama"
                => new OllamaPredictionEngine(_config.OllamaModel, _config.OllamaEndpoint)
                {
                    Temperature     = 0.1,
                    MaxOutputTokens = _config.PresetMaxOutputTokens,
                    LearningService = _learningService,
                    StyleProfileService = _config.StyleProfileEnabled ? _styleProfileService : null
                },
            "openrouter" when !string.IsNullOrWhiteSpace(_config.OpenRouterApiKey)
                => new OpenRouterPredictionEngine(_config.OpenRouterApiKey, _config.OpenRouterModel)
                {
                    SystemPrompt      = _config.EffectiveSystemPrompt,
                    LengthInstruction = _config.CompletionLengthInstruction,
                    Temperature       = _config.Temperature,
                    MaxOutputTokens   = _config.PresetMaxOutputTokens,
                    LearningService          = _learningService,
                    StyleProfileService      = _config.StyleProfileEnabled ? _styleProfileService      : null,
                    VocabularyProfileService = _config.StyleProfileEnabled ? _vocabularyProfileService : null
                },
            "openrouter"
                => throw new InvalidOperationException("OpenRouter API key not set. Please configure it in Settings."),
            _ => new DummyPredictionEngine()
        };
    }

    // ==================== Window management ====================

    private void ShowDebugWindow()
    {
        if (_debugWindow == null || !_debugWindow.IsLoaded)
        {
            _debugWindow = new DebugWindow();
            _debugWindow.Closed += (s, e) => _debugWindow = null;
            _debugWindow.Log("Debug window opened.");
            _debugWindow.Log($"Status: {(_isEnabled ? "Enabled" : "Disabled")}");
            _debugWindow.Log($"Engine: {_predictionEngine?.GetType().Name}");
            _debugWindow.Log($"Buffer: \"{_typingBuffer.CurrentText}\"");

            // Show rolling context status
            var ctxInfo = _rollingContext.GetInfo();
            if (ctxInfo.Length > 0 && !ctxInfo.IsStale)
            {
                _debugWindow.Log($"Rolling context: {ctxInfo.Length} chars from {ctxInfo.Process}");
                _debugWindow.Log($"  Last: \"{TruncateString(ctxInfo.WindowTitle, 40)}\"");
            }
            else
            {
                _debugWindow.Log("Rolling context: (empty or stale)");
            }

            // Show learning stats
            var learningStats = _learningService.GetStats();
            _debugWindow.Log($"Learning file: {learningStats.DataFilePath}");
            _debugWindow.Log($"  Exists: {learningStats.DataFileExists}, Size: {learningStats.DataFileSize} bytes");

            if (learningStats.TotalAccepted > 0)
            {
                _debugWindow.Log($"Learning data: {learningStats.TotalAccepted} accepted completions");
                var categories = string.Join(", ", learningStats.ByCategory.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
                _debugWindow.Log($"  By category: {categories}");
            }
            else if (learningStats.DataFileExists && learningStats.DataFileSize > 0)
            {
                _debugWindow.Log("Learning data: File exists but 0 entries loaded (parsing issue?)");
            }
            else
            {
            _debugWindow.Log("Learning data: (no data yet)");
            }

            // Show style profile status
            var styleProfile = _styleProfileService.GetProfile();
            if (styleProfile != null)
            {
                _debugWindow.Log($"Style profile: {styleProfile.CategoryProfiles.Count} categories");
                _debugWindow.Log($"  General: \"{TruncateString(styleProfile.GeneralProfile, 60)}...");
                _debugWindow.Log($"  Last updated: {styleProfile.LastUpdated:yyyy-MM-dd}");
            }
            else
            {
                _debugWindow.Log("Style profile: (not generated yet)");
            }

            _debugWindow.Log("Type anywhere to see events here.\n");
        }
        _debugWindow.Show();
        _debugWindow.Activate();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_config, _styleProfileService, _vocabularyProfileService, _learningScoreService);
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
    /// Called when the settings window closes. Re-reads config and recreates the engine.
    /// </summary>
    private void SyncSettingsFromConfig()
    {
        _config = AppConfig.Load();
        Log("Settings updated from config.");

        // Dispose the old engine's HttpClient before creating a new one
        (_predictionEngine as IDisposable)?.Dispose();
        _predictionEngine = CreatePredictionEngine();
        _styleProfileService.Engine = _predictionEngine;
        Log($"Prediction engine recreated: {_predictionEngine?.GetType().Name}");

        _styleProfileService.CancelGeneration();
        _styleProfileService.UpdateInterval(_config.StyleProfileInterval);
        _vocabularyProfileService.UpdateInterval(_config.StyleProfileInterval);

        if (_config.LearningEnabled && _config.StyleProfileEnabled)
        {
            _styleProfileService.Start(_config.StyleProfileInterval);
            _vocabularyProfileService.Start(_config.StyleProfileInterval);
        }

        // Update debounce timers
        _debounceTimer?.Dispose();
        _debounceTimer = new DebounceTimer(_config.DebounceMs);
        _debounceTimer.DebounceComplete += OnDebounceComplete;
        _fastDebounceTimer?.Dispose();
        _fastDebounceTimer = new DebounceTimer(_config.FastDebounceMs);
        _fastDebounceTimer.DebounceComplete += OnDebounceComplete;

        // Toggle OCR on/off based on settings
        if (_config.OcrEnabled && _ocrService == null)
        {
            _ocrService = new ScreenReaderService();
            _ocrTimer   = new Timer(OnOcrTimerTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            Log("OCR enabled.");
        }
        else if (!_config.OcrEnabled && _ocrService != null)
        {
            _ocrTimer?.Dispose();
            _ocrTimer   = null;
            _ocrService?.Dispose();
            _ocrService = null;
            Log("OCR disabled.");
        }

        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = BuildToolTip();
            _trayIcon.Icon        = GetTrayIcon(_isEnabled);
        }
        if (_engineMenuItem != null)
            _engineMenuItem.Header = $"Engine: {_config.PredictionEngine} ({GetCurrentModelName()})";
        if (_sessionMenuItem != null)
            _sessionMenuItem.Header = $"Accepted: {_sessionAcceptCount} this session";
    }

    // ==================== Helpers ====================

    private void LogToDebug(string message)
    {
        if (_debugWindow != null)
        {
            Dispatcher.BeginInvoke(() => _debugWindow?.Log(message));
        }
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
        string[] logFiles = ["debug.log", "gemini.log", "claude.log", "gpt5.log", "ocr.log", "learning.log", "style-profile.log"];
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

    /// <summary>
    /// Helper to truncate strings for display in the debug window.
    /// </summary>
    private static string TruncateString(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
