using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeystrokeApp.Services;

/// <summary>
/// App configuration loaded from config.json.
/// API keys are stored encrypted on disk using Windows DPAPI and
/// decrypted transparently when loaded into memory.
/// </summary>
public class AppConfig
{
    // Engine settings — runtime plaintext values (never serialized directly)
    [JsonIgnore] public string? GeminiApiKey { get; set; }
    [JsonIgnore] public string? AnthropicApiKey { get; set; }
    [JsonIgnore] public string? OpenAiApiKey { get; set; }

    // Encrypted on-disk representations (used only for JSON serialization)
    public string? GeminiApiKeyEncrypted { get; set; }
    public string? AnthropicApiKeyEncrypted { get; set; }
    public string? OpenAiApiKeyEncrypted { get; set; }
    public string PredictionEngine { get; set; } = "gemini";
    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    public string ClaudeModel { get; set; } = "claude-3-haiku-20240307";
    public string Gpt5Model { get; set; } = "gpt-5.4-mini";

    // Behavior settings
    public int DebounceMs { get; set; } = 300;
    public int FastDebounceMs { get; set; } = 100;
    public int MinBufferLength { get; set; } = 3;
    public double Temperature { get; set; } = 0.3;
    public int MaxOutputTokens { get; set; } = 300;

    // Completion preset: "brief", "standard", "extended", "unlimited"
    public string CompletionPreset { get; set; } = "extended";

    // Context features
    public bool OcrEnabled { get; set; } = true;

    // Learning: opt-in tracking of accepted/dismissed completions for few-shot learning.
    // Off by default — user must explicitly enable in settings.
    public bool LearningEnabled { get; set; } = false;

    // First-launch consent: must be true before the app activates keystroke monitoring.
    public bool ConsentAccepted { get; set; } = false;

    // User-customizable system prompt (empty = use default)
    public string? CustomSystemPrompt { get; set; }

    // The default system prompt (used when CustomSystemPrompt is empty)
    public static string DefaultSystemPrompt =>
        "You are a predictive text engine embedded in a keyboard autocomplete system.\n" +
        "Your ONLY job is to predict the most likely continuation of what the user is typing.\n\n" +
        "OUTPUT RULES (strict — violating these makes your output unusable):\n" +
        "- Output ONLY the predicted continuation text. Nothing else.\n" +
        "- NEVER repeat any part of the text the user has already typed.\n" +
        "- NEVER wrap output in quotation marks, backticks, or any formatting.\n" +
        "- NEVER include explanations, alternatives, or meta-commentary.\n" +
        "- If you are not confident in a prediction, output NOTHING. An empty response is always better than a wrong one.\n\n" +
        "PREDICTION RULES (how to decide what to predict):\n" +
        "- Predict the SINGLE most likely continuation. Favor common, expected phrasing over creative or unusual wording.\n" +
        "- The screen context shows what the user is looking at — use it to understand the topic and conversation flow.\n" +
        "- ONLY predict content that is directly relevant to what the user has started typing. Do not introduce new topics, names, or facts not supported by the screen context.\n" +
        "- If the screen shows a conversation, predict a natural reply to the most recent message. Do not parrot or rephrase what someone else already said.\n" +
        "- NEVER repeat or echo text that the user has already written earlier in the same message. If the user has already typed a phrase, do not predict that same phrase again. Look at the FULL text the user has typed so far and ensure your prediction is a NEW continuation, not a duplicate of something already said.\n" +
        "- Match the user's tone and formality exactly. If they write casually, predict casual text. If formal, predict formal text.\n" +
        "- Complete ONLY the current sentence or thought. Stop at a natural ending point — do not ramble or start a new sentence unless the user clearly would.\n" +
        "- When the typed text is very short or ambiguous (under ~5 words), prefer shorter, safer completions. Only predict longer continuations when the intent is clear.\n" +
        "- Never predict UI elements, placeholder text, or instructions (e.g. 'Type a message', 'Send', 'Tab to accept').\n" +
        "- Always complete with whole words. Never end your output in the middle of a word.";

