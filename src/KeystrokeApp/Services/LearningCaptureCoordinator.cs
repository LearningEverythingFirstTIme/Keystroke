namespace KeystrokeApp.Services;

public sealed class LearningCaptureCoordinator
{
    private readonly LearningEventService _eventService;
    private readonly string _sessionId = Guid.NewGuid().ToString("n");
    private readonly object _lock = new();
    private PendingSuggestion? _pendingSuggestion;

    public LearningCaptureCoordinator(LearningEventService eventService)
    {
        _eventService = eventService;
    }

    public string SessionId => _sessionId;

    public void OnSuggestionShown(string suggestionId, long requestId, ContextSnapshot context, string prefix, string completion)
    {
        if (string.IsNullOrWhiteSpace(completion))
            return;

        lock (_lock)
        {
            _pendingSuggestion = new PendingSuggestion
            {
                SuggestionId = suggestionId,
                RequestId = requestId,
                Prefix = prefix,
                Completion = completion,
                ProcessKey = context.ProcessKey,
                WindowKey = context.WindowKey,
                SubcontextKey = context.SubcontextKey,
                Context = context
            };
        }

        _eventService.Append(CreateRecord(
            "suggestion_shown",
            context,
            suggestionId,
            requestId,
            prefix,
            completion,
            acceptedText: "",
            userWrittenText: "",
            sourceWeight: 0.2f,
            confidence: context.ContextConfidence));
    }

    public void OnPartialAccept(string suggestionId, long requestId, ContextSnapshot context, string prefix, string shownCompletion, string acceptedText)
    {
        if (string.IsNullOrWhiteSpace(acceptedText))
            return;

        _eventService.Append(CreateRecord(
            "suggestion_partial_accept",
            context,
            suggestionId,
            requestId,
            prefix,
            shownCompletion,
            acceptedText,
            acceptedText,
            sourceWeight: 0.4f,
            confidence: context.ContextConfidence));
    }

    public void OnFullAccept(
        string suggestionId,
        long requestId,
        ContextSnapshot context,
        string prefix,
        string completion,
        int latencyMs,
        int cycleDepth,
        bool editedAfter)
    {
        var quality = CompletionFeedbackService.ComputeQualityScore(latencyMs, cycleDepth, editedAfter);

        _eventService.Append(CreateRecord(
            "suggestion_full_accept",
            context,
            suggestionId,
            requestId,
            prefix,
            completion,
            completion,
            completion,
            latencyMs,
            cycleDepth,
            editedAfter,
            untouchedForMs: editedAfter ? 0 : 1500,
            qualityScore: quality,
            sourceWeight: editedAfter ? 0.2f : 0.55f,
            confidence: context.ContextConfidence));

        if (!editedAfter)
        {
            _eventService.Append(CreateRecord(
                "accepted_text_untouched",
                context,
                suggestionId,
                requestId,
                prefix,
                completion,
                completion,
                completion,
                latencyMs,
                cycleDepth,
                editedAfter: false,
                untouchedForMs: 1500,
                qualityScore: quality,
                sourceWeight: 0.7f,
                confidence: Math.Min(0.98, context.ContextConfidence + 0.1)));
        }

        lock (_lock)
        {
            if (_pendingSuggestion?.SuggestionId == suggestionId)
                _pendingSuggestion.Resolved = true;
        }
    }

    public void OnDismiss(string reason, string suggestionId, long requestId, ContextSnapshot context, string prefix, string completion)
    {
        if (string.IsNullOrWhiteSpace(completion))
            return;

        _eventService.Append(CreateRecord(
            "suggestion_dismiss",
            context,
            suggestionId,
            requestId,
            prefix,
            completion,
            acceptedText: "",
            userWrittenText: "",
            commitReason: reason,
            sourceWeight: 1.0f,
            confidence: context.ContextConfidence));

        lock (_lock)
        {
            if (_pendingSuggestion?.SuggestionId == suggestionId)
                _pendingSuggestion.Resolved = true;
        }
    }

    public void OnBufferChanged(string currentBuffer, ContextSnapshot context)
    {
        PendingSuggestion? pending;
        lock (_lock)
        {
            pending = _pendingSuggestion;
        }

        if (pending == null || pending.Resolved || pending.TypedPastLogged)
            return;

        if (!IsSameContext(pending, context))
            return;

        if (!currentBuffer.StartsWith(pending.Prefix, StringComparison.OrdinalIgnoreCase))
            return;

        var fullSuggestion = pending.Prefix + pending.Completion;
        if (currentBuffer.Length <= pending.Prefix.Length)
            return;

        if (!fullSuggestion.StartsWith(currentBuffer, StringComparison.OrdinalIgnoreCase))
        {
            var userWritten = currentBuffer[pending.Prefix.Length..];
            _eventService.Append(CreateRecord(
                "suggestion_typed_past",
                context,
                pending.SuggestionId,
                pending.RequestId,
                pending.Prefix,
                pending.Completion,
                acceptedText: "",
                userWrittenText: userWritten,
                sourceWeight: 1.0f,
                confidence: context.ContextConfidence));

            lock (_lock)
            {
                if (_pendingSuggestion?.SuggestionId == pending.SuggestionId)
                    _pendingSuggestion.TypedPastLogged = true;
            }
        }
    }

