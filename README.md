# Keystroke

A system-wide AI autocomplete for Windows. Keystroke runs in the background, watches what you type in any application, and suggests completions powered by your choice of AI engine — Google Gemini, Anthropic Claude, or OpenAI GPT.

![.NET 8](https://img.shields.io/badge/.NET-8.0-purple) ![Windows](https://img.shields.io/badge/platform-Windows-blue) ![Gemini](https://img.shields.io/badge/AI-Gemini%202.5-orange) ![Claude](https://img.shields.io/badge/AI-Claude-blueviolet) ![GPT](https://img.shields.io/badge/AI-GPT--5.4-green)

## How it works

1. A low-level keyboard hook captures your keystrokes across all applications
2. After a brief debounce, your typed text is sent to the AI engine along with context from the active window
3. A suggestion panel appears near your cursor with the predicted completion
4. Press **Tab** to accept, **Ctrl+Right** to accept one word at a time, **Esc** to dismiss, or just keep typing

## Features

- **System-wide** — works in any text field (browsers, chat apps, editors, email clients, etc.)
- **Multi-engine** — switch between Google Gemini, Anthropic Claude, and OpenAI GPT in Settings
- **Streaming predictions** — suggestions appear progressively as the AI responds
- **Word-by-word acceptance** — press `Shift+Tab` or `Ctrl+Right` to accept one word at a time
- **OCR screen reading** — captures visible text on screen for better context (GPU-accelerated via Windows built-in OCR)
- **App-aware tone** — adjusts prediction style based on the active application (casual in Discord, professional in Outlook, syntax-aware in VS Code)
- **Multi-suggestion cycling** — press `Ctrl+Down` / `Ctrl+Up` to browse alternative completions
- **Smart debounce** — triggers instantly on word boundaries (space, period), fast 100ms debounce mid-word
- **LRU prediction cache** — repeated or backspaced prefixes get instant results
- **Rolling context window** — remembers your last 500 characters of accepted text for topic continuity across multiple completions
- **Acceptance-based learning** — learns from completions you accept to match your style using few-shot conversation turns
- **Adjacent category matching** — learning examples from stylistically similar apps (Chat↔Email, Code↔Terminal) improve suggestions even in new contexts
- **Adaptive token caps** — short prefixes request fewer tokens for faster, more precise completions
- **Dynamic temperature** — automatically adjusts creativity: precise (0.1) for code/terminal, balanced (0.25-0.3) for documents, flexible (0.35) for chat
- **Anti-loop detection** — prevents the model from echoing text you've already written
- **Whole-word trimming** — completions always end on clean word boundaries
- **Acceptance tracking** — logs accepted/dismissed predictions to JSONL for future analysis (auto-pruned to 2,000 entries)
- **Global hotkey** — `Ctrl+Shift+K` to toggle on/off from anywhere
- **Configurable** — settings UI for engine, model, API keys, temperature, debounce timing, completion length, OCR toggle, and system prompt

## Requirements

- Windows 10 (build 19041+) or Windows 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (not required for self-contained builds)
- An API key for at least one supported engine:
  - [Google Gemini](https://aistudio.google.com/apikey) (default, free tier available)
  - [Anthropic Claude](https://console.anthropic.com/)
  - [OpenAI](https://platform.openai.com/api-keys)

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

Build a self-contained release exe:

```bash
dotnet publish KeystrokeApp -c Release -r win-x64 --self-contained
```

The exe will be at:
```
KeystrokeApp/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/KeystrokeApp.exe
```

### First-time setup

1. Launch `KeystrokeApp.exe` — it runs in the system tray (look for the keyboard icon)
2. Right-click the tray icon and open **Settings**
3. Select your preferred AI engine and enter the corresponding API key
4. Start typing anywhere — suggestions will appear near your cursor

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
| Engine | Gemini | Prediction engine: `gemini`, `claude`, or `gpt5` |
| Gemini Model | gemini-2.5-flash | Model used when engine is Gemini |
| Claude Model | claude-sonnet-4-5 | Model used when engine is Claude |
| GPT Model | gpt-4o | Model used when engine is GPT |
| Completion Length | Extended | Brief (3-5 words), Standard (8-15), Extended (15-30), Unlimited (30-50) |
| Temperature | 0.3 | Lower = more predictable, higher = more creative |
| Min Characters | 3 | Characters typed before predictions start |
| Debounce Delay | 300ms | Delay after word boundaries before predicting |
| Fast Debounce | 100ms | Delay mid-word before predicting |
| OCR | Enabled | Screen reading for context injection |
| System Prompt | Built-in | Customizable prediction instructions |

## Settings UI

The Settings window provides an intuitive interface to customize Keystroke:

- **🧠 General Tab** — Engine selection, API keys, trigger behavior, debounce timing
- **✨ Quality Tab** — Toggle smart features (rolling context, learning, OCR), creativity settings
- **📊 Learning Tab** — Visual statistics showing accepted completions by category
- **⚙️ Advanced Tab** — Custom system prompt editing

**Quick Presets:** Choose from Minimal, Balanced, or Maximum quality presets for instant configuration.

## Project structure

```
src/KeystrokeApp/
  App.xaml.cs                      # Main coordinator — hooks, buffers, predictions, OCR
  Services/
    KeyboardHookService.cs         # Low-level keyboard hook (WH_KEYBOARD_LL)
    GeminiPredictionEngine.cs      # Gemini API integration (streaming + alternatives)
    ClaudePredictionEngine.cs      # Anthropic Claude API integration
    Gpt5PredictionEngine.cs        # OpenAI GPT API integration
    OcrService.cs                  # Windows.Media.Ocr screen capture
    ActiveWindowService.cs         # Foreground window detection via P/Invoke
    AppCategory.cs                 # App classification and tone hints
    AppConfig.cs                   # Configuration with JSON persistence
    TypingBuffer.cs                # Keystroke accumulation buffer
    DebounceTimer.cs               # Configurable debounce with cancellation
    PredictionCache.cs             # LRU cache (50 entries)
    AcceptanceTracker.cs           # JSONL logging of accepted/dismissed predictions
    AcceptanceLearningService.cs   # Learns from accepted completions for few-shot prompting
    RollingContextService.cs       # Tracks recently accepted text for continuity
    CursorPositionHelper.cs        # Cross-process caret position detection
    ContextSnapshot.cs             # Context bundle for prediction requests
  Views/
    SuggestionPanel.xaml(.cs)      # Caret-positioned overlay with multi-suggestion support
    SettingsWindow.xaml(.cs)       # Settings UI
```

## License

MIT
