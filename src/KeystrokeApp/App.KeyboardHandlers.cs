using KeystrokeApp.Services;

namespace KeystrokeApp;

/// <summary>
/// Keyboard event handlers - character input, special keys, text injection, and
/// word-by-word suggestion acceptance. Split from App.xaml.cs as a partial class.
/// </summary>
public partial class App
{
    private static readonly HashSet<char> _wordBoundaryChars = [' ', '.', ',', '!', '?', ':', ';', ')', ']'];
    private static readonly HashSet<char> _commitBoundaryChars = ['.', '!', '?', ':', ';'];

    private enum AcceptanceMode
    {
        Full,
        NextWord
    }

    private sealed record AcceptancePreparation(
        string Buffer,
        string Completion,
        string AcceptedText,
        string ProcessName,
        string WindowTitle,
        string SuggestionId,
        long RequestId,
        ContextSnapshot Context);

    private void OnCharacterTyped(char c)
    {
        if (!_isEnabled)
            return;

        var (processName, windowTitle) = AppContextService.GetActiveWindow();
        if (!IsProcessEnabled(processName))
        {
            SuppressForFilteredApp(processName);
            return;
        }

        _postEditDetector.OnCharacterTyped(c);
        _typingBuffer.AddChar(c);
        var currentBuffer = _typingBuffer.CurrentText;

        if (IsPersonalizedLearningActive())
        {
            var context = CreateContextSnapshot(currentBuffer, processName, windowTitle);
            _learningCaptureCoordinator.OnBufferChanged(currentBuffer, context);

            if (_commitBoundaryChars.Contains(c))
            {
                if (_learningCaptureCoordinator.OnManualCommit(currentBuffer, context, "punctuation") &&
                    IsProfileLearningActive())
                {
                    _styleProfileService.OnAccepted();
                    _vocabularyProfileService.OnAccepted();
                }
            }
        }

    }

