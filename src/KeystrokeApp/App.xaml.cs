using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using KeystrokeApp.Services;
using KeystrokeApp.Views;

namespace KeystrokeApp;

/// <summary>
/// Main application - manages system tray icon and keyboard hook.
/// </summary>
public partial class App : Application
{
    private KeyboardHookService? _hookService;
    private TaskbarIcon? _trayIcon;
    private TestWindow? _testWindow;
    private SuggestionPanel? _suggestionPanel;
    private SettingsWindow? _settingsWindow;
    private IPredictionEngine? _predictionEngine;
    private OcrService? _ocrService;
    private Timer? _ocrTimer;

    private AppConfig _config = new();
    private TypingBuffer _typingBuffer = new();
    private DebounceTimer? _debounceTimer;
    private DebounceTimer? _fastDebounceTimer;
    private PredictionCache _predictionCache = new(50);
    private AcceptanceTracker _acceptanceTracker = new();
    private RollingContextService _rollingContext = new(maxChars: 500, timeoutMinutes: 5);
    private AcceptanceLearningService _learningService = new();
    private bool _isEnabled = true;
    private int _sessionAcceptCount;
    private Timer? _suspendTimer;
    private MenuItem? _enabledMenuItem;
    private readonly object _predictionCtsLock = new();
    private CancellationTokenSource? _predictionCts;
    private string _lastPredictionPrefix = "";

