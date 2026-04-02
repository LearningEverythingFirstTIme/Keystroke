using KeystrokeApp.Services;

namespace KeystrokeApp;

/// <summary>
/// Keyboard event handlers — character input, special keys, text injection, and
/// word-by-word suggestion acceptance. Split from App.xaml.cs as a partial class.
/// </summary>
public partial class App
{
    /// <summary>
    /// Characters that signal a word boundary — trigger prediction immediately.
    /// </summary>
    private static readonly HashSet<char> _wordBoundaryChars = [' ', '.', ',', '!', '?', ':', ';', ')', ']'];

    private void OnCharacterTyped(char c)
    {
        if (!_isEnabled) return;

        _typingBuffer.AddChar(c);

        if (_debugWindow != null)
        {
            Dispatcher.BeginInvoke(() => _debugWindow?.Log($"Char: '{c}' → Buffer: \"{_typingBuffer.CurrentText}\""));
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
                    // Notify the post-edit detector first — if this backspace fires within
                    // 1500ms of accepting a suggestion it means the user immediately corrected
                    // the injected text, which is a negative quality signal.
                    _postEditDetector.OnBackspace();
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
                        var fullSuggDismiss = _suggestionPanel.GetFullSuggestion();
                        if (oldBuffer.Length <= fullSuggDismiss.Length)
                        {
                            var (pn, wt) = ActiveWindowService.GetActiveWindow();
                            var dismissed = fullSuggDismiss.Substring(oldBuffer.Length);
                            _acceptanceTracker.LogDismissed(oldBuffer, dismissed, pn, wt);
                        }
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
                    Interlocked.Increment(ref _cycleDepth);
                    Dispatcher.BeginInvoke(() => _suggestionPanel?.NextSuggestion());
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Down → Next suggestion");
                }
                break;

            case KeyboardHookService.SpecialKey.CtrlUpArrow:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    Interlocked.Increment(ref _cycleDepth);
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
                    AcceptNextWord("Shift+Tab");
                    args.ShouldSwallow = true;
                }
                break;