    public bool OnManualCommit(string committedText, ContextSnapshot context, string reason)
    {
        if (string.IsNullOrWhiteSpace(committedText))
            return false;

        PendingSuggestion? pending;
        lock (_lock)
        {
            pending = _pendingSuggestion;
        }

        string suggestionId = "";
        long requestId = 0;
        string typedPrefix = "";
        string shownCompletion = "";
        string userWritten = committedText;

        if (pending != null && !pending.Resolved && IsSameContext(pending, context))
        {
            suggestionId = pending.SuggestionId;
            requestId = pending.RequestId;
            typedPrefix = pending.Prefix;
            shownCompletion = pending.Completion;

            if (committedText.StartsWith(typedPrefix, StringComparison.OrdinalIgnoreCase) &&
                committedText.Length > typedPrefix.Length)
            {
                userWritten = committedText[typedPrefix.Length..];
            }
        }

        // Skip trivially short commits (e.g. a lone period typed to reject a suggestion).
        // These carry no meaningful style signal and would pollute the native-writing corpus.
        var trimmedWritten = userWritten.Trim('.', '!', '?', ':', ';', ',', ' ');
        if (trimmedWritten.Length < 3)
        {
            lock (_lock)
            {
                if (_pendingSuggestion != null && _pendingSuggestion.SuggestionId == suggestionId)
                    _pendingSuggestion.Resolved = true;
            }
            return false;
        }

        _eventService.Append(CreateRecord(
            "manual_continuation_committed",
            context,
            suggestionId,
            requestId,
            typedPrefix,
            shownCompletion,
            acceptedText: "",
            userWrittenText: userWritten,
            commitReason: reason,
            sourceWeight: 1.0f,
            confidence: Math.Min(0.98, context.ContextConfidence + 0.05)));

        lock (_lock)
        {
            if (_pendingSuggestion != null && _pendingSuggestion.SuggestionId == suggestionId)
                _pendingSuggestion.Resolved = true;
        }

        return true;
    }

    public void ClearSuggestion(string suggestionId)
    {
        lock (_lock)
        {
            if (_pendingSuggestion?.SuggestionId == suggestionId)
                _pendingSuggestion = null;
        }
    }

    private LearningEventRecord CreateRecord(
        string eventType,
        ContextSnapshot context,
        string suggestionId,
        long requestId,
        string typedPrefix,
        string shownCompletion,
        string acceptedText,
        string userWrittenText,
        int latencyMs = -1,
        int cycleDepth = 0,
        bool editedAfter = false,
        int untouchedForMs = 0,
        float qualityScore = 0.5f,
        float sourceWeight = 0.5f,
        double confidence = 0.5,
        string commitReason = "")
    {
        return new LearningEventRecord
        {
            SessionId = _sessionId,
            SuggestionId = suggestionId,
            RequestId = requestId,
            EventType = eventType,
            ProcessName = context.ProcessName,
            Category = context.Category,
            SafeContextLabel = context.SafeContextLabel,
            ContextKeys = new LearningEventContextKeys
            {
                ProcessKey = context.ProcessKey,
                WindowKey = context.WindowKey,
                SubcontextKey = context.SubcontextKey,
                ProcessLabel = context.ProcessLabel,
                WindowLabel = context.WindowLabel,
                SubcontextLabel = context.SubcontextLabel
            },
            TypedPrefix = typedPrefix,
            ShownCompletion = shownCompletion,
            AcceptedText = acceptedText,
            UserWrittenText = userWrittenText,
            CommitReason = commitReason,
            LatencyMs = latencyMs,
            CycleDepth = cycleDepth,
            EditedAfterAccept = editedAfter,
            UntouchedForMs = untouchedForMs,
            QualityScore = MathF.Round(qualityScore, 3),
            SourceWeight = MathF.Round(sourceWeight, 3),
            Confidence = Math.Round(confidence, 3)
        };
    }

    private static bool IsSameContext(PendingSuggestion? pending, ContextSnapshot context)
    {
        if (pending == null)
            return false;

        return string.Equals(pending.ProcessKey, context.ProcessKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pending.SubcontextKey, context.SubcontextKey, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PendingSuggestion
    {
        public string SuggestionId { get; set; } = "";
        public long RequestId { get; set; }
        public string Prefix { get; set; } = "";
        public string Completion { get; set; } = "";
        public string ProcessKey { get; set; } = "";
        public string WindowKey { get; set; } = "";
        public string SubcontextKey { get; set; } = "";
        public ContextSnapshot? Context { get; set; }
        public bool TypedPastLogged { get; set; }
        public bool Resolved { get; set; }
    }
}