    private void OnSpecialKeyPressed(InputListenerService.SpecialKeyEventArgs args)
    {
        var key = args.Key;

        if (key == InputListenerService.SpecialKey.CtrlShiftK)
        {
            ToggleEnabled();
            args.ShouldSwallow = true;
            return;
        }

        if (!_isEnabled)
            return;

        var (activeProcessName, activeWindowTitle) = AppContextService.GetActiveWindow();
        if (!IsProcessEnabled(activeProcessName))
        {
            SuppressForFilteredApp(activeProcessName);
            return;
        }

        // Cache panel reference so the same instance is used for the check
        // and all subsequent access within this callback. Prevents a race
        // where the field is nulled between HasSuggestion and a follow-up call.
        var panel = _suggestionPanel;

        switch (key)
        {
            case InputListenerService.SpecialKey.Backspace:
                _postEditDetector.OnBackspace();
                _typingBuffer.RemoveLastChar();
                LogToDebug($"Backspace -> Buffer: \"{_typingBuffer.CurrentText}\"");
                break;

            case InputListenerService.SpecialKey.Enter:
            case InputListenerService.SpecialKey.Escape:
                {
                    var oldBuffer = _typingBuffer.CurrentText;
                    var context = CreateContextSnapshot(oldBuffer, activeProcessName, activeWindowTitle);

                    if (panel?.HasSuggestion == true)
                    {
                        var fullSuggestion = panel.GetFullSuggestion();
                        var dismissed = SuggestionAcceptance.GetRemainingCompletion(oldBuffer, fullSuggestion);
                        if (!string.IsNullOrEmpty(dismissed))
                        {
                            if (IsPersonalizedLearningActive())
                                _acceptanceTracker.LogDismissed(oldBuffer, dismissed, activeProcessName, activeWindowTitle);
                            var dismissSnapshot = SnapshotActiveSuggestion();
                            if (IsPersonalizedLearningActive() && dismissSnapshot.Context != null)
                            {
                                _learningCaptureCoordinator.OnDismiss(
                                    key == InputListenerService.SpecialKey.Enter ? "enter" : "escape",
                                    dismissSnapshot.SuggestionId,
                                    dismissSnapshot.RequestId,
                                    dismissSnapshot.Context,
                                    oldBuffer,
                                    dismissed);
                            }
                        }
                    }

                    if (IsPersonalizedLearningActive() &&
                        key == InputListenerService.SpecialKey.Enter &&
                        !string.IsNullOrWhiteSpace(oldBuffer) &&
                        _learningCaptureCoordinator.OnManualCommit(oldBuffer, context, "enter") &&
                        IsProfileLearningActive())
                    {
                        _styleProfileService.OnAccepted();
                        _vocabularyProfileService.OnAccepted();
                    }

                    _typingBuffer.Clear();
                    panel?.HideSuggestion();
                    ClearActiveSuggestion();
                    CancelPendingPrediction();
                    LogToDebug($"{key} -> Buffer cleared (was: \"{oldBuffer}\")");
                    break;
                }

            case InputListenerService.SpecialKey.LeftArrow:
            case InputListenerService.SpecialKey.RightArrow:
            case InputListenerService.SpecialKey.UpArrow:
            case InputListenerService.SpecialKey.DownArrow:
            case InputListenerService.SpecialKey.Home:
            case InputListenerService.SpecialKey.End:
            case InputListenerService.SpecialKey.PageUp:
            case InputListenerService.SpecialKey.PageDown:
            case InputListenerService.SpecialKey.Delete:
                if (!_typingBuffer.IsEmpty)
                {
                    var oldBuffer = _typingBuffer.CurrentText;
                    var context = CreateContextSnapshot(oldBuffer, activeProcessName, activeWindowTitle);

                    if (IsPersonalizedLearningActive() &&
                        !string.IsNullOrWhiteSpace(oldBuffer) &&
                        _learningCaptureCoordinator.OnManualCommit(oldBuffer, context, "navigation") &&
                        IsProfileLearningActive())
                    {
                        _styleProfileService.OnAccepted();
                        _vocabularyProfileService.OnAccepted();
                    }

                    _typingBuffer.Clear();
                    panel?.HideSuggestion();
                    ClearActiveSuggestion();
                    CancelPendingPrediction();
                    LogToDebug($"{key} -> Buffer cleared (cursor moved, was: \"{oldBuffer}\")");
                }
                break;

            case InputListenerService.SpecialKey.CtrlDownArrow:
                if (panel?.HasSuggestion == true)
                {
                    _suggestionLifecycle.IncrementCycleDepth();
                    var downSnapshot = SnapshotActiveSuggestion();
                    Dispatcher.BeginInvoke(() =>
                    {
                        panel.NextSuggestion();
                        if (panel.HasSuggestion && downSnapshot.Context != null)
                        {
                            RegisterVisibleSuggestion(
                                downSnapshot.RequestId,
                                CreateContextSnapshot(_typingBuffer.CurrentText, downSnapshot.Context.ProcessName, downSnapshot.Context.WindowTitle),
                                _typingBuffer.CurrentText,
                                panel.CurrentCompletion);
                        }
                    });
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Down -> Next suggestion");
                }
                break;

            case InputListenerService.SpecialKey.CtrlUpArrow:
                if (panel?.HasSuggestion == true)
                {
                    _suggestionLifecycle.IncrementCycleDepth();
                    var upSnapshot = SnapshotActiveSuggestion();
                    Dispatcher.BeginInvoke(() =>
                    {
                        panel.PreviousSuggestion();
                        if (panel.HasSuggestion && upSnapshot.Context != null)
                        {
                            RegisterVisibleSuggestion(
                                upSnapshot.RequestId,
                                CreateContextSnapshot(_typingBuffer.CurrentText, upSnapshot.Context.ProcessName, upSnapshot.Context.WindowTitle),
                                _typingBuffer.CurrentText,
                                panel.CurrentCompletion);
                        }
                    });
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Up -> Previous suggestion");
                }
                break;

            case InputListenerService.SpecialKey.ShiftTab:
                if (panel?.HasSuggestion == true)
                {
                    _ = AcceptSuggestionAsync(AcceptanceMode.NextWord, "Shift+Tab", "word_accept");
                    args.ShouldSwallow = true;
                }
                break;

            case InputListenerService.SpecialKey.CtrlRight:
                if (panel?.HasSuggestion == true)
                {
                    _ = AcceptSuggestionAsync(AcceptanceMode.NextWord, "Ctrl+Right", "word_accept");
                    args.ShouldSwallow = true;
                }
                break;

            case InputListenerService.SpecialKey.Tab:
                if (panel?.HasSuggestion == true)
                {
                    _ = AcceptSuggestionAsync(AcceptanceMode.Full, "Tab", "full_accept");
                    args.ShouldSwallow = true;
                    LogToDebug("Tab -> Key swallowed");
                }
                else
                {
                    LogToDebug("Tab pressed (no suggestion, passing through)");
                }
                break;
        }
    }

    // ==================== Text Injection ====================