    private string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Keystroke",
        "debug.log"
    );

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ensure directories and config exist
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        AppConfig.EnsureExists();

        try
        {
            // Load config
            _config = AppConfig.Load();
            Log($"Config loaded: Engine={_config.PredictionEngine}, Debounce={_config.DebounceMs}ms");

            // First-launch consent — must accept before keystroke monitoring activates
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

            // Prune tracking file if it's grown too large
            _acceptanceTracker.PruneIfNeeded(maxLines: 2000);

            // Prune log files to prevent unbounded growth (keep last ~5000 lines each)
            PruneLogFiles();

            // Initialize prediction engine based on config
            _predictionEngine = CreatePredictionEngine();
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

            // Initialize keyboard hook
            _hookService = new KeyboardHookService();
            _hookService.CharacterTyped += OnCharacterTyped;
            _hookService.SpecialKeyPressed += OnSpecialKeyPressed;
            _hookService.HookDiagnostic += msg => LogToDebug(msg);
            _hookService.Start();
            Log("Keyboard hook started.");

            // Create suggestion panel (hidden initially)
            _suggestionPanel = new SuggestionPanel();
            Log("Suggestion panel created.");

            // Initialize OCR service with periodic capture (every 3 seconds)
            if (_config.OcrEnabled)
            {
                _ocrService = new OcrService();
                _ocrTimer = new Timer(OnOcrTimerTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
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

    private IPredictionEngine CreatePredictionEngine()
    {
        return _config.PredictionEngine.ToLower() switch
        {
            "gemini" when !string.IsNullOrWhiteSpace(_config.GeminiApiKey)
                => new GeminiPredictionEngine(_config.GeminiApiKey, _config.GeminiModel)
                {
                    SystemPrompt = _config.EffectiveSystemPrompt,
                    LengthInstruction = _config.CompletionLengthInstruction,
                    Temperature = _config.Temperature,
                    MaxOutputTokens = _config.PresetMaxOutputTokens,
                    LearningService = _learningService
                },
            "gemini"
                => throw new InvalidOperationException("Gemini API key not set. Please configure it in Settings."),
            "gpt5" when !string.IsNullOrWhiteSpace(_config.OpenAiApiKey)
                => new Gpt5PredictionEngine(_config.OpenAiApiKey, _config.Gpt5Model)
                {
                    SystemPrompt = _config.EffectiveSystemPrompt,
                    LengthInstruction = _config.CompletionLengthInstruction,
                    Temperature = _config.Temperature,
                    MaxOutputTokens = _config.PresetMaxOutputTokens,
                    LearningService = _learningService
                },
            "gpt5"
                => throw new InvalidOperationException("OpenAI API key not set. Please configure it in Settings."),
            "claude" when !string.IsNullOrWhiteSpace(_config.AnthropicApiKey)
                => new ClaudePredictionEngine(_config.AnthropicApiKey, _config.ClaudeModel)
                {
                    SystemPrompt = _config.EffectiveSystemPrompt,
                    LengthInstruction = _config.CompletionLengthInstruction,
                    Temperature = _config.Temperature,
                    MaxOutputTokens = _config.PresetMaxOutputTokens,
                    LearningService = _learningService
                },
            "claude"
                => throw new InvalidOperationException("Claude API key not set. Please configure it in Settings."),
            _ => new DummyPredictionEngine()
        };
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon();
        _trayIcon.Icon = CreateKeyboardIcon(_isEnabled);
        _trayIcon.ToolTipText = BuildToolTip();

        var menu = new ContextMenu();

        _enabledMenuItem = new MenuItem { Header = "Enabled", IsCheckable = true, IsChecked = _isEnabled };
        _enabledMenuItem.Click += (s, e) =>
        {
            _isEnabled = _enabledMenuItem.IsChecked;
            _trayIcon!.Icon = CreateKeyboardIcon(_isEnabled);
            _trayIcon!.ToolTipText = BuildToolTip();
            Log(_isEnabled ? "Enabled" : "Disabled");
        };

        var suspendItem = new MenuItem { Header = "Suspend for 30 min" };
        suspendItem.Click += (s, e) =>
        {
            _isEnabled = false;
            _enabledMenuItem.IsChecked = false;
            _trayIcon!.Icon = CreateKeyboardIcon(false);
            _trayIcon!.ToolTipText = BuildToolTip() + "\nSuspended until " + DateTime.Now.AddMinutes(30).ToString("t");
            _suggestionPanel?.HideSuggestion();
            CancelPendingPrediction();
            _typingBuffer.Clear();
            Log("Suspended for 30 minutes");

            _suspendTimer?.Dispose();
            _suspendTimer = new Timer(_ =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _isEnabled = true;
                    _enabledMenuItem.IsChecked = true;
                    _trayIcon!.Icon = CreateKeyboardIcon(true);
                    _trayIcon!.ToolTipText = BuildToolTip();
                    Log("Resumed after suspension");
                });
            }, null, TimeSpan.FromMinutes(30), TimeSpan.FromMilliseconds(-1));
        };

        var engineInfo = new MenuItem
        {
            Header = $"Engine: {_config.PredictionEngine} ({GetCurrentModelName()})",
            IsEnabled = false
        };

        var sessionInfo = new MenuItem
        {
            Header = $"Accepted: {_sessionAcceptCount} this session",
            IsEnabled = false
        };

        var showDebugItem = new MenuItem { Header = "Show Debug Window" };
        showDebugItem.Click += (s, e) => ShowDebugWindow();

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (s, e) => ShowSettingsWindow();

        var openConfigItem = new MenuItem { Header = "Open Config Folder" };
        openConfigItem.Click += (s, e) =>
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Keystroke"
            );
            System.Diagnostics.Process.Start("explorer.exe", configDir);
        };

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (s, e) =>
        {
            Log("Exit requested from tray menu.");
            Shutdown();
        };

        menu.Items.Add(_enabledMenuItem);
        menu.Items.Add(suspendItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(engineInfo);
        menu.Items.Add(sessionInfo);
        menu.Items.Add(new Separator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(showDebugItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (s, e) => ShowSettingsWindow();
    }

    private string BuildToolTip()
    {
        var status = _isEnabled ? "Active" : "Paused";
        var engine = _config.PredictionEngine;
        var model = GetCurrentModelName();
        var accepted = _sessionAcceptCount;
        return $"Keystroke - {status}\n{engine} ({model})\n{accepted} accepted this session";
    }

    private string GetCurrentModelName()
    {
        return _config.PredictionEngine.ToLower() switch
        {
            "gemini" => _config.GeminiModel ?? "default",
            "gpt5" => _config.Gpt5Model ?? "default",
            "claude" => _config.ClaudeModel ?? "default",
            _ => "default"
        };
    }

    private void ShowDebugWindow()
    {
        if (_testWindow == null || !_testWindow.IsLoaded)
        {
            _testWindow = new TestWindow();
            _testWindow.Closed += (s, e) => _testWindow = null;
            _testWindow.Log("Debug window opened.");
            _testWindow.Log($"Status: {(_isEnabled ? "Enabled" : "Disabled")}");
            _testWindow.Log($"Engine: {_predictionEngine?.GetType().Name}");
            _testWindow.Log($"Buffer: \"{_typingBuffer.CurrentText}\"");
            
            // Show rolling context status
            var ctxInfo = _rollingContext.GetInfo();
            if (ctxInfo.Length > 0 && !ctxInfo.IsStale)
            {
                _testWindow.Log($"Rolling context: {ctxInfo.Length} chars from {ctxInfo.Process}");
                _testWindow.Log($"  Last: \"{TruncateString(ctxInfo.WindowTitle, 40)}\"");
            }
            else
            {
                _testWindow.Log("Rolling context: (empty or stale)");
            }
            
            // Show learning stats
            var learningStats = _learningService.GetStats();
            _testWindow.Log($"Learning file: {learningStats.DataFilePath}");
            _testWindow.Log($"  Exists: {learningStats.DataFileExists}, Size: {learningStats.DataFileSize} bytes");
            
            if (learningStats.TotalAccepted > 0)
            {
                _testWindow.Log($"Learning data: {learningStats.TotalAccepted} accepted completions");
                var categories = string.Join(", ", learningStats.ByCategory.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
                _testWindow.Log($"  By category: {categories}");
            }
            else if (learningStats.DataFileExists && learningStats.DataFileSize > 0)
            {
                _testWindow.Log("Learning data: File exists but 0 entries loaded (parsing issue?)");
            }
            else
            {
                _testWindow.Log("Learning data: (no data yet)");
            }
            
            _testWindow.Log("Type anywhere to see events here.\n");
        }
        _testWindow.Show();
        _testWindow.Activate();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_config);
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
    /// Called when settings window closes. Re-reads config and updates engine.
    /// </summary>
    private void SyncSettingsFromConfig()
    {
        _config = AppConfig.Load();
        Log("Settings updated from config.");

        // Dispose the old engine's HttpClient before creating a new one
        (_predictionEngine as IDisposable)?.Dispose();
        _predictionEngine = CreatePredictionEngine();
        Log($"Prediction engine recreated: {_predictionEngine?.GetType().Name}");

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
            _ocrService = new OcrService();
            _ocrTimer = new Timer(OnOcrTimerTick, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
            Log("OCR enabled.");
        }
        else if (!_config.OcrEnabled && _ocrService != null)
        {
            _ocrTimer?.Dispose();
            _ocrTimer = null;
            _ocrService?.Dispose();
            _ocrService = null;
            Log("OCR disabled.");
        }

        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = BuildToolTip();
            _trayIcon.Icon = CreateKeyboardIcon(_isEnabled);
        }
    }

    private void ToggleEnabled()
    {
        _isEnabled = !_isEnabled;

        Dispatcher.BeginInvoke(() =>
        {
            if (_enabledMenuItem != null)
                _enabledMenuItem.IsChecked = _isEnabled;
            if (_trayIcon != null)
                _trayIcon.ToolTipText = _isEnabled ? "Keystroke" : "Keystroke (disabled)";
                _trayIcon.Icon = CreateKeyboardIcon(_isEnabled);
                _trayIcon.ToolTipText = BuildToolTip();

            if (!_isEnabled)
            {
                _suggestionPanel?.HideSuggestion();
                CancelPendingPrediction();
                _typingBuffer.Clear();
            }
        });

        Log($"Toggled via Ctrl+Shift+K: {(_isEnabled ? "Enabled" : "Disabled")}");
    }

    // ==================== Keyboard Event Handlers ====================

    /// <summary>
    /// Characters that signal a word boundary — trigger prediction immediately.
    /// </summary>
    private static readonly HashSet<char> _wordBoundaryChars = [' ', '.', ',', '!', '?', ':', ';', ')', ']'];

    private void OnCharacterTyped(char c)
    {
        if (!_isEnabled) return;

        _typingBuffer.AddChar(c);

        if (_testWindow != null)
        {
            Dispatcher.BeginInvoke(() => _testWindow?.Log($"Char: '{c}' → Buffer: \"{_typingBuffer.CurrentText}\""));
        }
    }

    private void OnSpecialKeyPressed(KeyboardHookService.SpecialKeyEventArgs args)
    {
        var key = args.Key;
        
        switch (key)
        {
            case KeyboardHookService.SpecialKey.Backspace:
                if (_isEnabled)
                {
                    _typingBuffer.RemoveLastChar();
                    LogToDebug($"Backspace → Buffer: \"{_typingBuffer.CurrentText}\"");
                }
                break;

            case KeyboardHookService.SpecialKey.Enter:
            case KeyboardHookService.SpecialKey.Escape:
                if (_isEnabled)
                {
                    var oldBuffer = _typingBuffer.CurrentText;

                    // Track dismissal if a suggestion was showing
                    if (_config.LearningEnabled && _suggestionPanel?.HasSuggestion == true)
                    {
                        var (pn, wt) = ActiveWindowService.GetActiveWindow();
                        var dismissed = _suggestionPanel.GetFullSuggestion().Substring(oldBuffer.Length);
                        _acceptanceTracker.LogDismissed(oldBuffer, dismissed, pn, wt);
                    }

                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();
                    CancelPendingPrediction();
                    LogToDebug($"{key} → Buffer cleared (was: \"{oldBuffer}\")");
                }
                break;

            case KeyboardHookService.SpecialKey.LeftArrow:
            case KeyboardHookService.SpecialKey.RightArrow:
            case KeyboardHookService.SpecialKey.UpArrow:
            case KeyboardHookService.SpecialKey.DownArrow:
            case KeyboardHookService.SpecialKey.Home:
            case KeyboardHookService.SpecialKey.End:
            case KeyboardHookService.SpecialKey.PageUp:
            case KeyboardHookService.SpecialKey.PageDown:
            case KeyboardHookService.SpecialKey.Delete:
                if (_isEnabled && !_typingBuffer.IsEmpty)
                {
                    var oldBuffer = _typingBuffer.CurrentText;
                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();
                    CancelPendingPrediction();
                    LogToDebug($"{key} → Buffer cleared (cursor moved, was: \"{oldBuffer}\")");
                }
                break;

            case KeyboardHookService.SpecialKey.CtrlDownArrow:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    Dispatcher.BeginInvoke(() => _suggestionPanel?.NextSuggestion());
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Down → Next suggestion");
                }
                break;

            case KeyboardHookService.SpecialKey.CtrlUpArrow:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    Dispatcher.BeginInvoke(() => _suggestionPanel?.PreviousSuggestion());
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Up → Previous suggestion");
                }
                break;

            case KeyboardHookService.SpecialKey.CtrlShiftK:
                ToggleEnabled();
                args.ShouldSwallow = true;
                break;

            case KeyboardHookService.SpecialKey.ShiftTab:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    var buffer = _typingBuffer.CurrentText;
                    var completion = _suggestionPanel.GetFullSuggestion().Substring(buffer.Length);
                    var nextWord = GetNextWord(completion);

                    LogToDebug($"Shift+Tab → Accepting next word: \"{nextWord}\" (remaining: \"{completion[nextWord.Length..]}\")");

                    InjectText(nextWord);

                    var (procName, winTitle) = ActiveWindowService.GetActiveWindow();
                    if (_config.LearningEnabled)
                        _acceptanceTracker.LogAccepted(buffer, nextWord, procName, winTitle);

                    var newBuffer = buffer + nextWord;
                    _rollingContext.AppendAccepted(newBuffer, procName, winTitle);

                    // Update buffer silently (no event → no prediction debounce)
                    _typingBuffer.SetText(newBuffer);
                    _lastPredictionPrefix = newBuffer;
                    CancelPendingPrediction();

                    // Show the remaining completion, or hide if nothing left
                    var remaining = completion[nextWord.Length..];
                    if (string.IsNullOrWhiteSpace(remaining))
                    {
                        _suggestionPanel.HideSuggestion();
                    }
                    else
                    {
                        _suggestionPanel.ShowSuggestion(newBuffer, remaining);
                    }

                    args.ShouldSwallow = true;
                }
                break;

            case KeyboardHookService.SpecialKey.CtrlRight:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    var buffer = _typingBuffer.CurrentText;
                    var completion = _suggestionPanel.GetFullSuggestion().Substring(buffer.Length);
                    var nextWord = GetNextWord(completion);

                    LogToDebug($"Ctrl+Right → Accepting next word: \"{nextWord}\" (remaining: \"{completion[nextWord.Length..]}\")");

                    InjectText(nextWord);

                    var (procName, winTitle) = ActiveWindowService.GetActiveWindow();
                    if (_config.LearningEnabled)
                        _acceptanceTracker.LogAccepted(buffer, nextWord, procName, winTitle);

                    var newBuffer = buffer + nextWord;
                    _rollingContext.AppendAccepted(newBuffer, procName, winTitle);

                    _typingBuffer.SetText(newBuffer);
                    _lastPredictionPrefix = newBuffer;
                    CancelPendingPrediction();

                    var remaining = completion[nextWord.Length..];
                    if (string.IsNullOrWhiteSpace(remaining))
                        _suggestionPanel.HideSuggestion();
                    else
                        _suggestionPanel.ShowSuggestion(newBuffer, remaining);

                    args.ShouldSwallow = true;
                }
                break;

            case KeyboardHookService.SpecialKey.Tab:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    var buffer = _typingBuffer.CurrentText;
                    var fullText = _suggestionPanel.GetFullSuggestion();

                    LogToDebug($"Tab → Buffer: \"{buffer}\" ({buffer.Length} chars)");
                    LogToDebug($"Tab → Buffer ends with space: {buffer.EndsWith(" ")}");
                    LogToDebug($"Tab → Full suggestion: \"{fullText}\" ({fullText.Length} chars)");

                    var completion = fullText.Substring(buffer.Length);
                    LogToDebug($"Tab → Injecting: \"{completion}\" ({completion.Length} chars)");
                    LogToDebug($"Tab → First char code: {(int)completion.FirstOrDefault()}");

                    InjectText(completion);

                    // Show confirmation flash
                    _suggestionPanel?.AcceptSuggestion();
                    _sessionAcceptCount++;
                    UpdateTraySessionInfo();

                    // Track acceptance
                    var (procName, winTitle) = ActiveWindowService.GetActiveWindow();
                    if (_config.LearningEnabled)
                        _acceptanceTracker.LogAccepted(buffer, completion, procName, winTitle);

                    // Update rolling context with the full accepted text (buffer + completion)
                    // This provides continuity for the next prediction
                    var fullAcceptedText = buffer + completion;
                    _rollingContext.AppendAccepted(fullAcceptedText, procName, winTitle);
                    LogToDebug($"Tab → Rolling context updated (+{fullAcceptedText.Length} chars)");

                    _typingBuffer.Clear();
                    _suggestionPanel.HideSuggestion();
                    CancelPendingPrediction();

                    // Swallow the Tab key so it doesn't insert a tab character
                    args.ShouldSwallow = true;
                    LogToDebug($"Tab → Key swallowed");
                }
                else
                {
                    LogToDebug($"Tab pressed (no suggestion, passing through)");
                }
                break;
        }
    }

    // ==================== Buffer Event Handlers ====================

    private void OnBufferChanged(string newText)
    {
        LogToDebug($"Buffer: \"{newText}\" ({newText.Length} chars)");

        // Only cancel in-flight predictions if the user backspaced or the text diverged.
        // If they're just extending (typing forward), let the current prediction finish —
        // it may still be relevant, and cancelling it creates dead air.
        bool isExtending = newText.Length > _lastPredictionPrefix.Length
            && newText.StartsWith(_lastPredictionPrefix);

        if (!isExtending)
        {
            CancelPendingPrediction();
        }

        // On word boundaries (space, period, etc.), trigger prediction immediately
        if (newText.Length > 0 && _wordBoundaryChars.Contains(newText[^1]))
        {
            _debounceTimer?.Cancel();
            _fastDebounceTimer?.Cancel();
            OnDebounceComplete();
        }
        else
        {
            // Use the fast debounce (100ms) so predictions fire while typing,
            // not just after a long pause
            _debounceTimer?.Cancel();
            _fastDebounceTimer?.Restart();
        }
    }

    private void OnBufferCleared()
    {
        CancelPendingPrediction();
        _suggestionPanel?.HideSuggestion();
        LogToDebug("Buffer cleared");
    }

    /// <summary>
    /// Called after debounce pause - fetch a prediction from the engine.
    /// Fires on a background thread from DebounceTimer.
    /// </summary>
    private void OnDebounceComplete()
    {
        var buffer = _typingBuffer.CurrentText;

        if (buffer.Length < _config.MinBufferLength)
            return;

        // Check cache first — instant result for repeated/backspaced prefixes
        if (_predictionCache.TryGet(buffer, out var cached))
        {
            if (cached != null)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _suggestionPanel?.ShowSuggestion(buffer, cached);
                });
                LogToDebug($"Cache hit: \"{buffer}\" + \"{cached}\"");
            }
            return;
        }

        // Cancel any previous prediction request and track what we're predicting for
        CancellationToken ct;
        lock (_predictionCtsLock)
        {
            _predictionCts?.Cancel();
            _predictionCts = new CancellationTokenSource();
            ct = _predictionCts.Token;
        }
        _lastPredictionPrefix = buffer;

        // Build context snapshot — app detection is instant, OCR uses cached result
        var (processName, windowTitle) = ActiveWindowService.GetActiveWindow();
        var context = new ContextSnapshot
        {
            TypedText = buffer,
            ProcessName = processName,
            WindowTitle = windowTitle,
            // Scrub PII from data sent to external AI providers.
            // TypedText is intentionally NOT scrubbed — it's the completion target.
            ScreenText = PiiFilter.Scrub(_ocrService?.CachedText),
            RollingContext = PiiFilter.Scrub(_rollingContext.GetContext(processName, windowTitle))
        };

        // Run prediction on background using streaming for progressive display
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(8000);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                bool streamStarted = false;

                var completion = await _predictionEngine!.PredictStreamingAsync(
                    context,
                    chunk =>
                    {
                        if (ct.IsCancellationRequested) return;

                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!streamStarted)
                            {
                                streamStarted = true;
                                _suggestionPanel?.BeginStreamingSuggestion(buffer);

                                // Add leading space if needed
                                if (!buffer.EndsWith(" ") && !chunk.StartsWith(" "))
                                    chunk = " " + chunk;
                            }
                            _suggestionPanel?.AppendSuggestion(buffer, chunk);
                        });
                    },
                    linkedCts.Token);

                // Cache the final result
                _predictionCache.Put(buffer, completion);

                if (ct.IsCancellationRequested) return;

                // Signal that streaming is complete - suggestion is fully loaded
                Dispatcher.BeginInvoke(() =>
                {
                    _suggestionPanel?.OnStreamingComplete();
                });

                if (completion != null)
                {
                    var rollingInfo = context.HasRollingContext 
                        ? $"[rolling={context.RollingContext!.Length} chars]" 
                        : "[no rolling context]";
                    LogToDebug($"Streamed: \"{buffer}\" + \"{completion}\" [app={processName}] {rollingInfo}");

                    // Fire background request for alternatives
                    _ = FetchAlternativesAsync(context, buffer, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogToDebug($"Prediction error: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>
    /// Periodic OCR capture. Only re-captures when the active window has changed.
    /// </summary>
    private void OnOcrTimerTick(object? state)
    {
        if (!_isEnabled || _ocrService == null) return;

        if (_ocrService.ShouldRecapture())
        {
            _ = _ocrService.CaptureAsync();
        }
    }

    /// <summary>
    /// Fetch alternative suggestions in the background and push them to the panel.
    /// </summary>
    private async Task FetchAlternativesAsync(ContextSnapshot context, string buffer, CancellationToken ct)
    {
        try
        {
            if (_predictionEngine == null) return;

            var alternatives = await _predictionEngine.FetchAlternativesAsync(context, 3, ct);
            if (ct.IsCancellationRequested || alternatives.Count == 0) return;

            Dispatcher.BeginInvoke(() =>
            {
                _suggestionPanel?.SetAlternatives(buffer, alternatives);
            });
            LogToDebug($"Loaded {alternatives.Count} alternatives for \"{buffer}\"");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogToDebug($"Alternatives error: {ex.Message}");
        }
    }

    private void CancelPendingPrediction()
    {
        lock (_predictionCtsLock)
        {
            _predictionCts?.Cancel();
            _predictionCts = null;
        }
    }

    // ==================== Text Injection ====================

    private void InjectText(string text)
    {
        // Inject text directly via SendInput (VK_PACKET) — no clipboard involved.
        // TextEntry sends each character as a Unicode keystroke, which works reliably
        // across virtually all Windows apps without touching clipboard state.
        Task.Run(async () =>
        {
            await Task.Delay(30); // Let the accepting key (Tab/Shift+Tab) release first
            var simulator = new WindowsInput.InputSimulator();
            simulator.Keyboard.TextEntry(text);
        });
    }

    // ==================== Helpers ====================

    private void LogToDebug(string message)
    {
        if (_testWindow != null)
        {
            Dispatcher.BeginInvoke(() => _testWindow?.Log(message));
        }
    }

    private void Log(string message)
    {
        File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
    }

    /// <summary>
    /// Prune all log files at startup to prevent unbounded growth.
    /// Keeps the most recent lines of each log file.
    /// </summary>
    private void PruneLogFiles()
    {
        var logDir = Path.GetDirectoryName(_logPath)!;
        string[] logFiles = ["debug.log", "gemini.log", "claude.log", "gpt5.log", "ocr.log", "learning.log"];
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
            catch { }
        }
    }

    /// <summary>
    /// Extracts the first word (including leading space) from a completion string.
    /// e.g. " because I had" → " because"
    /// If the completion is a single word, returns it in full.
    /// </summary>
    private static string GetNextWord(string completion)
    {
        if (string.IsNullOrEmpty(completion)) return completion;
        int start = completion[0] == ' ' ? 1 : 0;
        int spaceIdx = completion.IndexOf(' ', start);
        return spaceIdx < 0 ? completion : completion[..spaceIdx];
    }

    /// <summary>
    /// Helper to truncate strings for display.
    /// </summary>
    private static string TruncateString(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private void UpdateTraySessionInfo()
    {
        if (_trayIcon?.ContextMenu?.Items == null) return;
        foreach (var item in _trayIcon.ContextMenu.Items)
        {
            if (item is MenuItem mi && mi.Header is string s && s.StartsWith("Accepted:"))
            {
                mi.Header = $"Accepted: {_sessionAcceptCount} this session";
                break;
            }
        }
        _trayIcon.ToolTipText = BuildToolTip();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("App exiting...");
        CancelPendingPrediction();
        _ocrTimer?.Dispose();
        _ocrService?.Dispose();
        _hookService?.Dispose();
        (_predictionEngine as IDisposable)?.Dispose();
        _trayIcon?.Dispose();
        _suggestionPanel?.Close();
        _debounceTimer?.Dispose();
        _fastDebounceTimer?.Dispose();
        _suspendTimer?.Dispose();
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private Icon CreateKeyboardIcon(bool enabled = true)
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);

        g.Clear(System.Drawing.Color.FromArgb(30, 30, 46));

        using var bodyBrush = new SolidBrush(System.Drawing.Color.FromArgb(200, 200, 220));
        using var smallBrush = new SolidBrush(System.Drawing.Color.FromArgb(30, 30, 46));
        using var statusGreen = new SolidBrush(System.Drawing.Color.FromArgb(47, 186, 78));
        using var statusGray = new SolidBrush(System.Drawing.Color.FromArgb(100, 100, 120));

        g.FillRectangle(bodyBrush, 2, 8, 28, 16);

        for (int i = 0; i < 5; i++)
        {
            g.FillRectangle(smallBrush, 4 + i * 5, 10, 4, 4);
        }
        g.FillRectangle(smallBrush, 8, 18, 16, 3);

        g.FillEllipse(enabled ? statusGreen : statusGray, 22, 2, 8, 8);

        var hIcon = bitmap.GetHicon();
        var tempIcon = Icon.FromHandle(hIcon);
        var icon = (Icon)tempIcon.Clone();
        tempIcon.Dispose();
        DestroyIcon(hIcon);
        return icon;
    }
}
