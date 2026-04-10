namespace KeystrokeApp.Services;

public sealed class OnboardingStateService
{
    public const string GeminiApiKeyUrl = "https://aistudio.google.com/apikey";

    public void ApplyRecommendedGeminiDefaults(AppConfig config)
    {
        config.PredictionEngine = "gemini";
        config.GeminiModel = AppConfig.DefaultGeminiModel;
        config.OcrEnabled = true;
        config.RollingContextEnabled = true;
        config.LearningEnabled = false;
    }

    public bool HasUsableProviderSetup(AppConfig config)
    {
        return config.PredictionEngine.ToLowerInvariant() switch
        {
            "gemini" => HasApiKey(config.GeminiApiKey),
            "gpt5" => HasApiKey(config.OpenAiApiKey),
            "claude" => HasApiKey(config.AnthropicApiKey),
            "openrouter" => HasApiKey(config.OpenRouterApiKey),
            "ollama" => !string.IsNullOrWhiteSpace(config.OllamaModel),
            _ => false
        };
    }

    public bool TryCompleteOnboardingFromExistingSetup(AppConfig config)
    {
        if (!config.ConsentAccepted || config.OnboardingCompleted || !HasUsableProviderSetup(config))
            return false;

        config.OnboardingCompleted = true;
        return true;
    }

    public bool CanActivateRuntime(AppConfig config) =>
        config.ConsentAccepted && HasUsableProviderSetup(config);

    public string GetSetupIncompleteReason(AppConfig config)
    {
        if (!config.ConsentAccepted)
            return "Consent is required before predictions can start.";

        return config.PredictionEngine.ToLowerInvariant() switch
        {
            "gemini" => "Add and verify a Gemini API key to start completions.",
            "gpt5" => "Add an OpenAI API key to start completions.",
            "claude" => "Add an Anthropic API key to start completions.",
            "openrouter" => "Add an OpenRouter API key to start completions.",
            "ollama" => "Finish local model setup to start completions.",
            _ => "Finish onboarding to start completions."
        };
    }

    private static bool HasApiKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length > 10;
}
