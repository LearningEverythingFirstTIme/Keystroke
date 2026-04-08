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

        _typingBuffer.AddChar(c);
        var currentBuffer = _typingBuffer.CurrentText;

        if (_config.LearningEnabled && _config.LearningV2Enabled)
        {
            var context = CreateContextSnapshot(currentBuffer, processName, windowTitle);
            _learningCaptureCoordinator.OnBufferChanged(currentBuffer, context);

            if (_commitBoundaryChars.Contains(c))
            {
                if (_learningCaptureCoordinator.OnManualCommit(currentBuffer, context, "punctuation") &&
                    _config.StyleProfileEnabled)
                {
                    _styleProfileService.OnAccepted();
                    _vocabularyProfileService.OnAccepted();
                }
            }
        }

        if (_debugWindow != null)
        {
            Dispatcher.BeginInvoke(() => _debugWindow?.Log($"Char: '{c}' -> Buffer: \"{currentBuffer}\""));
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

                    if (_config.LearningEnabled && _suggestionPanel?.HasSuggestion == true)
                    {
                        var fullSuggestion = _suggestionPanel.GetFullSuggestion();
                        var dismissed = SuggestionAcceptance.GetRemainingCompletion(oldBuffer, fullSuggestion);
                        if (!string.IsNullOrEmpty(dismissed))
                        {
                            _acceptanceTracker.LogDismissed(oldBuffer, dismissed, activeProcessName, activeWindowTitle);
                            var dismissSnapshot = SnapshotActiveSuggestion();
                            if (_config.LearningV2Enabled && dismissSnapshot.Context != null)
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

                    if (_config.LearningEnabled && _config.LearningV2Enabled &&
                        key == InputListenerService.SpecialKey.Enter &&
                        !string.IsNullOrWhiteSpace(oldBuffer) &&
                        _learningCaptureCoordinator.OnManualCommit(oldBuffer, context, "enter") &&
                        _config.StyleProfileEnabled)
                    {
                        _styleProfileService.OnAccepted();
                        _vocabularyProfileService.OnAccepted();
                    }

                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();
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

                    if (_config.LearningEnabled && _config.LearningV2Enabled &&
                        !string.IsNullOrWhiteSpace(oldBuffer) &&
                        _learningCaptureCoordinator.OnManualCommit(oldBuffer, context, "navigation") &&
                        _config.StyleProfileEnabled)
                    {
                        _styleProfileService.OnAccepted();
                        _vocabularyProfileService.OnAccepted();
                    }

                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();
                    ClearActiveSuggestion();
                    CancelPendingPrediction();
                    LogToDebug($"{key} -> Buffer cleared (cursor moved, was: \"{oldBuffer}\")");
                }
                break;

            case InputListenerService.SpecialKey.CtrlDownArrow:
                if (_suggestionPanel?.HasSuggestion == true)
                {
                    Interlocked.Increment(ref _cycleDepth);
                    var downSnapshot = SnapshotActiveSuggestion();
                    Dispatcher.BeginInvoke(() =>
                    {
                        _suggestionPanel?.NextSuggestion();
                        if (_suggestionPanel?.HasSuggestion == true && downSnapshot.Context != null)
                        {
                            RegisterVisibleSuggestion(
                                downSnapshot.RequestId,
                                CreateContextSnapshot(_typingBuffer.CurrentText, downSnapshot.Context.ProcessName, downSnapshot.Context.WindowTitle),
                                _typingBuffer.CurrentText,
                                _suggestionPanel.CurrentCompletion);
                        }
                    });
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Down -> Next suggestion");
                }
                break;

            case InputListenerService.SpecialKey.CtrlUpArrow:
                if (_suggestionPanel?.HasSuggestion == true)
                {
                    Interlocked.Increment(ref _cycleDepth);
                    var upSnapshot = SnapshotActiveSuggestion();
                    Dispatcher.BeginInvoke(() =>
                    {
                        _suggestionPanel?.PreviousSuggestion();
                        if (_suggestionPanel?.HasSuggestion == true && upSnapshot.Context != null)
                        {
                            RegisterVisibleSuggestion(
                                upSnapshot.RequestId,
                                CreateContextSnapshot(_typingBuffer.CurrentText, upSnapshot.Context.ProcessName, upSnapshot.Context.WindowTitle),
                                _typingBuffer.CurrentText,
                                _suggestionPanel.CurrentCompletion);
                        }
                    });
                    args.ShouldSwallow = true;
                    LogToDebug("Ctrl+Up -> Previous suggestion");
                }
                break;

            case InputListenerService.SpecialKey.ShiftTab:
                if (_suggestionPanel?.HasSuggestion == true)
                {
                    AcceptNextWord("Shift+Tab");
                    args.ShouldSwallow = true;
                }
                break;

            case InputListenerService.SpecialKey.CtrlRight:
                if (_suggestionPanel?.HasSuggestion == true)
                {
                    AcceptNextWord("Ctrl+Right");
                    args.ShouldSwallow = true;
                }
                break;

            case InputListenerService.SpecialKey.Tab:
                if (_suggestionPanel?.HasSuggestion == true)
                {
                    var buffer = _typingBuffer.CurrentText;
                    var fullText = _suggestionPanel.GetFullSuggestion();
                    var completion = SuggestionAcceptance.GetRemainingCompletion(buffer, fullText);
                    if (string.IsNullOrEmpty(completion))
                    {
                        _suggestionPanel.HideSuggestion();
                        ClearActiveSuggestion();
                        break;
                    }

                    LogToDebug($"Tab -> Buffer: \"{buffer}\" ({buffer.Length} chars)");
                    LogToDebug($"Tab -> Full suggestion: \"{fullText}\" ({fullText.Length} chars)");
                    LogToDebug($"Tab -> Injecting: \"{completion}\" ({completion.Length} chars)");

                    _ = InjectAcceptedTextAsync(completion, "full_accept");
                    _suggestionPanel.AcceptSuggestion();

                    Interlocked.Increment(ref _sessionAcceptCount);
                    UpdateTraySessionInfo();

                    var shownTicks = Interlocked.Exchange(ref _suggestionShownAtTicks, 0);
                    int latencyMs = shownTicks == 0 ? -1
                        : (int)Math.Min(
                            (DateTime.UtcNow.Ticks - shownTicks) / TimeSpan.TicksPerMillisecond,
                            int.MaxValue);
                    int cycleDepth = Interlocked.Exchange(ref _cycleDepth, 0);

                    var suggestionSnapshot = SnapshotActiveSuggestion();
                    var captureContext = suggestionSnapshot.Context ?? CreateContextSnapshot(buffer, activeProcessName, activeWindowTitle);

                    if (_config.LearningEnabled)
                    {
                        if (_config.StyleProfileEnabled)
                        {
                            _styleProfileService.OnAccepted();
                            _vocabularyProfileService.OnAccepted();
                        }

                        var capturedBuffer = buffer;
                        var capturedCompletion = completion;
                        var capturedProc = activeProcessName;
                        var capturedTitle = activeWindowTitle;
                        var capturedLatency = latencyMs;
                        var capturedDepth = cycleDepth;
                        var capturedSuggestionId = suggestionSnapshot.SuggestionId;
                        var capturedRequestId = suggestionSnapshot.RequestId;
                        var capturedContext = captureContext;

                        var initialQuality = CompletionFeedbackService.ComputeQualityScore(
                            capturedLatency, capturedDepth, editedAfter: false);

                        _postEditDetector.StartWatching(editedAfter =>
                        {
                            _acceptanceTracker.LogAccepted(
                                capturedBuffer,
                                capturedCompletion,
                                capturedProc,
                                capturedTitle,
                                capturedLatency,
                                capturedDepth,
                                editedAfter);

                            if (_config.LearningV2Enabled)
                            {
                                _learningCaptureCoordinator.OnFullAccept(
                                    capturedSuggestionId,
                                    capturedRequestId,
                                    capturedContext,
                                    capturedBuffer,
                                    capturedCompletion,
                                    capturedLatency,
                                    capturedDepth,
                                    editedAfter);
                            }

                            LogToDebug($"Tracked: latency={capturedLatency}ms cycle={capturedDepth} edited={editedAfter} quality={CompletionFeedbackService.ComputeQualityScore(capturedLatency, capturedDepth, editedAfter):F2}");
                        });

                        _learningService.AddToSession(
                            capturedBuffer,
                            capturedCompletion,
                            string.IsNullOrWhiteSpace(captureContext.SubcontextKey) ? captureContext.Category : captureContext.SubcontextKey,
                            initialQuality);
                        LogToDebug($"Session buffer updated ({captureContext.SubcontextLabel}, quality={initialQuality:F2})");
                    }

                    var fullAcceptedText = buffer + completion;
                    _rollingContext.AppendAccepted(fullAcceptedText, activeProcessName, activeWindowTitle);
                    LogToDebug($"Tab -> Rolling context updated (+{fullAcceptedText.Length} chars)");

                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();
                    ClearActiveSuggestion();
                    CancelPendingPrediction();

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

    private void AcceptNextWord(string keyLabel)
    {
        if (_suggestionPanel == null)
            return;

        var (procName, winTitle) = AppContextService.GetActiveWindow();
        if (!IsProcessEnabled(procName))
        {
            SuppressForFilteredApp(procName);
            return;
        }

        var buffer = _typingBuffer.CurrentText;
        var fullSugg = _suggestionPanel.GetFullSuggestion();
        var completion = SuggestionAcceptance.GetRemainingCompletion(buffer, fullSugg);
        if (string.IsNullOrEmpty(completion))
        {
            _suggestionPanel.HideSuggestion();
            ClearActiveSuggestion();
            return;
        }

        var nextWord = GetNextWord(completion);
        LogToDebug($"{keyLabel} -> Accepting next word: \"{nextWord}\" (remaining: \"{completion[nextWord.Length..]}\")");
        _ = InjectAcceptedTextAsync(nextWord, "word_accept");

        var wordSnapshot = SnapshotActiveSuggestion();
        var context = wordSnapshot.Context ?? CreateContextSnapshot(buffer, procName, winTitle);

        if (_config.LearningEnabled && _config.LearningV2Enabled)
        {
            _learningCaptureCoordinator.OnPartialAccept(
                wordSnapshot.SuggestionId,
                wordSnapshot.RequestId,
                context,
                buffer,
                completion,
                nextWord);
            if (_config.StyleProfileEnabled)
            {
                _styleProfileService.OnAccepted();
                _vocabularyProfileService.OnAccepted();
            }
        }

        var newBuffer = buffer + nextWord;
        _rollingContext.AppendAccepted(newBuffer, procName, winTitle);

        _typingBuffer.SetText(newBuffer);
        lock (_predictionCtsLock)
        {
            _lastPredictionPrefix = newBuffer;
        }

        CancelPendingPrediction();

        var remaining = completion[nextWord.Length..];
        if (string.IsNullOrWhiteSpace(remaining))
        {
            _suggestionPanel.HideSuggestion();
            ClearActiveSuggestion();
        }
        else
        {
            _suggestionPanel.ShowSuggestion(newBuffer, remaining);
            RegisterVisibleSuggestion(
                _activeSuggestionRequestId,
                CreateContextSnapshot(newBuffer, procName, winTitle),
                newBuffer,
                remaining);
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
}
