using KeystrokeApp.Services;

namespace KeystrokeApp;

/// <summary>
/// Prediction pipeline — buffer events, debounce, streaming, OCR capture, and
/// alternative suggestion fetching. Split from App.xaml.cs as a partial class.
/// </summary>
public partial class App
{
    // ==================== Buffer Event Handlers ====================

    private void OnBufferChanged(string newText)
    {
        LogToDebug($"Buffer: \"{newText}\" ({newText.Length} chars)");
        _reliabilityTrace.Trace("buffer", "changed", "Typing buffer changed.", new Dictionary<string, string>
        {
            ["length"] = newText.Length.ToString()
        });

        // Only cancel in-flight predictions if the user backspaced or the text diverged.
        // If they're just extending (typing forward), let the current prediction finish —
        // it may still be relevant, and cancelling it creates dead air.
        bool isExtending;
        lock (_predictionCtsLock)
        {
            isExtending = newText.Length > _lastPredictionPrefix.Length
                && newText.StartsWith(_lastPredictionPrefix);
        }

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
        _reliabilityTrace.Trace("buffer", "cleared", "Typing buffer cleared.");
    }

    // ==================== Prediction ====================

    /// <summary>
    /// Called after debounce pause - fetch a prediction from the engine.
    /// Fires on a background thread from DebounceTimer.
    /// </summary>
    private void OnDebounceComplete()
    {
        var buffer = _typingBuffer.CurrentText;
        var sanitizedTyped = _outboundPrivacy.SanitizeTypedText(buffer);

        if (buffer.Length < _config.MinBufferLength || sanitizedTyped.ShouldBlockPrediction)
        {
            if (buffer.Length < _config.MinBufferLength)
                TracePredictionSuppressed("Below minimum buffer length", buffer);
            else
                TracePredictionSuppressed("Sensitive input blocked", buffer);
            return;
        }

        // Check cache first — instant result for repeated/backspaced prefixes
        if (_predictionCache.TryGet(buffer, out var cached))
        {
            if (cached != null)
            {
                var cacheRequestId = NextPredictionRequestId();
                Interlocked.Exchange(ref _activePredictionRequestId, cacheRequestId);
                SetPredictionState("ShowingCachedSuggestion", cacheRequestId, new Dictionary<string, string>
                {
                    ["bufferLength"] = buffer.Length.ToString()
                });
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsPredictionRequestCurrent(cacheRequestId))
                        return;

                    // Reset cycle depth and stamp the suggestion-shown time so the Tab
                    // latency measurement is accurate even for cache hits.
                    Interlocked.Exchange(ref _cycleDepth, 0);
                    Interlocked.Exchange(ref _suggestionShownAtTicks, DateTime.UtcNow.Ticks);
                    _suggestionPanel?.ShowSuggestion(buffer, cached);

                });
                LogToDebug($"Cache hit: \"{buffer}\" + \"{cached}\"");
                _reliabilityTrace.Trace("prediction", "cache_hit", "Served prediction from cache.", new Dictionary<string, string>
                {
                    ["requestId"] = cacheRequestId.ToString(),
                    ["completionLength"] = cached.Length.ToString()
                });
            }
            return;
        }

        // If a prediction is already in-flight for a prefix the user is still extending,
        // let it finish rather than cancelling. Cloud models respond in <500ms so this
        // rarely matters there, but local models can take 3-8 seconds — without this,
        // every keystroke cancels the previous request and nothing ever completes.
        lock (_predictionCtsLock)
        {
            if (_predictionCts != null && buffer.StartsWith(_lastPredictionPrefix, StringComparison.Ordinal))
            {
                LogToDebug("Prediction in-flight for extending prefix, skipping restart");
                return;
            }
        }

        // Cancel any previous prediction request and track what we're predicting for
        CancellationToken ct;
        var requestId = NextPredictionRequestId();
        lock (_predictionCtsLock)
        {
            _predictionCts?.Cancel();
            _predictionCts?.Dispose();
            _predictionCts = new CancellationTokenSource();
            ct = _predictionCts.Token;
            _activePredictionRequestId = requestId;
            _lastPredictionPrefix = buffer;
        }
        SetPredictionState("Predicting", requestId, new Dictionary<string, string>
        {
            ["bufferLength"] = buffer.Length.ToString()
        });

        // Build context snapshot — app detection is instant, OCR uses cached result
        var (processName, windowTitle) = AppContextService.GetActiveWindow();
        var context = new ContextSnapshot
        {
            TypedText = sanitizedTyped.Text,
            ProcessName = processName,
            WindowTitle = windowTitle,
            SafeContextLabel = _outboundPrivacy.BuildSafeContextLabel(processName, windowTitle),
            ScreenText = _outboundPrivacy.SanitizeForPrompt(_ocrService?.CachedText),
            RollingContext = _config.RollingContextEnabled
                ? _outboundPrivacy.SanitizeForPrompt(_rollingContext.GetContext(processName, windowTitle))
                : null
        };

        // New prediction starting — clear the stale suggestion timestamp and cycle count
        // so they don't bleed into the latency measurement for the incoming suggestion.
        Interlocked.Exchange(ref _suggestionShownAtTicks, 0);
        Interlocked.Exchange(ref _cycleDepth, 0);

        // Show the loading animation immediately rather than waiting for the first streamed
        // chunk. This gives instant visual feedback that a new prediction is in progress and
        // clears any stale result that was anchored to an older prefix.
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsPredictionRequestCurrent(requestId))
                return;

            _suggestionPanel?.BeginStreamingSuggestion(buffer);
        });

        // Run prediction on background using streaming for progressive display
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(_predictionEngine!.TimeoutMs);
                using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                bool firstChunk = true;

                var completion = await _predictionEngine!.PredictStreamingAsync(
                    context,
                    chunk =>
                    {
                        if (ct.IsCancellationRequested) return;

                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!IsPredictionRequestCurrent(requestId))
                                return;

                            // Add leading space on the very first chunk if needed
                            if (firstChunk)
                            {
                                firstChunk = false;
                                if (!buffer.EndsWith(" ") && !chunk.StartsWith(" "))
                                    chunk = " " + chunk;
                            }
                            _suggestionPanel?.AppendSuggestion(buffer, chunk);

                        });
                    },
                    linkedCts.Token);

                // Cache the final result
                _predictionCache.Put(buffer, completion);

                if (ct.IsCancellationRequested)
                {
                    // Prediction was superseded by a newer keystroke — stop any loading animation.
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!IsPredictionRequestCurrent(requestId))
                            return;

                        _suggestionPanel?.HideSuggestion();
                    });
                    SetPredictionState("Cancelled", requestId);
                    return;
                }

                // Signal that streaming is complete — triggers the completion flash animation.
                // Also stamp the suggestion-shown time NOW (not when streaming started) because
                // the user cannot meaningfully accept until the full suggestion is visible.
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsPredictionRequestCurrent(requestId))
                        return;

                    Interlocked.Exchange(ref _suggestionShownAtTicks, DateTime.UtcNow.Ticks);
                    _suggestionPanel?.OnStreamingComplete();
                });
                SetPredictionState("ShowingSuggestion", requestId, new Dictionary<string, string>
                {
                    ["hasCompletion"] = (completion != null).ToString()
                });

                if (completion != null)
                {
                    var rollingInfo = context.HasRollingContext
                        ? $"[rolling={context.RollingContext!.Length} chars]"
                        : "[no rolling context]";
                    LogToDebug($"Streamed: \"{buffer}\" + \"{completion}\" [app={processName}] {rollingInfo}");

                    // Fire background request for alternatives (skip if user only wants 1 suggestion)
                    if (_config.MaxSuggestions > 1)
                        _ = FetchAlternativesAsync(context, buffer, requestId, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled by a newer keystroke — stop any in-progress loading animation.
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsPredictionRequestCurrent(requestId))
                        return;

                    _suggestionPanel?.HideSuggestion();
                });
                SetPredictionState("Cancelled", requestId);
            }
            catch (Exception ex)
            {
                LogToDebug($"Prediction error: {ex.Message}");
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsPredictionRequestCurrent(requestId))
                        return;

                    _suggestionPanel?.HideSuggestion();
                });
                SetPredictionState("Failed", requestId, new Dictionary<string, string>
                {
                    ["error"] = ex.Message
                });
            }
            finally
            {
                // Clear the CTS so the in-flight check above doesn't block the next prediction.
                lock (_predictionCtsLock)
                {
                    if (_predictionCts != null && _predictionCts.Token == ct)
                    {
                        _predictionCts.Dispose();
                        _predictionCts = null;
                        if (IsPredictionRequestCurrent(requestId))
                            Interlocked.CompareExchange(ref _activePredictionRequestId, 0, requestId);
                    }
                }

                // If the buffer advanced while this prediction was in-flight, immediately
                // kick off a fresh prediction for the current position. Without this, the
                // "don't restart if extending" guard holds off every debounce while a
                // prediction runs — leaving the user with a stale result and no follow-up
                // when they've typed ahead of the old prefix.
                if (!ct.IsCancellationRequested)
                {
                    var currentBuffer = _typingBuffer.CurrentText;
                    if (currentBuffer.Length > buffer.Length
                        && currentBuffer.StartsWith(buffer, StringComparison.Ordinal))
                    {
                        LogToDebug($"Buffer advanced (\"{buffer}\" → \"{currentBuffer}\"), re-triggering prediction");
                        _fastDebounceTimer?.Restart();
                    }
                }

                if (IsPredictionRequestCurrent(requestId))
                    SetPredictionState("Idle", requestId);
            }
        }, ct);
    }

    // ==================== OCR ====================

    /// <summary>
    /// Periodic screen read. Only re-reads when the active window has changed.
    /// </summary>
    private void OnOcrTimerTick(object? state)
    {
        if (!_isEnabled || _ocrService == null) return;

        if (_ocrService.ShouldRefresh())
        {
            _ = Task.Run(async () =>
            {
                try { await _ocrService.ReadScreenAsync(); }
                catch (Exception ex) { LogToDebug($"Screen read error: {ex.Message}"); }
            });
        }
    }

    // ==================== Alternatives ====================

    /// <summary>
    /// Fetch alternative suggestions in the background and push them to the panel.
    /// </summary>
    private async Task FetchAlternativesAsync(ContextSnapshot context, string buffer, long requestId, CancellationToken ct)
    {
        try
        {
            if (_predictionEngine == null) return;

            var alternatives = await _predictionEngine.FetchAlternativesAsync(context, _config.MaxSuggestions, ct);
            if (ct.IsCancellationRequested || alternatives.Count == 0) return;

            Dispatcher.BeginInvoke(() =>
            {
                if (!IsPredictionRequestCurrent(requestId))
                    return;

                _suggestionPanel?.SetAlternatives(buffer, alternatives);
            });
            LogToDebug($"Loaded {alternatives.Count} alternatives for \"{buffer}\"");
            _reliabilityTrace.Trace("prediction", "alternatives_loaded", "Loaded alternative suggestions.", new Dictionary<string, string>
            {
                ["requestId"] = requestId.ToString(),
                ["count"] = alternatives.Count.ToString()
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogToDebug($"Alternatives error: {ex.Message}");
            _reliabilityTrace.Trace("prediction", "alternatives_failed", "Failed to load alternatives.", new Dictionary<string, string>
            {
                ["requestId"] = requestId.ToString(),
                ["error"] = ex.Message
            });
        }
    }

    private void CancelPendingPrediction()
    {
        lock (_predictionCtsLock)
        {
            _predictionCts?.Cancel();
            _predictionCts?.Dispose();
            _predictionCts = null;
            Interlocked.Exchange(ref _activePredictionRequestId, 0);
        }
        _predictionState = "Idle";
    }
}