    /// <summary>
    /// Get the effective system prompt (custom if set, otherwise default).
    /// </summary>
    public string EffectiveSystemPrompt => 
        string.IsNullOrWhiteSpace(CustomSystemPrompt) ? DefaultSystemPrompt : CustomSystemPrompt;

    /// <summary>
    /// Get the word count instruction based on completion preset.
    /// </summary>
    public string CompletionLengthInstruction => CompletionPreset switch
    {
        "brief" => "Write 3-5 words to complete the immediate next phrase.",
        "standard" => "Write 8-15 words to complete the sentence.",
        "extended" => "Write 15-30 words to complete the full thought.",
        "unlimited" => "Write as much as needed to fully complete the thought, 30-50 words if appropriate.",
        _ => "Write 15-30 words to complete the full thought."
    };

    /// <summary>
    /// Max output tokens scaled to the completion preset.
    /// Smaller values mean faster Gemini responses.
    /// </summary>
    public int PresetMaxOutputTokens => CompletionPreset switch
    {
        "brief" => 30,
        "standard" => 60,
        "extended" => 100,
        "unlimited" => 200,
        _ => 100
    };

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Keystroke",
        "config.json"
    );

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

                // Decrypt API keys from their on-disk encrypted form
                config.GeminiApiKey = KeyProtection.Decrypt(config.GeminiApiKeyEncrypted);
                config.AnthropicApiKey = KeyProtection.Decrypt(config.AnthropicApiKeyEncrypted);
                config.OpenAiApiKey = KeyProtection.Decrypt(config.OpenAiApiKeyEncrypted);

                // Migrate legacy plaintext keys from old config format.
                // Old configs stored keys as "GeminiApiKey" etc. directly in JSON.
                // JsonSerializer will ignore them (marked [JsonIgnore]) but we can
                // detect them by checking if the raw JSON contains the old field names
                // while the encrypted fields are empty.
                MigrateLegacyKeys(json, config);

                return config;
            }
        }
        catch { }
        return new AppConfig();
    }

    /// <summary>
    /// Detects plaintext API keys from old config format and migrates them
    /// to the encrypted fields. Saves the config immediately to remove
    /// plaintext keys from disk.
    /// </summary>
    private static void MigrateLegacyKeys(string json, AppConfig config)
    {
        bool migrated = false;

        // Parse raw JSON to check for legacy plaintext key fields
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (config.GeminiApiKey == null &&
                root.TryGetProperty("GeminiApiKey", out var gemKey) &&
                gemKey.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(gemKey.GetString()))
            {
                config.GeminiApiKey = gemKey.GetString();
                migrated = true;
            }

            if (config.AnthropicApiKey == null &&
                root.TryGetProperty("AnthropicApiKey", out var antKey) &&
                antKey.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(antKey.GetString()))
            {
                config.AnthropicApiKey = antKey.GetString();
                migrated = true;
            }

            if (config.OpenAiApiKey == null &&
                root.TryGetProperty("OpenAiApiKey", out var oaiKey) &&
                oaiKey.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(oaiKey.GetString()))
            {
                config.OpenAiApiKey = oaiKey.GetString();
                migrated = true;
            }
        }
        catch { }

        // Re-save immediately so plaintext keys are replaced with encrypted versions
        if (migrated)
        {
            config.Save();
        }
    }

    public void Save()
    {
        // Encrypt API keys before writing to disk
        GeminiApiKeyEncrypted = KeyProtection.Encrypt(GeminiApiKey);
        AnthropicApiKeyEncrypted = KeyProtection.Encrypt(AnthropicApiKey);
        OpenAiApiKeyEncrypted = KeyProtection.Encrypt(OpenAiApiKey);

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public static void EnsureExists()
    {
        if (!File.Exists(ConfigPath))
        {
            new AppConfig().Save();
        }
    }
}
