namespace KeystrokeApp.Services;

public sealed class SuggestionLifecycleController
{
    private readonly object _sync = new();
    private SuggestionLifecycleState _state = SuggestionLifecycleState.Empty;
    private long _suggestionCounter;

    public SuggestionLifecycleState Snapshot()
    {
        lock (_sync)
            return _state;
    }

    public bool IsCurrentRequest(long requestId)
    {
        lock (_sync)
            return _state.ActiveRequestId == requestId;
    }

    public void BeginPrediction(long requestId, string prefix)
    {
        lock (_sync)
        {
            _state = SuggestionLifecycleState.Empty with
            {
                ActiveRequestId = requestId,
                Prefix = prefix,
                IsLoading = true
            };
        }
    }

    public void CancelPrediction(long? requestId = null)
    {
        lock (_sync)
        {
            if (requestId.HasValue && _state.ActiveRequestId != requestId.Value)
                return;

            _state = _state with
            {
                ActiveRequestId = 0,
                IsLoading = false,
                AlternativesValid = false
            };
        }
    }

    public void CompletePrediction(long requestId)
    {
        lock (_sync)
        {
            if (_state.ActiveRequestId != requestId)
                return;

            _state = _state with
            {
                ActiveRequestId = _state.IsVisible ? _state.ActiveRequestId : 0,
                IsLoading = false
            };
        }
    }

    public string? ClearSuggestion(bool resetRequest = false)
    {
        lock (_sync)
        {
            var clearedSuggestionId = string.IsNullOrWhiteSpace(_state.SuggestionId)
                ? null
                : _state.SuggestionId;

            _state = _state with
            {
                ActiveRequestId = resetRequest ? 0 : _state.ActiveRequestId,
                Prefix = resetRequest ? "" : _state.Prefix,
                Completion = "",
                IsVisible = false,
                IsLoading = resetRequest ? false : _state.IsLoading,
                AlternativesValid = false,
                SuggestionId = "",
                SuggestionRequestId = 0,
                Context = null,
                ShownAtTicks = 0,
                CycleDepth = 0
            };

            return clearedSuggestionId;
        }
    }

    public (SuggestionLifecycleState State, string? ClearedSuggestionId) ShowSuggestion(
        long requestId,
        ContextSnapshot context,
        string prefix,
        string completion)
    {
        lock (_sync)
        {
            var clearedSuggestionId = string.IsNullOrWhiteSpace(_state.SuggestionId)
                ? null
                : _state.SuggestionId;
            var suggestionId = $"sugg-{Interlocked.Increment(ref _suggestionCounter)}";

            _state = _state with
            {
                ActiveRequestId = requestId,
                Prefix = prefix,
                Completion = completion,
                IsVisible = true,
                IsLoading = false,
                AlternativesValid = false,
                SuggestionId = suggestionId,
                SuggestionRequestId = requestId,
                Context = context,
                ShownAtTicks = DateTime.UtcNow.Ticks,
                CycleDepth = 0
            };

            return (_state, clearedSuggestionId);
        }
    }

    public bool MarkAlternativesReady(long requestId)
    {
        lock (_sync)
        {
            if (_state.ActiveRequestId != requestId || !_state.IsVisible)
                return false;

            _state = _state with { AlternativesValid = true };
            return true;
        }
    }

    public int IncrementCycleDepth()
    {
        lock (_sync)
        {
            _state = _state with { CycleDepth = _state.CycleDepth + 1 };
            return _state.CycleDepth;
        }
    }
}
