namespace KeystrokeApp.Services;

public static class SuggestionAcceptance
{
    public static string GetRemainingCompletion(string typedBuffer, string fullSuggestion)
    {
        typedBuffer ??= string.Empty;
        fullSuggestion ??= string.Empty;

        if (fullSuggestion.Length == 0)
            return string.Empty;

        if (typedBuffer.Length == 0)
            return fullSuggestion;

        if (fullSuggestion.StartsWith(typedBuffer, StringComparison.Ordinal))
            return fullSuggestion[typedBuffer.Length..];

        var maxOverlap = Math.Min(typedBuffer.Length, fullSuggestion.Length);
        for (var overlap = maxOverlap; overlap > 0; overlap--)
        {
            if (typedBuffer.EndsWith(fullSuggestion[..overlap], StringComparison.Ordinal))
                return fullSuggestion[overlap..];
        }

        return fullSuggestion;
    }
}
