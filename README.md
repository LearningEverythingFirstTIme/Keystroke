# Keystroke

A system-wide AI autocomplete for Windows. Keystroke runs in the background, watches what you type in any application, and suggests completions powered by your choice of AI engine — Google Gemini, Anthropic Claude, OpenAI GPT, OpenRouter, or local models via Ollama.

![.NET 8](https://img.shields.io/badge/.NET-8.0-purple) ![Windows](https://img.shields.io/badge/platform-Windows-blue) ![Gemini](https://img.shields.io/badge/AI-Gemini%203.1-orange) ![Claude](https://img.shields.io/badge/AI-Claude%20Haiku%204.5-blueviolet) ![GPT](https://img.shields.io/badge/AI-GPT--5.4-green) ![OpenRouter](https://img.shields.io/badge/AI-OpenRouter-red) ![Ollama](https://img.shields.io/badge/AI-Ollama%20(local)-gray)

## How it works

1. A low-level keyboard hook captures your keystrokes across all applications
2. After a brief debounce, your typed text is sent to the AI engine along with context from the active window
3. A suggestion panel appears near your cursor with the predicted completion
4. Press **Tab** to accept, **Shift+Tab** to accept one word at a time, **Esc** to dismiss, or just keep typing

## Features

### Core
- **System-wide** — works in any text field (browsers, chat apps, editors, email clients, etc.)
- **5 engines** — Google Gemini, Anthropic Claude, OpenAI GPT, OpenRouter (hundreds of models), and local Ollama
- **Streaming predictions** — suggestions appear progressively as the AI responds
- **Animated glassmorphism panel** — acrylic-blur overlay with smooth fade-in/slide-up animations
- **Draggable overlay** — grab the suggestion panel to reposition it; it follows your mouse on the next prediction
- **Word-by-word acceptance** — press `Shift+Tab` or `Ctrl+Right` to accept one word at a time
- **Multi-suggestion cycling** — press `Ctrl+Down` / `Ctrl+Up` to browse alternative completions
- **5 color themes** — Midnight, Ember, Forest, Rose, and Slate panel themes

### Intelligence
- **OCR screen reading** — captures visible text on screen for better context (GPU-accelerated via Windows built-in OCR)
- **App-aware tone** — adjusts prediction style based on the active application (casual in Discord, professional in Outlook, syntax-aware in VS Code)
- **Acceptance-based learning** — learns from completions you accept to match your style using few-shot conversation turns
- **Adjacent category matching** — learning examples from stylistically similar apps (Chat/Email, Code/Terminal) improve suggestions even in new contexts
- **Style profiling** — LLM-powered analysis of your accepted completions to build a natural-language description of your writing style, per app category
- **Vocabulary fingerprinting** — deterministic analysis of your word choices, preferred phrases, sentence structure, and formality level to guide predictions
- **Intelligence scoring** — per-category 0-100 score tracking learning quality across Volume, Quality, Accept Rate, and Richness, with drift detection
- **Post-edit detection** — detects when you immediately backspace after accepting a suggestion to signal unwanted completions
- **Rolling context window** — remembers your last 500 characters of accepted text for topic continuity across multiple completions

### Performance
- **Smart debounce** — triggers instantly on word boundaries (space, period), fast 100ms debounce mid-word
- **LRU prediction cache** — repeated or backspaced prefixes get instant results
- **Adaptive token caps** — short prefixes request fewer tokens for faster, more precise completions
- **Dynamic temperature** — automatically adjusts creativity: precise (0.1) for code/terminal, balanced (0.25-0.3) for documents, flexible (0.35) for chat
- **Anti-loop detection** — prevents the model from echoing text you've already written
- **Whole-word trimming** — completions always end on clean word boundaries
- **Rate limiting** — graceful backoff on API rate limits across all engines

### Security and privacy
- **DPAPI-encrypted API keys** — keys are encrypted at rest using Windows Data Protection API, never stored in plaintext
- **PII filtering** — Luhn-validated credit card redaction, SSN/email/phone scrubbing before data leaves the device
- **No telemetry** — all data stays on your machine; acceptance tracking is local-only JSONL
- **Consent-first** — keystroke monitoring only activates after explicit user consent on first launch
- **Auto-pruning logs** — log files and tracking data are automatically pruned to prevent unbounded disk usage

## Requirements

- Windows 10 (build 19041+) or Windows 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (not required for self-contained builds)
- An API key for at least one cloud engine, **or** a local [Ollama](https://ollama.com) installation:
  - [Google Gemini](https://aistudio.google.com/apikey) (default, free tier available)
  - [Anthropic Claude](https://console.anthropic.com/)
  - [OpenAI](https://platform.openai.com/api-keys)
  - [OpenRouter](https://openrouter.ai/keys) (access hundreds of models through one API key)
  - [Ollama](https://ollama.com) (completely free, runs locally, no API key needed)

## Getting started

### Option 1 — Download a release

Download `KeystrokeApp.exe` from the [Releases](../../releases) page and run it directly. No installation required.

### Option 2 — Build from source

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```bash
git clone https://github.com/LearningEverythingFirstTIme/Keystroke.git
cd Keystroke/src
dotnet build KeystrokeApp
```

Run directly for development:

```bash
dotnet run --project KeystrokeApp
```

Build a portable single-file exe:

```bash
dotnet publish KeystrokeApp -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### First-time setup

1. Launch `KeystrokeApp.exe` — it runs in the system tray (look for the keyboard icon)
2. A consent dialog will appear on first launch — Keystroke only activates after you explicitly opt in
3. Right-click the tray icon and open **Settings**
4. Select your preferred AI engine and enter the corresponding API key (or select Ollama for local inference)
5. Start typing anywhere — suggestions will appear near your cursor

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Tab` | Accept the full suggestion |
| `Shift+Tab` | Accept the next word only |
| `Ctrl+Right` | Accept the next word only (alternative) |
| `Esc` | Dismiss the suggestion |
| `Ctrl+Down` | Next alternative suggestion |
| `Ctrl+Up` | Previous alternative suggestion |
| `Ctrl+Shift+K` | Toggle Keystroke on/off globally |

## Configuration

All settings are stored in `%AppData%/Keystroke/config.json` and are editable through the Settings UI.

| Setting | Default | Description |
|---|---|---|
| Engine | Gemini | `gemini`, `claude`, `gpt5`, `openrouter`, or `ollama` |
| Gemini Model | gemini-3.1-flash-lite-preview | Options: flash-lite (fastest), 3-flash, 2.5-flash, 2.5-pro (smartest) |
| Claude Model | claude-haiku-4-5-20251001 | Options: haiku-4-5 (fast), sonnet-4-5 (smart) |
| GPT Model | gpt-5.4-nano | Options: nano (fastest), mini (balanced), 5.4 (smart) |
| OpenRouter Model | google/gemini-flash-2.0 | Browse hundreds of models from the settings UI |
| Ollama Model | llama3.2:latest | Any model you've pulled locally; supports instruct and base models |
| Completion Length | Extended | Brief (3-5 words), Standard (8-15), Extended (15-30), Unlimited (30-50) |
| Temperature | 0.3 | Lower = more predictable, higher = more creative (auto-adjusted per app category) |
| Min Characters | 3 | Characters typed before predictions start |
| Debounce Delay | 300ms | Delay after word boundaries before predicting |
| Fast Debounce | 100ms | Delay mid-word before predicting |
| OCR | Enabled | Screen reading for context injection |
| Learning | Disabled | Opt-in acceptance tracking for few-shot style learning |
| Theme | Midnight | Panel color theme: Midnight, Ember, Forest, Rose, Slate |
| System Prompt | Built-in | Customizable prediction instructions |

## Settings UI

The Settings window provides an intuitive interface to customize Keystroke:

- **General Tab** — Engine selection, API keys, trigger behavior, debounce timing
- **Quality Tab** — Toggle smart features (rolling context, learning, OCR), creativity settings
- **Learning Tab** — Visual intelligence cards showing per-category scores (0-100), trends, and drift alerts
- **Advanced Tab** — Custom system prompt editing, theme selection

**Quick Presets:** Choose from Minimal, Balanced, or Maximum quality presets for instant configuration.

## Project structure

```
src/KeystrokeApp/
  App.xaml.cs                        # App startup, service wiring, consent dialog
  App.KeyboardHandlers.cs            # Key input, text injection, word-by-word acceptance
  App.Prediction.cs                  # Prediction pipeline, debounce, streaming, OCR
  App.TrayIcon.cs                    # System tray icon, context menu, global toggle
  Services/
    KeyboardHookService.cs           # Low-level keyboard hook (WH_KEYBOARD_LL)
    IPredictionEngine.cs             # Engine interface with streaming + alternatives
    PredictionEngineBase.cs          # Shared engine logic: prompts, rate limits, post-processing
    GeminiPredictionEngine.cs        # Google Gemini API
    ClaudePredictionEngine.cs        # Anthropic Claude API
    Gpt5PredictionEngine.cs          # OpenAI GPT API
    OpenRouterPredictionEngine.cs    # OpenRouter API (hundreds of models)
    OpenRouterModelService.cs        # Fetches and caches the OpenRouter model catalog
    OllamaPredictionEngine.cs        # Local Ollama (instruct + base model support)
    OcrService.cs                    # Windows.Media.Ocr screen capture
    ActiveWindowService.cs           # Foreground window detection via P/Invoke
    AppCategory.cs                   # App classification and tone hints
    AppConfig.cs                     # Configuration with DPAPI-encrypted key storage
    KeyProtection.cs                 # Windows DPAPI encryption/decryption for API keys
    PiiFilter.cs                     # PII scrubbing with Luhn-validated credit card detection
    TypingBuffer.cs                  # Keystroke accumulation buffer
    DebounceTimer.cs                 # Configurable debounce with cancellation
    PredictionCache.cs               # LRU cache (50 entries)
    AcceptanceTracker.cs             # JSONL logging of accepted/dismissed predictions
    AcceptanceLearningService.cs     # Few-shot prompting from accepted completions
    StyleProfileService.cs           # LLM-powered writing style analysis per category
    VocabularyProfileService.cs      # Deterministic vocabulary and structure fingerprinting
    LearningScoreService.cs          # Per-category intelligence scoring with drift detection
    PostEditDetector.cs              # Detects immediate backspace after acceptance
    RollingContextService.cs         # Tracks recently accepted text for continuity
    CursorPositionHelper.cs          # Mouse cursor position for panel placement
    ThemeDefinitions.cs              # Panel color themes (Midnight, Ember, Forest, Rose, Slate)
    ContextSnapshot.cs               # Context bundle for prediction requests
  Views/
    SuggestionPanel.xaml(.cs)        # Glassmorphism overlay with multi-suggestion support
    SettingsWindow.xaml(.cs)         # Settings UI with per-engine model selection
```

## Security

- **API keys** are encrypted at rest using Windows DPAPI (Data Protection API) — they are never stored in plaintext on disk. Legacy plaintext keys from older versions are automatically detected and re-encrypted on startup.
- **PII filtering** scrubs credit card numbers (Luhn-validated), SSNs, email addresses, and phone numbers before any data is sent to the AI engine. API key patterns (`sk-`, `AIzaSy`, `ghp_`, `AKIA`, AWS keys) are also stripped from outgoing text.
- **No telemetry** — all data stays on your machine. Acceptance tracking is local-only JSONL, auto-pruned to 2,000 entries.
- **Consent-first** — keystroke monitoring only activates after explicit user consent on first launch.
- **Local-only option** — use Ollama to run predictions entirely on your machine with zero cloud API calls.

## License

MIT
