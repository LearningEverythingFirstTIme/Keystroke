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
    public const string DefaultGeminiModel = "gemini-3.1-flash-lite-preview";
    public const string DefaultClaudeModel = "claude-haiku-4-5";
    public const string DefaultGpt5Model = "gpt-5.4-nano";
    public const string DefaultOllamaModel = "qwen3:30b-a3b";

    private static readonly HashSet<string> SupportedGeminiModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "gemini-3.1-flash-lite-preview",
        "gemini-3-flash-preview",
        "gemini-3.1-pro-preview"
    };

    private static readonly HashSet<string> SupportedClaudeModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude-haiku-4-5",
        "claude-sonnet-4-6"
    };

    private static readonly HashSet<string> SupportedGpt5Models = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.4-nano",
        "gpt-5.4-mini",
        "gpt-5.4"
    };

    private static readonly HashSet<string> SupportedOllamaModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "qwen3:30b-a3b",
        "qwen3:8b",
        "mistral:7b",
        "llama3.1:8b"
    };

    private static readonly Dictionary<string, string> LegacyGeminiModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-2.5-flash-lite"] = "gemini-3.1-flash-lite-preview",
        ["gemini-2.5-flash"] = "gemini-3-flash-preview",
        ["gemini-2.5-pro"] = "gemini-3.1-pro-preview"
    };

    private static readonly Dictionary<string, string> LegacyClaudeModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-3-5-haiku-20241022"] = "claude-haiku-4-5",
        ["claude-haiku-4-5-20251001"] = "claude-haiku-4-5",
        ["claude-sonnet-4-20250514"] = "claude-sonnet-4-6",
        ["claude-sonnet-4-5"] = "claude-sonnet-4-6",
        ["claude-sonnet-4-5-20250214"] = "claude-sonnet-4-6"
    };

    private static readonly Dictionary<string, string> LegacyGpt5Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5-nano"] = "gpt-5.4-nano",
        ["gpt-5-mini"] = "gpt-5.4-mini"
    };

    private static readonly Dictionary<string, string> LegacyOllamaModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qwen2.5:7b"] = "qwen3:8b",
        ["llama3.2:latest"] = "llama3.1:8b",
        ["qwen2.5:0.5b-base"] = "qwen3:8b"
    };

    // Engine settings — runtime plaintext values (never serialized directly)
    [JsonIgnore] public string? GeminiApiKey { get; set; }
    [JsonIgnore] public string? AnthropicApiKey { get; set; }
    [JsonIgnore] public string? OpenAiApiKey { get; set; }
    [JsonIgnore] public string? OpenRouterApiKey { get; set; }

    // Encrypted on-disk representations (used only for JSON serialization)
    public string? GeminiApiKeyEncrypted { get; set; }
    public string? AnthropicApiKeyEncrypted { get; set; }
    public string? OpenAiApiKeyEncrypted { get; set; }
    public string? OpenRouterApiKeyEncrypted { get; set; }
    public string PredictionEngine { get; set; } = "gemini";
    public string GeminiModel { get; set; } = DefaultGeminiModel;
    public string ClaudeModel { get; set; } = DefaultClaudeModel;
    public string Gpt5Model { get; set; } = DefaultGpt5Model;

    // Local LLM (Ollama) settings — no API key needed
    public string OllamaModel { get; set; } = DefaultOllamaModel;
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    // OpenRouter settings — proxies hundreds of models via one OpenAI-compatible API
    public string OpenRouterModel { get; set; } = "google/gemini-flash-2.0";

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
    public bool RollingContextEnabled { get; set; } = true;

    // Learning: opt-in logging of accepted/dismissed completions for few-shot learning.
    // Off by default — user must explicitly enable in settings.
    public bool LearningEnabled { get; set; } = false;
    public bool LimitEnabled { get; set; } = true;
    public bool LearningV2Enabled { get; set; } = true;
    public bool LearningContextV2Enabled { get; set; } = true;
    public bool LearningRerankerEnabled { get; set; } = true;
    public bool LearningUiV2Enabled { get; set; } = true;

    // Style profile: periodically analyzes accepted completions to generate a writing
    // style summary injected into the system prompt. Only active when LearningEnabled is true.
    public bool StyleProfileEnabled { get; set; } = true;
    public int StyleProfileInterval { get; set; } = 30;

    // Number of suggestions per request (1 = single prediction only, 2-5 = extras for Ctrl+Up/Down cycling)
    // Lower values reduce API costs and latency; higher values give more choices.
    public int MaxSuggestions { get; set; } = 3;

    // First-launch consent: must be true before the app activates input processing.
    public bool ConsentAccepted { get; set; } = false;
    public bool OnboardingCompleted { get; set; } = false;

    // Suggestion panel color theme ("midnight", "ember", "forest", "rose", "slate")
    public string ThemeId { get; set; } = "midnight";

    // Per-app availability rules.
    // Default: enabled everywhere except processes explicitly blocked by the user.
    public string AppFilteringMode { get; set; } = PerAppSettings.AllowAllExceptBlocked;
    public List<string> BlockedProcesses { get; set; } = [];
    public List<string> AllowedProcesses { get; set; } = [];

    // User-customizable system prompt (empty = use default)
    public string? CustomSystemPrompt { get; set; }

    // The default system prompt (used when CustomSystemPrompt is empty)
    public static string DefaultSystemPrompt =>
        "You are a keyboard autocomplete engine. Predict the next words someone is typing.\n\n" +
        "GROUNDING (most important — read this first):\n" +
        "- <screen_context> = what is visible on screen right now. This is your PRIMARY source of truth for topic, tone, and intent.\n" +
        "- <recently_written> = text written earlier in this session. Use it to maintain the train of thought and avoid repeating earlier phrases.\n" +
        "- <complete_this> = the text being typed RIGHT NOW. Your output is appended directly after it.\n" +
        "- <user_style_hints> (when present) = OPTIONAL hints about writing habits. Use lightly — never override what makes sense given screen context.\n" +
        "- In a conversation, predict a natural reply to the MOST RECENT message from the other person. Do not parrot or rephrase what was already said.\n" +
        "- ONLY predict content grounded in the provided context. Do not introduce new topics, names, or facts.\n\n" +
        "OUTPUT FORMAT (strict — violating these makes output unusable):\n" +
        "- Output ONLY continuation text. Nothing else.\n" +
        "- NEVER repeat any part of the text inside <complete_this>.\n" +
        "- NEVER wrap output in quotation marks, backticks, or any formatting.\n" +
        "- NEVER include explanations, alternatives, or meta-commentary.\n" +
        "- If not confident, output NOTHING. Empty is always better than wrong.\n\n" +
        "PREDICTION QUALITY:\n" +
        "- Predict the SINGLE most likely continuation. Favor natural, expected phrasing.\n" +
        "- Match tone and formality to the existing text exactly.\n" +
        "- Complete the current sentence or thought. Stop at a natural ending point.\n" +
        "- When typed text is very short (under ~5 words), prefer shorter, safer completions.\n" +
        "- NEVER use filler words like 'honestly', 'basically', 'actually', 'literally', 'definitely' unless they appear in the screen context or recently written text.\n" +
        "- Never predict UI elements or placeholder text (e.g. 'Type a message', 'Send').\n" +
        "- Always complete with whole words. Never end mid-word.";

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
                config.GeminiApiKey = ApiKeyEncryption.Decrypt(config.GeminiApiKeyEncrypted);
                config.AnthropicApiKey = ApiKeyEncryption.Decrypt(config.AnthropicApiKeyEncrypted);
                config.OpenAiApiKey = ApiKeyEncryption.Decrypt(config.OpenAiApiKeyEncrypted);
                config.OpenRouterApiKey = ApiKeyEncryption.Decrypt(config.OpenRouterApiKeyEncrypted);

                // Migrate legacy plaintext keys from old config format.
                // Old configs stored keys as "GeminiApiKey" etc. directly in JSON.
                // JsonSerializer will ignore them (marked [JsonIgnore]) but we can
                // detect them by checking if the raw JSON contains the old field names
                // while the encrypted fields are empty.
                MigrateLegacyKeys(json, config);

                config.Validate();
                return config;
            }
        }
        catch (Exception ex)
        {
            // Log the error so corrupt configs are diagnosable, not silently lost
            try
            {
                var logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Keystroke", "config-error.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:o}] Config load failed: {ex}\n");
            }
            catch (IOException) { }
        }
        return new AppConfig();
    }

    /// <summary>
    /// Clamp config values to sane ranges to prevent crashes from corrupt/edited config files.
    /// </summary>
    private void Validate()
    {
        DebounceMs = Math.Clamp(DebounceMs, 50, 5000);
        FastDebounceMs = Math.Clamp(FastDebounceMs, 20, 2000);
        MinBufferLength = Math.Clamp(MinBufferLength, 1, 20);
        Temperature = Math.Clamp(Temperature, 0.0, 2.0);
        MaxSuggestions = Math.Clamp(MaxSuggestions, 1, 5);
        MaxOutputTokens = Math.Clamp(MaxOutputTokens, 1, 2000);
        StyleProfileInterval = Math.Clamp(StyleProfileInterval, 10, 200);
        AppFilteringMode = PerAppSettings.NormalizeMode(AppFilteringMode);
        BlockedProcesses = PerAppSettings.NormalizeProcessList(BlockedProcesses);
        AllowedProcesses = PerAppSettings.NormalizeProcessList(AllowedProcesses);
        NormalizeModelSelections();
    }

    internal void NormalizeModelSelections()
    {
        GeminiModel = NormalizeModelSelection(GeminiModel, DefaultGeminiModel, SupportedGeminiModels, LegacyGeminiModels);
        ClaudeModel = NormalizeModelSelection(ClaudeModel, DefaultClaudeModel, SupportedClaudeModels, LegacyClaudeModels);
        Gpt5Model = NormalizeModelSelection(Gpt5Model, DefaultGpt5Model, SupportedGpt5Models, LegacyGpt5Models);
        OllamaModel = NormalizeModelSelection(OllamaModel, DefaultOllamaModel, SupportedOllamaModels, LegacyOllamaModels);
    }

    private static string NormalizeModelSelection(
        string? current,
        string defaultValue,
        HashSet<string> supported,
        Dictionary<string, string> legacyMap)
    {
        if (string.IsNullOrWhiteSpace(current))
            return defaultValue;

        if (legacyMap.TryGetValue(current, out var migrated))
            current = migrated;

        return supported.Contains(current) ? current : defaultValue;
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

            if (config.OpenRouterApiKey == null &&
                root.TryGetProperty("OpenRouterApiKey", out var orKey) &&
                orKey.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(orKey.GetString()))
            {
                config.OpenRouterApiKey = orKey.GetString();
                migrated = true;
            }
        }
        catch (Exception) { /* Malformed JSON in config — skip migration, caller falls back to defaults */ }

        // Re-save immediately so plaintext keys are replaced with encrypted versions
        if (migrated)
        {
            config.Save();
        }
    }

    public void Save()
    {
        // Encrypt API keys before writing to disk
        GeminiApiKeyEncrypted = ApiKeyEncryption.Encrypt(GeminiApiKey);
        AnthropicApiKeyEncrypted = ApiKeyEncryption.Encrypt(AnthropicApiKey);
        OpenAiApiKeyEncrypted = ApiKeyEncryption.Encrypt(OpenAiApiKey);
        OpenRouterApiKeyEncrypted = ApiKeyEncryption.Encrypt(OpenRouterApiKey);

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        // Atomic write: write to temp file, then rename over the original.
        // Prevents data loss if the process crashes mid-write.
        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    public static void EnsureExists()
    {
        if (!File.Exists(ConfigPath))
        {
            new AppConfig().Save();
        }
    }
}
