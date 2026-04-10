using KeystrokeApp.Services;

namespace KeystrokeApp;

/// <summary>
/// Prediction pipeline â€” buffer events, debounce, streaming, OCR capture, and
/// alternative suggestion fetching. Split from App.xaml.cs as a partial class.
/// </summary>
public partial class App
{
    // ==================== Buffer Event Handlers ====================

    private void OnBufferChanged(string newText)
    {
        LogToDebug($"Buffer: \"{newText}\" ({newText.Length} chars)");
        TraceBufferChanged(newText);

        CancelPendingPrediction();
        _suggestionPanel?.HideSuggestion();
        ClearActiveSuggestion();

        _fastDebounceTimer?.Cancel();
        _debounceTimer?.Cancel();

        if (newText.Length > 0 && _wordBoundaryChars.Contains(newText[^1]))
            _debounceTimer?.Restart();
        else
            _fastDebounceTimer?.Restart();
    }

    private void OnBufferCleared()
    {
        CancelPendingPrediction();
        _suggestionPanel?.HideSuggestion();
        ClearActiveSuggestion();
        Interlocked.Exchange(ref _lastTracedBufferLength, 0);
        Interlocked.Exchange(ref _lastBufferTraceTicks, 0);

        LogToDebug("Buffer cleared");
        _reliabilityTrace.Trace("buffer", "cleared", "Typing buffer cleared.");
    }

    // ==================== Prediction ====================

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

        if (!TryGetEligibleActiveWindow(out var processName, out var windowTitle))
            return;

        var context = CreateContextSnapshot(buffer, processName, windowTitle);

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
                    if (_typingBuffer.CurrentText != buffer)
                        return;

                    _suggestionPanel?.ShowSuggestion(buffer, cached);
                    RegisterVisibleSuggestion(cacheRequestId, context, buffer, cached);
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
        _suggestionLifecycle.BeginPrediction(requestId, buffer);

        SetPredictionState("Predicting", requestId, new Dictionary<string, string>
        {
            ["bufferLength"] = buffer.Length.ToString()
        });

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsPredictionRequestCurrent(requestId))
                return;

            _suggestionPanel?.BeginStreamingSuggestion(buffer);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(_predictionEngine!.TimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                bool firstChunk = true;

                var completion = await _predictionEngine.PredictStreamingAsync(
                    context,
                    chunk =>
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        Dispatcher.BeginInvoke(() =>
                        {
                            if (!IsPredictionRequestCurrent(requestId))
                                return;
                            if (_typingBuffer.CurrentText != buffer)
                                return;

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

                _predictionCache.Put(buffer, completion);

                if (ct.IsCancellationRequested)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!IsPredictionRequestCurrent(requestId))
                            return;

                        _suggestionPanel?.HideSuggestion();
                    });
                    SetPredictionState("Cancelled", requestId);
                    return;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsPredictionRequestCurrent(requestId))
                        return;
                    if (_typingBuffer.CurrentText != buffer)
                        return;

                    _suggestionPanel?.OnStreamingComplete();
                    if (completion != null)
                        RegisterVisibleSuggestion(requestId, context, buffer, completion);
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

                    if (_config.MaxSuggestions > 1)
                        _ = FetchAlternativesAsync(context, buffer, requestId, ct);
                }
            }
            catch (OperationCanceledException)
            {
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

                if (IsPredictionRequestCurrent(requestId))
                {
                    _suggestionLifecycle.CompletePrediction(requestId);
                    SetPredictionState("Idle", requestId);
                }
            }
        }, ct);
    }

    // ==================== OCR ====================

    private void OnOcrTimerTick(object? state)
    {
        if (!_isEnabled || _ocrService == null)
            return;

        if (_ocrService.ShouldRefresh())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _ocrService.ReadScreenAsync();
                }
                catch (Exception ex)
                {
                    LogToDebug($"Screen read error: {ex.Message}");
                }
            });
        }
    }

    // ==================== Alternatives ====================

    private async Task FetchAlternativesAsync(ContextSnapshot context, string buffer, long requestId, CancellationToken ct)
    {
        try
        {
            if (_predictionEngine == null)
                return;

            var alternatives = await _predictionEngine.FetchAlternativesAsync(context, _config.MaxSuggestions, ct);
            if (ct.IsCancellationRequested || alternatives.Count == 0)
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (!IsPredictionRequestCurrent(requestId))
                    return;

                _suggestionPanel?.SetAlternatives(buffer, alternatives);
                _suggestionLifecycle.MarkAlternativesReady(requestId);
            });

            LogToDebug($"Loaded {alternatives.Count} alternatives for \"{buffer}\"");
            _reliabilityTrace.Trace("prediction", "alternatives_loaded", "Loaded alternative suggestions.", new Dictionary<string, string>
            {
                ["requestId"] = requestId.ToString(),
                ["count"] = alternatives.Count.ToString()
            });
        }
        catch (OperationCanceledException)
        {
        }
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
        _suggestionLifecycle.CancelPrediction();
    }

    private void TraceBufferChanged(string newText)
    {
        if (string.IsNullOrEmpty(newText))
            return;

        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastBufferTraceTicks);
        var lastLength = Interlocked.CompareExchange(ref _lastTracedBufferLength, 0, 0);

        bool shouldTrace =
            newText.Length <= 1 ||
            newText.Length < lastLength ||
            _wordBoundaryChars.Contains(newText[^1]) ||
            (nowTicks - lastTicks) >= TimeSpan.FromMilliseconds(250).Ticks;

        if (!shouldTrace)
            return;

        Interlocked.Exchange(ref _lastBufferTraceTicks, nowTicks);
        Interlocked.Exchange(ref _lastTracedBufferLength, newText.Length);

        _reliabilityTrace.Trace("buffer", "changed", "Typing buffer changed.", new Dictionary<string, string>
        {
            ["length"] = newText.Length.ToString()
        });
    }
}
