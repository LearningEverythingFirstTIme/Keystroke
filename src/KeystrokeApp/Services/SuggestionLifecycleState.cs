namespace KeystrokeApp.Services;

public sealed record SuggestionLifecycleState(
    long ActiveRequestId,
    string Prefix,
    string Completion,
    bool IsVisible,
    bool IsLoading,
    bool AlternativesValid,
    string SuggestionId,
    long SuggestionRequestId,
    ContextSnapshot? Context,
    long ShownAtTicks,
    int CycleDepth)
{
    public static SuggestionLifecycleState Empty { get; } = new(
        ActiveRequestId: 0,
        Prefix: "",
        Completion: "",
        IsVisible: false,
        IsLoading: false,
        AlternativesValid: false,
        SuggestionId: "",
        SuggestionRequestId: 0,
        Context: null,
        ShownAtTicks: 0,
        CycleDepth: 0);
}
