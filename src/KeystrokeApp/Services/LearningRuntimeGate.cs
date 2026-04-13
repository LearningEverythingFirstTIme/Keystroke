namespace KeystrokeApp.Services;

public static class LearningRuntimeGate
{
    public static bool IsPersonalizedLearningActive(AppConfig config, bool isProTier)
        => isProTier && config.LearningEnabled && config.LearningV2Enabled;

    public static bool IsProfileLearningActive(AppConfig config, bool isProTier)
        => IsPersonalizedLearningActive(config, isProTier) && config.StyleProfileEnabled;
}
