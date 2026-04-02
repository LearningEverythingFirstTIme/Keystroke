using System.Threading;
using System.Threading.Tasks;

namespace KeystrokeApp.Services;

/// <summary>
/// Dummy prediction engine for testing (no API calls).
/// </summary>
public class DummyPredictionEngine : IPredictionEngine
{
    public Task<string?> PredictAsync(ContextSnapshot context, CancellationToken cancellationToken = default)
    {
        var prefix = context.TypedText;
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3)
            return Task.FromResult<string?>(null);

        var completion = prefix.ToLower() switch
        {
            var b when b.StartsWith("hel") => "lo world",
            var b when b.StartsWith("th") => "e quick brown fox",
            var b when b.StartsWith("than") => "ks for your help",
            var b when b.StartsWith("how a") => "re you doing",
            var b when b.StartsWith("good mo") => "rning",
            var b when b.StartsWith("i am") => " doing great",
            var b when b.StartsWith("this is") => " a test",
            var b when b.StartsWith("wh") => "at's up",
            var b when b.StartsWith("ye") => "ah that sounds good",
            var b when b.StartsWith("no") => " problem at all",
            var b when b.StartsWith("plea") => "se let me know",
            var b when b.StartsWith("let me") => " know if you need anything",
            var b when b.StartsWith("can you") => " help me with this",
            var b when b.StartsWith("hey") => " there",
            _ => null
        };

        return Task.FromResult(completion);
    }

    public Task<string?> GenerateTextAsync(string systemPrompt, string userPrompt, int maxTokens = 200, CancellationToken ct = default)
        => Task.FromResult<string?>("The user writes in a neutral, professional tone with concise sentences.");
}
