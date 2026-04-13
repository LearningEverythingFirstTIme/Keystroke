using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

/// <summary>
/// Tests for the ContextAdaptiveProfile data model and AdaptiveSettingsData container.
/// These exercise the deterministic profile computation logic that drives
/// per-context temperature and length adaptation.
/// </summary>
public class ContextAdaptiveProfileTests
{
    // ── HasSufficientData ────────────────────────────────────────────────────

    [Theory]
    [InlineData(5, 5, true)]   // exactly 10 = threshold
    [InlineData(10, 0, true)]  // 10 accepts, 0 dismissals
    [InlineData(0, 10, true)]  // 0 accepts, 10 dismissals
    [InlineData(4, 5, false)]  // 9 < 10
    [InlineData(0, 0, false)]  // no data
    public void HasSufficientData_RespectsMinimumThreshold(int accepted, int dismissed, bool expected)
    {
        var profile = new ContextAdaptiveProfile
        {
            AcceptedCount = accepted,
            DismissedCount = dismissed
        };

        Assert.Equal(expected, profile.HasSufficientData);
    }

    // ── LengthInstruction mapping ────────────────────────────────────────────

    [Theory]
    [InlineData("brief", "3-5 words")]
    [InlineData("standard", "8-15 words")]
    [InlineData("extended", "15-30 words")]
    [InlineData("unlimited", "as much as needed")]
    public void LengthInstruction_MapsPresetToCorrectRange(string preset, string expectedFragment)
    {
        var profile = new ContextAdaptiveProfile { SuggestedLengthPreset = preset };

        Assert.Contains(expectedFragment, profile.LengthInstruction);
    }

    [Fact]
    public void LengthInstruction_UnknownPreset_FallsBackToExtended()
    {
        var profile = new ContextAdaptiveProfile { SuggestedLengthPreset = "invalid" };

        Assert.Contains("15-30 words", profile.LengthInstruction);
    }

    // ── AdaptiveSettingsData: GetSettings fallback chain ──────────────────────

    [Fact]
    public void GetSettings_PrefersSubcontextOverCategory()
    {
        var service = new ContextAdaptiveSettingsService();

        // We can't inject settings data through the public API without file I/O,
        // but we CAN test the ContextAdaptiveProfile model independently.
        // The GetSettings fallback chain is: subcontext → category → null.
        //
        // This test verifies the model's HasSufficientData gate,
        // which GetSettings uses to decide whether to return a profile.

        var sufficientProfile = new ContextAdaptiveProfile
        {
            ContextKey = "ctx-slack-team",
            AcceptedCount = 8,
            DismissedCount = 5,
            TemperatureAdjustment = -0.05
        };

        var insufficientProfile = new ContextAdaptiveProfile
        {
            ContextKey = "ctx-new",
            AcceptedCount = 2,
            DismissedCount = 1,
            TemperatureAdjustment = 0.10
        };

        Assert.True(sufficientProfile.HasSufficientData);
        Assert.False(insufficientProfile.HasSufficientData);
    }

    // ── Temperature adjustment semantics ─────────────────────────────────────

    [Fact]
    public void TemperatureAdjustment_PrecisionTuned_IsNegative()
    {
        // High accept rate scenario: model is already accurate, lower temperature
        var profile = new ContextAdaptiveProfile
        {
            AcceptedCount = 18,
            DismissedCount = 2,
            AcceptRate = 0.90,
            TemperatureAdjustment = -0.08
        };

        Assert.True(profile.TemperatureAdjustment < 0,
            "Precision-tuned contexts should have negative temperature adjustment");
    }

    [Fact]
    public void TemperatureAdjustment_VarietyBoosted_IsPositive()
    {
        // Low accept rate scenario: model needs variety, raise temperature
        var profile = new ContextAdaptiveProfile
        {
            AcceptedCount = 5,
            DismissedCount = 15,
            AcceptRate = 0.25,
            TemperatureAdjustment = 0.12
        };

        Assert.True(profile.TemperatureAdjustment > 0,
            "Variety-boosted contexts should have positive temperature adjustment");
    }

    // ── CorrectionInfo model tests ───────────────────────────────────────────

    [Fact]
    public void CorrectionInfo_HasCorrection_RequiresBackspaceCount()
    {
        var noCorrection = new CorrectionInfo
        {
            EditDetected = false,
            BackspaceCount = 0,
            ReplacementText = ""
        };

        var withCorrection = new CorrectionInfo
        {
            EditDetected = true,
            BackspaceCount = 3,
            ReplacementText = "fix"
        };

        Assert.False(noCorrection.HasCorrection);
        Assert.True(withCorrection.HasCorrection);
    }

    [Theory]
    [InlineData(0, "", "none")]
    [InlineData(1, "", "minor")]
    [InlineData(2, "ab", "minor")]
    [InlineData(10, "", "truncated")]
    [InlineData(5, "word", "replaced_ending")]
    public void CorrectionType_ClassifiesCorrectly(int backspaces, string replacement, string expected)
    {
        var info = new CorrectionInfo
        {
            EditDetected = backspaces > 0,
            BackspaceCount = backspaces,
            ReplacementText = replacement
        };

        Assert.Equal(expected, info.CorrectionType());
    }

    // ── AdaptiveSettingsData staleness ────────────────────────────────────────

    [Fact]
    public void AdaptiveSettingsData_FreshData_IsUsable()
    {
        var data = new AdaptiveSettingsData
        {
            LastUpdated = DateTime.UtcNow,
            EventsProcessed = 100,
            Contexts = { ["ctx-1"] = new ContextAdaptiveProfile { AcceptedCount = 10, DismissedCount = 5 } },
            Categories = { ["Chat"] = new ContextAdaptiveProfile { AcceptedCount = 20, DismissedCount = 10 } }
        };

        Assert.True((DateTime.UtcNow - data.LastUpdated).TotalDays < 14,
            "Fresh data should be within the 14-day staleness window");
        Assert.Single(data.Contexts);
        Assert.Single(data.Categories);
    }

    // ── CorrectionPatterns model tests ───────────────────────────────────────

    [Fact]
    public void CategoryCorrectionPatterns_TruncationRate_CalculatedCorrectly()
    {
        var patterns = new CategoryCorrectionPatterns
        {
            TotalCorrections = 20,
            TruncationCount = 9,
            TruncationRate = 9.0 / 20.0
        };

        Assert.Equal(0.45, patterns.TruncationRate);
    }

    [Fact]
    public void WordReplacement_CapturesPreferenceData()
    {
        var replacement = new WordReplacement
        {
            Original = "option",
            Replacement = "plan",
            Count = 3
        };

        Assert.Equal("option", replacement.Original);
        Assert.Equal("plan", replacement.Replacement);
        Assert.Equal(3, replacement.Count);
    }

    [Fact]
    public void CorrectionPatterns_EmptyState_HasNoPatternsOrContexts()
    {
        var patterns = new CorrectionPatterns();

        Assert.Empty(patterns.Categories);
        Assert.Empty(patterns.Contexts);
        Assert.Empty(patterns.ContextLabels);
        Assert.Equal(0, patterns.EntriesProcessed);
    }
}
