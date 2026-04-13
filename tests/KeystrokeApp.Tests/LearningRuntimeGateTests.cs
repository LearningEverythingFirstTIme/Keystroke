using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class LearningRuntimeGateTests
{
    [Fact]
    public void FreeTier_WithLearningV2Enabled_IsNotActive()
    {
        var config = new AppConfig
        {
            LearningEnabled = true,
            LearningV2Enabled = true,
            StyleProfileEnabled = true
        };

        Assert.False(LearningRuntimeGate.IsPersonalizedLearningActive(config, isProTier: false));
        Assert.False(LearningRuntimeGate.IsProfileLearningActive(config, isProTier: false));
    }

    [Fact]
    public void ProTier_WithLearningDisabled_IsNotActive()
    {
        var config = new AppConfig
        {
            LearningEnabled = false,
            LearningV2Enabled = true,
            StyleProfileEnabled = true
        };

        Assert.False(LearningRuntimeGate.IsPersonalizedLearningActive(config, isProTier: true));
        Assert.False(LearningRuntimeGate.IsProfileLearningActive(config, isProTier: true));
    }

    [Fact]
    public void ProTier_WithLearningV2Disabled_IsNotActive()
    {
        var config = new AppConfig
        {
            LearningEnabled = true,
            LearningV2Enabled = false,
            StyleProfileEnabled = true
        };

        Assert.False(LearningRuntimeGate.IsPersonalizedLearningActive(config, isProTier: true));
        Assert.False(LearningRuntimeGate.IsProfileLearningActive(config, isProTier: true));
    }

    [Fact]
    public void ProTier_WithLearningAndLearningV2Enabled_IsActive()
    {
        var config = new AppConfig
        {
            LearningEnabled = true,
            LearningV2Enabled = true,
            StyleProfileEnabled = true
        };

        Assert.True(LearningRuntimeGate.IsPersonalizedLearningActive(config, isProTier: true));
        Assert.True(LearningRuntimeGate.IsProfileLearningActive(config, isProTier: true));
    }
}
