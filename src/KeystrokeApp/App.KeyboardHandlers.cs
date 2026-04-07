using KeystrokeApp.Services;

namespace KeystrokeApp;

/// <summary>
/// Keyboard event handlers â€” character input, special keys, text injection, and
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

        _typingBuffer.AddChar(c);
        var currentBuffer = _typingBuffer.CurrentText;

        if (_config.LearningEnabled && _config.LearningV2Enabled)
        {
            var (processName, windowTitle) = AppContextService.GetActiveWindow();
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
            Dispatcher.BeginInvoke(() => _debugWindow?.Log($"Char: '{c}' â†’ Buffer: \"{currentBuffer}\""));
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
                    _postEditDetector.OnBackspace();
                    _typingBuffer.RemoveLastChar();
                    LogToDebug($"Backspace â†’ Buffer: \"{_typingBuffer.CurrentText}\"");
                }
                break;

            case InputListenerService.SpecialKey.Enter:
            case InputListenerService.SpecialKey.Escape:
                if (_isEnabled)
                {
                    var oldBuffer = _typingBuffer.CurrentText;
                    var (pn, wt) = AppContextService.GetActiveWindow();
                    var context = CreateContextSnapshot(oldBuffer, pn, wt);

                    if (_config.LearningEnabled && _suggestionPanel?.HasSuggestion == true)
                    {
                        var fullSuggestion = _suggestionPanel.GetFullSuggestion();
                        var dismissed = SuggestionAcceptance.GetRemainingCompletion(oldBuffer, fullSuggestion);
                        if (!string.IsNullOrEmpty(dismissed))
                        {
                            _acceptanceTracker.LogDismissed(oldBuffer, dismissed, pn, wt);
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
                    LogToDebug($"{key} â†’ Buffer cleared (was: \"{oldBuffer}\")");
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
                    var (pn, wt) = AppContextService.GetActiveWindow();
                    var context = CreateContextSnapshot(oldBuffer, pn, wt);

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
                    LogToDebug($"{key} â†’ Buffer cleared (cursor moved, was: \"{oldBuffer}\")");
                }
                break;

            case InputListenerService.SpecialKey.CtrlDownArrow:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
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
                    LogToDebug("Ctrl+Down â†’ Next suggestion");
                }
                break;

            case InputListenerService.SpecialKey.CtrlUpArrow:
                if (_isEnabled && _suggestionPanel?.HasSuggestion == true)
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
                    LogToDebug("Ctrl+Up â†’ Previous suggestion");
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
                    var buffer = _typingBuffer.CurrentText;
                    var fullText = _suggestionPanel.GetFullSuggestion();
                    var completion = SuggestionAcceptance.GetRemainingCompletion(buffer, fullText);
                    if (string.IsNullOrEmpty(completion))
                    {
                        _suggestionPanel.HideSuggestion();
                        ClearActiveSuggestion();
                        break;
                    }

                    LogToDebug($"Tab â†’ Buffer: \"{buffer}\" ({buffer.Length} chars)");
                    LogToDebug($"Tab â†’ Full suggestion: \"{fullText}\" ({fullText.Length} chars)");
                    LogToDebug($"Tab â†’ Injecting: \"{completion}\" ({completion.Length} chars)");

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

                    var (procName, winTitle) = AppContextService.GetActiveWindow();
                    var suggestionSnapshot = SnapshotActiveSuggestion();
                    var captureContext = suggestionSnapshot.Context ?? CreateContextSnapshot(buffer, procName, winTitle);

                    if (_config.LearningEnabled)
                    {
                        if (_config.StyleProfileEnabled)
                        {
                            _styleProfileService.OnAccepted();
                            _vocabularyProfileService.OnAccepted();
                        }

                        var capturedBuffer = buffer;
                        var capturedCompletion = completion;
                        var capturedProc = procName;
                        var capturedTitle = winTitle;
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
                    _rollingContext.AppendAccepted(fullAcceptedText, procName, winTitle);
                    LogToDebug($"Tab â†’ Rolling context updated (+{fullAcceptedText.Length} chars)");

                    _typingBuffer.Clear();
                    _suggestionPanel?.HideSuggestion();
                    ClearActiveSuggestion();
                    CancelPendingPrediction();

                    args.ShouldSwallow = true;
                    LogToDebug("Tab â†’ Key swallowed");
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
                    "SendInput fallback used â€” injected keystrokes are filtered by the input hook. " +
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
        LogToDebug($"{keyLabel} â†’ Accepting next word: \"{nextWord}\" (remaining: \"{completion[nextWord.Length..]}\")");
        _ = InjectAcceptedTextAsync(nextWord, "word_accept");

        var (procName, winTitle) = AppContextService.GetActiveWindow();
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
