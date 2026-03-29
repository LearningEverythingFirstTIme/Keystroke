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
    private bool _isEnabled = true;
    private MenuItem? _enabledMenuItem;
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
                    MaxOutputTokens = _config.PresetMaxOutputTokens
                },
            "gemini"
                => throw new InvalidOperationException("Gemini API key not set in config.json"),
            _ => new DummyPredictionEngine()
        };
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon();
        _trayIcon.Icon = CreateKeyboardIcon();
        _trayIcon.ToolTipText = "Keystroke";

        var menu = new ContextMenu();

        _enabledMenuItem = new MenuItem { Header = "Enabled", IsCheckable = true, IsChecked = _isEnabled };
        _enabledMenuItem.Click += (s, e) =>
        {
            _isEnabled = _enabledMenuItem.IsChecked;
            _trayIcon!.ToolTipText = _isEnabled ? "Keystroke" : "Keystroke (disabled)";
            Log(_isEnabled ? "Enabled" : "Disabled");
        };

        var showDebugItem = new MenuItem { Header = "Show Debug Window" };
        showDebugItem.Click += (s, e) => ShowDebugWindow();

        var settingsItem = new MenuItem { Header = "⚙️ Settings" };
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
        menu.Items.Add(new Separator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(showDebugItem);
        menu.Items.Add(openConfigItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (s, e) => ShowDebugWindow();
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

        // Update prediction engine properties
        if (_predictionEngine is GeminiPredictionEngine gemini)
        {
            gemini.SystemPrompt = _config.EffectiveSystemPrompt;
            gemini.LengthInstruction = _config.CompletionLengthInstruction;
            gemini.Temperature = _config.Temperature;
            gemini.MaxOutputTokens = _config.PresetMaxOutputTokens;
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
                    if (_suggestionPanel?.HasSuggestion == true)
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

                    // Track acceptance
                    var (procName, winTitle) = ActiveWindowService.GetActiveWindow();
                    _acceptanceTracker.LogAccepted(buffer, completion, procName, winTitle);

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
        _predictionCts?.Cancel();
        _predictionCts = new CancellationTokenSource();
        var ct = _predictionCts.Token;
        _lastPredictionPrefix = buffer;

        // Build context snapshot — app detection is instant, OCR uses cached result
        var (processName, windowTitle) = ActiveWindowService.GetActiveWindow();
        var context = new ContextSnapshot
        {
            TypedText = buffer,
            ProcessName = processName,
            WindowTitle = windowTitle,
            ScreenText = _ocrService?.CachedText
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

                if (completion != null)
                {
                    LogToDebug($"Streamed: \"{buffer}\" + \"{completion}\" [app={processName}]");

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
            if (_predictionEngine is not GeminiPredictionEngine gemini) return;

            var alternatives = await gemini.FetchAlternativesAsync(context, 3, ct);
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
        _predictionCts?.Cancel();
        _predictionCts = null;
    }

    // ==================== Text Injection ====================

    private void InjectText(string text)
    {
        // Save current clipboard, inject via Ctrl+V, then restore
        // Clipboard operations must happen on the UI (STA) thread
        string? previousClipboard = null;

        Dispatcher.Invoke(() =>
        {
            try { if (Clipboard.ContainsText()) previousClipboard = Clipboard.GetText(); }
            catch { }

            Clipboard.SetText(text);
        });

        Task.Run(async () =>
        {
            await Task.Delay(30); // Let Tab key release
            var simulator = new WindowsInput.InputSimulator();
            simulator.Keyboard.ModifiedKeyStroke(
                WindowsInput.Native.VirtualKeyCode.CONTROL,
                WindowsInput.Native.VirtualKeyCode.VK_V);

            // Restore previous clipboard after a brief delay
            if (previousClipboard != null)
            {
                await Task.Delay(100);
                Dispatcher.Invoke(() =>
                {
                    try { Clipboard.SetText(previousClipboard); }
                    catch { }
                });
            }
        });
    }

    // ==================== Helpers ====================

    private void LogToDebug(string message)
    {
        if (_testWindow != null)
        {
            Dispatcher.Invoke(() => _testWindow.Log(message));
        }
    }

    private void Log(string message)
    {
        File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("App exiting...");
        CancelPendingPrediction();
        _ocrTimer?.Dispose();
        _ocrService?.Dispose();
        _hookService?.Dispose();
        _trayIcon?.Dispose();
        _suggestionPanel?.Close();
        _debounceTimer?.Dispose();
        _fastDebounceTimer?.Dispose();
        base.OnExit(e);
    }

    private Icon CreateKeyboardIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);

        g.Clear(System.Drawing.Color.FromArgb(30, 30, 46));

        using var brush = new SolidBrush(System.Drawing.Color.FromArgb(200, 200, 220));
        using var smallBrush = new SolidBrush(System.Drawing.Color.FromArgb(30, 30, 46));

        g.FillRectangle(brush, 2, 8, 28, 16);

        for (int i = 0; i < 5; i++)
        {
            g.FillRectangle(smallBrush, 4 + i * 5, 10, 4, 4);
        }
        g.FillRectangle(smallBrush, 8, 18, 16, 3);

        var hIcon = bitmap.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
