using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class SensitiveDataDetectorTests
{
    [Fact]
    public void Detect_FindsEmailAndMarksItAsNonBlocking()
    {
        var matches = SensitiveDataDetector.Detect("Email me at nick@example.com");

        var email = Assert.Single(matches.Where(m => m.Kind == "Email"));
        Assert.Equal("nick@example.com", "Email me at nick@example.com".Substring(email.Start, email.Length));
        Assert.Equal("[EMAIL]", email.Replacement);
        Assert.False(email.ShouldBlockPrediction);
    }

    [Fact]
    public void Detect_FindsValidCreditCardAndBlocksPrediction()
    {
        var matches = SensitiveDataDetector.Detect("Card 4242 4242 4242 4242");

        var card = Assert.Single(matches.Where(m => m.Kind == "CreditCard"));
        Assert.Equal("[CREDIT_CARD]", card.Replacement);
        Assert.True(card.ShouldBlockPrediction);
    }

    [Fact]
    public void Detect_DoesNotTreatInvalidCardNumberAsCreditCard()
    {
        var matches = SensitiveDataDetector.Detect("Number 1234 5678 9012 3456");

        Assert.DoesNotContain(matches, m => m.Kind == "CreditCard");
    }

    [Fact]
    public void ContainsBlockingSensitiveData_ReturnsTrueForApiKey()
    {
        const string text = "api_key: sk-abcdefghijklmnopqrstuvwxyz123456";

        Assert.True(SensitiveDataDetector.ContainsBlockingSensitiveData(text));
    }
}
