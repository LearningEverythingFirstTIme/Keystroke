namespace KeystrokeApp.Services;

public sealed record PromptPreviewSnapshot
{
    public string ProviderLabel { get; init; } = "";
    public string AppFilteringModeLabel { get; init; } = "";
    public string ActiveAppLabel { get; init; } = "";
    public string AppAvailabilityLabel { get; init; } = "";
    public string AppAvailabilityReason { get; init; } = "";
    public bool WouldSendPrediction { get; init; }
    public bool TypedInputBlocked { get; init; }
    public string TypedInputStatus { get; init; } = "";
    public string TypedTextPreview { get; init; } = "";
    public string ScreenContextPreview { get; init; } = "";
    public string RollingContextPreview { get; init; } = "";
    public bool LearningHintsIncluded { get; init; }
    public string LearningHintsPreview { get; init; } = "";
    public string UserPromptPreview { get; init; } = "";
}
