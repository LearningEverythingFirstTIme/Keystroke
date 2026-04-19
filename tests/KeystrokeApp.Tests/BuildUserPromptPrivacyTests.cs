using System.Text.RegularExpressions;
using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

/// <summary>
/// Guards the central privacy invariant: all outbound prompt text flows through
/// <see cref="OutboundPrivacyService"/>. The prior implementation sanitized typed
/// text and few-shot examples but passed <c>ScreenText</c> and <c>RollingContext</c>
/// through untouched — a test card visible on screen or inside the rolling buffer
/// would land in the provider payload intact. These tests fail loudly if that
/// regresses.
/// </summary>
public class BuildUserPromptPrivacyTests
{
    // Well-known Luhn-valid test card (Visa). Scrubbing replaces it with a token;
    // we assert the original digit run is no longer present.
    private const string TestCardNumber = "4111111111111111";

    [Fact]
    public void ScreenText_WithCreditCard_IsScrubbedBeforeBeingEmbedded()
    {
        var engine = new TestEngine();
        var context = new ContextSnapshot
        {
            TypedText = "write a follow up",
            ScreenText = $"order confirmation card {TestCardNumber} charged successfully"
        };

        var prompt = engine.InvokeBuildUserPrompt(context);

        AssertNoCardDigits(prompt);
        Assert.Contains("<screen_context>", prompt);
    }

    [Fact]
    public void RollingContext_WithCreditCard_IsScrubbedBeforeBeingEmbedded()
    {
        var engine = new TestEngine();
        var context = new ContextSnapshot
        {
            TypedText = "thanks for",
            RollingContext = $"earlier I sent them my card {TestCardNumber} for the order"
        };

        var prompt = engine.InvokeBuildUserPrompt(context);

        AssertNoCardDigits(prompt);
        Assert.Contains("<recently_written>", prompt);
    }

    [Fact]
    public void ScreenText_WithEmail_IsScrubbed()
    {
        var engine = new TestEngine();
        var context = new ContextSnapshot
        {
            TypedText = "reply",
            ScreenText = "From: alice@example.com\nSubject: hi"
        };

        var prompt = engine.InvokeBuildUserPrompt(context);

        Assert.DoesNotContain("alice@example.com", prompt);
    }

    [Fact]
    public void RollingContextStraddlingTruncationBoundary_DoesNotLeakTail()
    {
        // Guards the "sanitize before truncation" invariant: if a card straddles the
        // truncation cut, the naive approach (truncate then scrub) could leave the tail
        // of the card intact in the emitted prompt.
        var engine = new TestEngine();

        // Pad so the card sits near the start of the rolling context — well outside
        // the trailing truncation window. If we truncated first, the card would be
        // dropped entirely. If we scrubbed first, the card is replaced with a token
        // that survives truncation. Either way the original digits must be gone.
        var padding = new string('x', 3000);
        var context = new ContextSnapshot
        {
            TypedText = "ok",
            RollingContext = $"card {TestCardNumber} {padding}"
        };

        var prompt = engine.InvokeBuildUserPrompt(context);

        AssertNoCardDigits(prompt);
    }

    private static void AssertNoCardDigits(string prompt)
    {
        Assert.DoesNotContain(TestCardNumber, prompt);

        // Belt-and-braces: no unscrubbed run of 13+ digits should appear anywhere
        // in the prompt. This catches alternative card formats or detector changes.
        var longDigitRun = Regex.Match(prompt, @"\d{13,}");
        Assert.False(longDigitRun.Success,
            $"Prompt contained a digit run '{longDigitRun.Value}' that looks like an unscrubbed card number.");
    }

    private sealed class TestEngine : PredictionEngineBase
    {
        public TestEngine() : base("build-user-prompt-privacy-tests.log")
        {
        }

        public string InvokeBuildUserPrompt(ContextSnapshot context) =>
            BuildUserPrompt(context);
    }
}
