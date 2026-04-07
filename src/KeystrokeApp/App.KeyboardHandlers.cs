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

    private void OnSpecialKeyPressed(InputListenerService.SpecialKeyEventArgs args)
    {
        var key = args.Key;

        switch (key)
        {
            case InputListenerService.SpecialKey.Backspace:
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

            case InputListenerService.SpecialKey.Enter:
            case InputListenerService.SpecialKey.Escape:
                if (_isEnabled)
                {
                    var oldBuffer = _typingBuffer.CurrentText;

                    // Track dismissal if a suggestion was showing
                    if (_config.LearningEnabled && _suggestionPanel?.HasSuggestion == true)
                    {
                        var fullSuggDismiss = _suggestionPanel.GetFullSuggestion();
                        var dismissed = SuggestionAcceptance.GetRemainingCompletion(oldBuffer, fullSuggDismiss);
                        if (!string.IsNullOrEmpty(dismissed))
                        {
                            var (pn, wt) = AppContextService.GetActiveWindow();
                            _acceptanceTracker.LogDismissed(oldBuffer, dismissed, pn, wt);
                        }
                    }

                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();

                    CancelPendingPrediction();
                    LogToDebug($"{key} → Buffer cleared (was: \"{oldBuffer}\")");
                }
                break;

            case InputListenerService.SpecialKey.LeftArrow:
            case InputListenerService.SpecialKey.RightArrow:
            case InputListenerService.SpecialKey.UpArrow:
            case InputListenerService.SpecialKey.DownArrow:
            case InputListenerService.SpecialKey.Home:
            case InputListenerService.SpecialKey.End:
            case InputListenerService.SpecialKey.PageUp:
            case InputListenerService.SpecialKey.PageDown:
            case InputListenerService.SpecialKey.Delete:
                if (_isEnabled && !_typingBuffer.IsEmpty)
                {
                    var oldBuffer = _typingBuffer.CurrentText;
                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();

                    CancelPendingPrediction();
                    LogToDebug($"{key} → Buffer cleared (cursor moved, was: \"{oldBuffer}\")");
                }
                break;

            case InputListenerService.SpecialKey.CtrlDownArrow:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    Interlocked.Increment(ref _cycleDepth);
                    Dispatcher.BeginInvoke(() => _suggestionPanel?.NextSuggestion());
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Down → Next suggestion");
                }
                break;

            case InputListenerService.SpecialKey.CtrlUpArrow:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    Interlocked.Increment(ref _cycleDepth);
                    Dispatcher.BeginInvoke(() => _suggestionPanel?.PreviousSuggestion());
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Up → Previous suggestion");
                }
                break;

            case InputListenerService.SpecialKey.CtrlShiftK:
                ToggleEnabled();
                args.ShouldSwallow = true;
                break;

            case InputListenerService.SpecialKey.ShiftTab:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    AcceptNextWord("Shift+Tab");
                    args.ShouldSwallow = true;
                }
                break;

            case InputListenerService.SpecialKey.CtrlRight:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    AcceptNextWord("Ctrl+Right");
                    args.ShouldSwallow = true;
                }
                break;

            case InputListenerService.SpecialKey.Tab:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
                {
                    var buffer   = _typingBuffer.CurrentText;
                    var fullText = _suggestionPanel.GetFullSuggestion();

                    LogToDebug($"Tab → Buffer: \"{buffer}\" ({buffer.Length} chars)");
                    LogToDebug($"Tab → Buffer ends with space: {buffer.EndsWith(" ")}");
                    LogToDebug($"Tab → Full suggestion: \"{fullText}\" ({fullText.Length} chars)");

                    var completion = SuggestionAcceptance.GetRemainingCompletion(buffer, fullText);
                    if (string.IsNullOrEmpty(completion)) { _suggestionPanel.HideSuggestion(); break; }
                    LogToDebug($"Tab → Injecting: \"{completion}\" ({completion.Length} chars)");
                    LogToDebug($"Tab → First char code: {(int)completion.FirstOrDefault()}");

                    _ = InjectAcceptedTextAsync(completion, "full_accept");

                    // Show confirmation flash
                    _suggestionPanel?.AcceptSuggestion();

                    Interlocked.Increment(ref _sessionAcceptCount);
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
                    var (procName, winTitle) = AppContextService.GetActiveWindow();
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

                        // Compute an initial quality score without post-edit signal.
                        // This lets us gate the session buffer immediately (before the
                        // 1500ms post-edit window completes). The full quality score
                        // (with editedAfter) still goes to the tracker on disk.
                        var initialQuality = CompletionFeedbackService.ComputeQualityScore(
                            capturedLatency, capturedDepth, editedAfter: false);

                        _postEditDetector.StartWatching(editedAfter =>
                        {
                            _acceptanceTracker.LogAccepted(
                                capturedBuffer, capturedCompletion,
                                capturedProc,   capturedTitle,
                                capturedLatency, capturedDepth, editedAfter);

                            LogToDebug($"Tracked: latency={capturedLatency}ms " +
                                       $"cycle={capturedDepth} edited={editedAfter} " +
                                       $"quality={CompletionFeedbackService.ComputeQualityScore(capturedLatency, capturedDepth, editedAfter):F2}");
                        });

                        // Sub-Phase C: feed the in-memory session buffer immediately so
                        // the next prediction can use this completion as context without
                        // waiting for the 5-second JSONL file refresh cycle.
                        // Quality-gated: only high-confidence accepts enter the buffer.
                        var sessionCategory = Services.AppCategory
                            .GetEffectiveCategory(capturedProc, capturedTitle).ToString();
                        _learningService.AddToSession(capturedBuffer, capturedCompletion, sessionCategory, initialQuality);
                        LogToDebug($"Session buffer updated ({sessionCategory}, quality={initialQuality:F2})");
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

    private async Task InjectAcceptedTextAsync(string text, string source)
    {
        try
        {
            var result = await _textInjector.InjectAsync(text);
            var data = new Dictionary<string, string>
            {
                ["source"] = source,
                ["method"] = result.Method.ToString(),
                ["restoreAttempted"] = result.ClipboardRestoreAttempted.ToString(),
                ["restoreSucceeded"] = result.ClipboardRestoreSucceeded.ToString(),
                ["clipboardChangedExternally"] = result.ClipboardChangedExternally.ToString()
            };

            if (!string.IsNullOrWhiteSpace(result.FailureReason))
                data["failureReason"] = result.FailureReason;

            _reliabilityTrace.Trace(
                "injection",
                result.Success ? "completed" : "failed",
                $"Accepted text injection {(result.Success ? "completed" : "failed")} via {result.Method}.",
                data);

            if (result.Method == TextInjectionMethod.SendInputFallback)
            {
                // SendInput produces LLKHF_INJECTED keystrokes which are filtered by
                // InputListenerService (correctly — to prevent hook re-entry). The
                // keystrokes still reach the target app via CallNextHookEx, but our
                // typing buffer never sees them. This is fine because the buffer was
                // already cleared by the Tab/word-accept handler before this runs.
                // However, if future code depends on the buffer reflecting injected
                // text, this will be a problem. Log prominently so it's not a mystery.
                LogToDebug("WARNING: Clipboard injection fell back to SendInput. " +
                           "Injected keystrokes bypass InputListenerService (LLKHF_INJECTED filtered). " +
                           $"Text \"{text}\" was sent to target app but is invisible to the typing buffer.");
                _reliabilityTrace.Trace("injection", "sendinput_warning",
                    "SendInput fallback used — injected keystrokes are filtered by the input hook. " +
                    "Text reaches the target app but bypasses Keystroke's buffer tracking.",
                    new Dictionary<string, string>
                    {
                        ["textLength"] = text.Length.ToString(),
                        ["source"] = source
                    });
            }
        }
        catch (OperationCanceledException)
        {
            _reliabilityTrace.Trace("injection", "cancelled", "Accepted text injection cancelled.", new Dictionary<string, string>
            {
                ["source"] = source
            });
        }
        catch (Exception ex)
        {
            _reliabilityTrace.Trace("injection", "failed", "Accepted text injection crashed.", new Dictionary<string, string>
            {
                ["source"] = source,
                ["error"] = ex.Message
            });
            LogToDebug($"Injection failed: {ex.Message}");
        }
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
        var completion = SuggestionAcceptance.GetRemainingCompletion(buffer, fullSugg);
        if (string.IsNullOrEmpty(completion)) { _suggestionPanel.HideSuggestion(); return; }
        var nextWord   = GetNextWord(completion);

        LogToDebug($"{keyLabel} → Accepting next word: \"{nextWord}\" (remaining: \"{completion[nextWord.Length..]}\")");

        _ = InjectAcceptedTextAsync(nextWord, "word_accept");

        var (procName, winTitle) = AppContextService.GetActiveWindow();

        // Intentionally not logged to the learning system — a single accepted word
        // carries no meaningful signal about what the user wanted the completion to be.
        // Only full-suggestion accepts (Tab) are tracked.

        var newBuffer = buffer + nextWord;
        _rollingContext.AppendAccepted(newBuffer, procName, winTitle);

        // Update buffer silently (no event → no prediction debounce)
        _typingBuffer.SetText(newBuffer);
        lock (_predictionCtsLock)
        {
            _lastPredictionPrefix = newBuffer;
        }
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