    private async Task<TextInjectionResult> InjectAcceptedTextAsync(string text, string source)
    {
        try
        {
            var result = await _textInjector.InjectAsync(text);
            var data = new Dictionary<string, string>
            {
                ["source"] = source,
                ["outcome"] = result.Outcome.ToString(),
                ["method"] = result.Method.ToString(),
                ["restoreAttempted"] = result.ClipboardRestoreAttempted.ToString(),
                ["restoreSucceeded"] = result.ClipboardRestoreSucceeded.ToString(),
                ["clipboardChangedExternally"] = result.ClipboardChangedExternally.ToString()
            };

            if (!string.IsNullOrWhiteSpace(result.FailureReason))
                data["failureReason"] = result.FailureReason;

            _reliabilityTrace.Trace(
                "injection",
                result.DeliveredToTarget ? "completed" : "failed",
                $"Accepted text injection {(result.DeliveredToTarget ? "completed" : "failed")} via {result.Method}.",
                data);

            if (result.Method == TextInjectionMethod.SendInputFallback)
            {
                LogToDebug("WARNING: Clipboard injection fell back to SendInput. " +
                           "Injected keystrokes bypass InputListenerService (LLKHF_INJECTED filtered). " +
                           $"Text \"{text}\" was sent to target app but is invisible to the typing buffer.");
                _reliabilityTrace.Trace("injection", "sendinput_warning",
                    "SendInput fallback used - injected keystrokes are filtered by the input hook. " +
                    "Text reaches the target app but bypasses Keystroke's buffer tracking.",
                    new Dictionary<string, string>
                    {
                        ["textLength"] = text.Length.ToString(),
                        ["source"] = source
                    });
            }

            ReportAcceptanceStatus(result, source);
            return result;
        }
        catch (Exception ex)
        {
            _reliabilityTrace.Trace("injection", "failed", "Accepted text injection crashed.", new Dictionary<string, string>
            {
                ["source"] = source,
                ["error"] = ex.Message
            });
            LogToDebug($"Injection failed: {ex.Message}");
            var failed = TextInjectionResult.Failed(TextInjectionMethod.ClipboardPaste, ex.Message);
            ReportAcceptanceStatus(failed, source);
            return failed;
        }
    }

    // ==================== Word-by-word acceptance ====================

    // Bounded timeout for the acceptance gate. A single Tab-press injection should
    // complete in tens of milliseconds; 500 ms is a large safety margin for clipboard
    // contention. Past this, something has gone wrong — better to drop the press than
    // queue duplicate injections.
    private static readonly TimeSpan AcceptanceGateTimeout = TimeSpan.FromMilliseconds(500);

    private async Task AcceptSuggestionAsync(AcceptanceMode mode, string triggerLabel, string source)
    {
        // Single-flight: if a prior acceptance is still running, a second Tab-press
        // within the gate timeout is coalesced away rather than queued. Rapid double-taps
        // (muscle memory, sticky keys) must not inject duplicates.
        if (!await _acceptanceGate.WaitAsync(AcceptanceGateTimeout).ConfigureAwait(true))
        {
            LogToDebug($"{triggerLabel} -> Dropped: acceptance gate still held after {AcceptanceGateTimeout.TotalMilliseconds:F0}ms.");
            _reliabilityTrace.Trace(
                area: "acceptance",
                eventName: "gate_dropped",
                message: "Acceptance gate timeout — dropping this press to avoid duplicate injection.",
                data: new Dictionary<string, string>
                {
                    ["source"] = source,
                    ["trigger"] = triggerLabel,
                    ["timeout_ms"] = ((int)AcceptanceGateTimeout.TotalMilliseconds).ToString()
                });
            return;
        }
        try
        {
            var preparation = PrepareAcceptance(mode);
            if (preparation == null)
                return;

            LogToDebug($"{triggerLabel} -> Injecting: \"{preparation.AcceptedText}\" ({preparation.AcceptedText.Length} chars)");
            var result = await InjectAcceptedTextAsync(preparation.AcceptedText, source);
            if (!result.DeliveredToTarget)
                return;

            RecordAcceptedSuggestionUsage(preparation.SuggestionId);

            if (mode == AcceptanceMode.Full)
                ApplyFullAcceptance(preparation);
            else
                ApplyPartialAcceptance(triggerLabel, preparation);
        }
        finally
        {
            _acceptanceGate.Release();
        }
    }

    private static string GetNextWord(string completion)
    {
        if (string.IsNullOrEmpty(completion))
            return completion;

        int start = completion[0] == ' ' ? 1 : 0;
        int spaceIdx = completion.IndexOf(' ', start);
        return spaceIdx < 0 ? completion : completion[..spaceIdx];
    }

    private AcceptancePreparation? PrepareAcceptance(AcceptanceMode mode)
    {
        if (_suggestionPanel == null)
            return null;

        var (processName, windowTitle) = AppContextService.GetActiveWindow();
        if (!IsProcessEnabled(processName))
        {
            SuppressForFilteredApp(processName);
            return null;
        }

        var buffer = _typingBuffer.CurrentText;
        var fullSuggestion = _suggestionPanel.GetFullSuggestion();
        var completion = SuggestionAcceptance.GetRemainingCompletion(buffer, fullSuggestion);
        if (string.IsNullOrEmpty(completion))
        {
            _suggestionPanel.HideSuggestion();
            ClearActiveSuggestion();
            return null;
        }

        var acceptedText = mode == AcceptanceMode.NextWord
            ? GetNextWord(completion)
            : completion;
        var snapshot = SnapshotActiveSuggestion();
        var context = snapshot.Context ?? CreateContextSnapshot(buffer, processName, windowTitle);

        return new AcceptancePreparation(
            buffer,
            completion,
            acceptedText,
            processName,
            windowTitle,
            snapshot.SuggestionId,
            snapshot.RequestId,
            context);
    }