            case KeyboardHookService.SpecialKey.CtrlRight:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    AcceptNextWord("Ctrl+Right");
                    args.ShouldSwallow = true;
                }
                break;

            case KeyboardHookService.SpecialKey.Tab:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    var buffer   = _typingBuffer.CurrentText;
                    var fullText = _suggestionPanel.GetFullSuggestion();

                    if (buffer.Length > fullText.Length) { _suggestionPanel.HideSuggestion(); break; }

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

                    // ── Sub-Phase A: capture interaction signals ───────────────
                    // Read latency (ms since suggestion became visible) and cycle depth
                    // atomically, then reset both for the next prediction.
                    var shownTicks = Interlocked.Exchange(ref _suggestionShownAtTicks, 0);
                    int latencyMs  = shownTicks == 0 ? -1
                        : (int)Math.Min(
                            (DateTime.UtcNow.Ticks - shownTicks) / TimeSpan.TicksPerMillisecond,
                            int.MaxValue);
                    int cycleDepth = Interlocked.Exchange(ref _cycleDepth, 0);

                    // Delay the disk write by the post-edit watch window (1500ms) so we can
                    // include editedAfter in the quality score. StyleProfileService is notified
                    // immediately — it only increments a counter, so timing doesn't matter.
                    var (procName, winTitle) = ActiveWindowService.GetActiveWindow();
                    if (_config.LearningEnabled)
                    {
                        if (_config.StyleProfileEnabled)
                        {
                            _styleProfileService.OnAccepted();
                            _vocabularyProfileService.OnAccepted();
                        }

                        // Capture locals for the closure — buffer/completion may change by the
                        // time the post-edit window expires.
                        var capturedBuffer     = buffer;
                        var capturedCompletion = completion;
                        var capturedProc       = procName;
                        var capturedTitle      = winTitle;
                        var capturedLatency    = latencyMs;
                        var capturedDepth      = cycleDepth;

                        _postEditDetector.StartWatching(editedAfter =>
                        {
                            _acceptanceTracker.LogAccepted(
                                capturedBuffer, capturedCompletion,
                                capturedProc,   capturedTitle,
                                capturedLatency, capturedDepth, editedAfter);

                            LogToDebug($"Tracked: latency={capturedLatency}ms " +
                                       $"cycle={capturedDepth} edited={editedAfter} " +
                                       $"quality={AcceptanceTracker.ComputeQualityScore(capturedLatency, capturedDepth, editedAfter):F2}");
                        });

                        // Sub-Phase C: feed the in-memory session buffer immediately so
                        // the next prediction can use this completion as context without
                        // waiting for the 5-second JSONL file refresh cycle.
                        var sessionCategory = Services.AppCategory
                            .GetEffectiveCategory(capturedProc, capturedTitle).ToString();
                        _learningService.AddToSession(capturedBuffer, capturedCompletion, sessionCategory);
                        LogToDebug($"Session buffer updated ({sessionCategory})");
                    }

                    // Update rolling context with the full accepted text (buffer + completion)
                    // This provides continuity for the next prediction
                    var fullAcceptedText = buffer + completion;
                    _rollingContext.AppendAccepted(fullAcceptedText, procName, winTitle);
                    LogToDebug($"Tab → Rolling context updated (+{fullAcceptedText.Length} chars)");

                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();
                    CancelPendingPrediction();

                    // Swallow the Tab key so it doesn't insert a tab character
                    args.ShouldSwallow = true;
                    LogToDebug("Tab → Key swallowed");
                }
                else
                {
                    LogToDebug("Tab pressed (no suggestion, passing through)");
                }
                break;
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

    // ==================== Word-by-word acceptance ====================

    /// <summary>
    /// Accepts the next word from the current suggestion. Injects it, updates the buffer,
    /// tracks acceptance, and shows the remaining completion (or hides if exhausted).
    /// Used by both Shift+Tab and Ctrl+Right, which behave identically.
    /// </summary>
    private void AcceptNextWord(string keyLabel)
    {
        if (_suggestionPanel == null) return;

        var buffer   = _typingBuffer.CurrentText;
        var fullSugg = _suggestionPanel.GetFullSuggestion();
        if (buffer.Length > fullSugg.Length) { _suggestionPanel.HideSuggestion(); return; }

        var completion = fullSugg.Substring(buffer.Length);
        var nextWord   = GetNextWord(completion);

        LogToDebug($"{keyLabel} → Accepting next word: \"{nextWord}\" (remaining: \"{completion[nextWord.Length..]}\")");

        InjectText(nextWord);

        var (procName, winTitle) = ActiveWindowService.GetActiveWindow();

        // Intentionally not logged to the learning system — a single accepted word
        // carries no meaningful signal about what the user wanted the completion to be.
        // Only full-suggestion accepts (Tab) are tracked.

        var newBuffer = buffer + nextWord;
        _rollingContext.AppendAccepted(newBuffer, procName, winTitle);

        // Update buffer silently (no event → no prediction debounce)
        _typingBuffer.SetText(newBuffer);
        _lastPredictionPrefix = newBuffer;
        CancelPendingPrediction();

        // Show the remaining completion, or hide if nothing left
        var remaining = completion[nextWord.Length..];
        if (string.IsNullOrWhiteSpace(remaining))
            _suggestionPanel.HideSuggestion();
        else
            _suggestionPanel.ShowSuggestion(newBuffer, remaining);
    }

    /// <summary>
    /// Extracts the first word (including leading space) from a completion string.
    /// e.g. " because I had" → " because"
    /// If the completion is a single word, returns it in full.
    /// </summary>
    private static string GetNextWord(string completion)
    {
        if (string.IsNullOrEmpty(completion)) return completion;
        int start    = completion[0] == ' ' ? 1 : 0;
        int spaceIdx = completion.IndexOf(' ', start);
        return spaceIdx < 0 ? completion : completion[..spaceIdx];
    }
}