    private void ApplyFullAcceptance(AcceptancePreparation preparation)
    {
        _suggestionPanel?.AcceptSuggestion();

        int latencyMs = GetSuggestionLatencyMs();
        int cycleDepth = _suggestionLifecycle.Snapshot().CycleDepth;

        if (IsProfileLearningActive())
        {
            _styleProfileService.OnAccepted();
            _vocabularyProfileService.OnAccepted();
            _contextAdaptiveSettingsService.OnAccepted();
        }

        var initialQuality = CompletionFeedbackService.ComputeQualityScore(
            latencyMs,
            cycleDepth,
            editedAfter: false);

        _postEditDetector.StartWatching(correction =>
        {
            bool editedAfter = correction.EditDetected;

            if (IsPersonalizedLearningActive())
            {
                _acceptanceTracker.LogAccepted(
                    preparation.Buffer,
                    preparation.Completion,
                    preparation.ProcessName,
                    preparation.WindowTitle,
                    latencyMs,
                    cycleDepth,
                    editedAfter);
            }

            if (IsPersonalizedLearningActive())
            {
                _learningCaptureCoordinator.OnFullAccept(
                    preparation.SuggestionId,
                    preparation.RequestId,
                    preparation.Context,
                    preparation.Buffer,
                    preparation.Completion,
                    latencyMs,
                    cycleDepth,
                    editedAfter,
                    correction);

                if (correction.HasCorrection && IsProfileLearningActive())
                    _correctionPatternService.OnCorrectionDetected();
            }

            LogToDebug($"Tracked: latency={latencyMs}ms cycle={cycleDepth} edited={editedAfter}" +
                       $" correction={correction.CorrectionType()}" +
                       $" quality={CompletionFeedbackService.ComputeQualityScore(latencyMs, cycleDepth, editedAfter):F2}");
        });

        if (IsPersonalizedLearningActive())
        {
            _learningService.AddToSession(
                preparation.Buffer,
                preparation.Completion,
                string.IsNullOrWhiteSpace(preparation.Context.SubcontextKey)
                    ? preparation.Context.Category
                    : preparation.Context.SubcontextKey,
                initialQuality);
            LogToDebug($"Session buffer updated ({preparation.Context.SubcontextLabel}, quality={initialQuality:F2})");
        }

        var fullAcceptedText = preparation.Buffer + preparation.Completion;
        _rollingContext.AppendAccepted(fullAcceptedText, preparation.ProcessName, preparation.WindowTitle);
        LogToDebug($"Tab -> Rolling context updated (+{fullAcceptedText.Length} chars)");

        _typingBuffer.Clear();
        _suggestionPanel?.HideSuggestion();
        ClearActiveSuggestion();
        CancelPendingPrediction();
    }

    private void ApplyPartialAcceptance(string triggerLabel, AcceptancePreparation preparation)
    {
        LogToDebug($"{triggerLabel} -> Accepting next word: \"{preparation.AcceptedText}\"");

        if (IsPersonalizedLearningActive())
        {
            _learningCaptureCoordinator.OnPartialAccept(
                preparation.SuggestionId,
                preparation.RequestId,
                preparation.Context,
                preparation.Buffer,
                preparation.Completion,
                preparation.AcceptedText);

            if (IsProfileLearningActive())
            {
                _styleProfileService.OnAccepted();
                _vocabularyProfileService.OnAccepted();
            }
        }

        var newBuffer = preparation.Buffer + preparation.AcceptedText;
        _rollingContext.AppendAccepted(newBuffer, preparation.ProcessName, preparation.WindowTitle);

        _typingBuffer.SetText(newBuffer);
        lock (_predictionCtsLock)
        {
            _lastPredictionPrefix = newBuffer;
        }

        CancelPendingPrediction();

        var remaining = preparation.Completion[preparation.AcceptedText.Length..];
        if (string.IsNullOrWhiteSpace(remaining))
        {
            _suggestionPanel?.HideSuggestion();
            ClearActiveSuggestion();
            return;
        }

        _suggestionPanel?.ShowSuggestion(newBuffer, remaining);
        RegisterVisibleSuggestion(
            preparation.RequestId,
            CreateContextSnapshot(newBuffer, preparation.ProcessName, preparation.WindowTitle),
            newBuffer,
            remaining,
            preparation.SuggestionId);
    }
}
