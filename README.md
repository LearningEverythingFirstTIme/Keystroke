# Keystroke

**A ghostwriter that lives in every app on your Windows machine.** System-wide AI autocomplete with five interchangeable engines, sub-300&nbsp;ms suggestions, and a privacy layer that scrubs your text before it ever leaves your keyboard.

Live at **[keystroke-app.com](https://www.keystroke-app.com)** · [Download v0.1.0](https://gitlab.com/LearningEverythingFirstTIme/keystroke/-/releases) · [Privacy](PRIVACY.md) · [Security](SECURITY.md) · [Data flow](DATA_FLOW.md)

![.NET 8](https://img.shields.io/badge/.NET-8.0-purple) ![Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-blue) ![Release](https://img.shields.io/badge/release-v0.1.0-brightgreen) ![License](https://img.shields.io/badge/license-MIT-lightgrey)

## What it is

Keystroke runs in the background, watches the text you're typing in the currently focused application, and proposes a completion inline — in Outlook, Slack, VS Code, Word, the terminal, the browser address bar, anywhere. Press **Tab** to accept, keep typing to ignore.

You pick the engine: **Google Gemini**, **Anthropic Claude**, **OpenAI GPT**, **OpenRouter** (hundreds of models behind one key), or **Ollama** running entirely on your machine. Before any cloud request is built, a centralized egress layer scrubs secrets and sensitive data — sixteen detectors covering credit cards, SSNs, API keys, private keys, JWTs, IBANs, and more. Raw window titles never leave the machine; outbound prompts see only a coarse app label like `code (Code)`.

A free tier (30 accepted completions per day, all engines, all privacy features) is enough to evaluate. **Pro** ($20 once, no subscription) unlocks unlimited usage and the adaptive learning stack — see [Free vs. Pro](#free-vs-pro).

## Status

**v0.1.0** is the current public release. Actively developed. Windows 10 (build 19041+) and Windows 11. .NET 8. Shipping surface is [GitLab releases](https://gitlab.com/LearningEverythingFirstTIme/keystroke/-/releases); the marketing site and checkout are at [keystroke-app.com](https://www.keystroke-app.com).

## Install

### Option 1 — Installer (recommended)

Grab the latest installer from [keystroke-app.com](https://www.keystroke-app.com) or directly from [GitLab releases](https://gitlab.com/LearningEverythingFirstTIme/keystroke/-/releases). Run it and walk through the onboarding wizard.

### Option 2 — Build from source

```bash
git clone https://gitlab.com/LearningEverythingFirstTIme/keystroke.git
cd keystroke
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Run directly for development:

```bash
dotnet run --project src/KeystrokeApp/KeystrokeApp.csproj
```

Build a portable single-file exe:

```bash
dotnet publish src/KeystrokeApp/KeystrokeApp.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build; the runtime is not required for self-contained builds. You'll also need an API key for at least one cloud engine *or* a local [Ollama](https://ollama.com) install.

### First-time setup

A guided onboarding wizard walks you through privacy consent, picking an engine, and verifying your API key. Gemini is the default — it has a generous free tier. After onboarding, Keystroke runs in the system tray; right-click the tray icon to open **Settings**.

## How it works

1. The input listener watches text you type in the focused app.
2. After a brief debounce, Keystroke builds a context bundle and passes it through the outbound privacy layer — secrets are redacted or, if the actively-typed text looks sensitive, the prediction is suppressed entirely.
3. The selected engine returns a streaming completion (one or several alternatives in parallel).
4. A glassmorphism panel appears near your cursor. **Tab** accepts, **Shift+Tab** accepts one word, **Esc** dismisses, **Ctrl+Up/Down** cycles alternatives.

## Free vs. Pro

| | **Free** | **Pro** ($20 one-time) |
|---|---|---|
| Accepted completions per day | 30 | Unlimited |
| All five engines (cloud + local Ollama) | ✅ | ✅ |
| Centralized privacy layer · DPAPI-encrypted keys · no telemetry | ✅ | ✅ |
| OCR screen reading for context | ✅ | ✅ |
| Rolling 500-char context window | ✅ | ✅ |
| Multi-suggestion cycling · per-app filtering · themes · custom prompts | ✅ | ✅ |
| Acceptance-based learning (few-shot from your accepted completions) | — | ✅ |
| LLM-powered style profiling per app category | — | ✅ |
| Deterministic vocabulary fingerprinting | — | ✅ |
| Correction-pattern detection · context-adaptive tuning | — | ✅ |
| Per-category intelligence scoring (0–100) with drift alerts | — | ✅ |
| Writing analytics dashboard | — | ✅ |

**How Pro works.** Pro is unlocked by a license key of the form `KS-…`, cryptographically signed offline with an ECDSA P-256 private key and verified locally against an embedded public key. No account, no server check, no trial countdown. Keys arrive by email immediately after purchase. Buy at [keystroke-app.com/#pricing](https://www.keystroke-app.com/#pricing).

## Engines

| Engine | Notes | API key |
|---|---|---|
| **Google Gemini** | Default. Generous free tier. Flash Lite (fastest), Flash (balanced), Pro (smartest). | [aistudio.google.com](https://aistudio.google.com/apikey) |
| **Anthropic Claude** | Haiku for speed, Sonnet for quality. | [console.anthropic.com](https://console.anthropic.com/) |
| **OpenAI GPT** | Nano/mini/full tiers. | [platform.openai.com](https://platform.openai.com/api-keys) |
| **OpenRouter** | Hundreds of models through one key; model catalog fetched live in Settings. | [openrouter.ai/keys](https://openrouter.ai/keys) |
| **Ollama** (Experimental) | 100% local, zero cloud calls, free. Needs 16&nbsp;GB+ RAM and a GPU to be usable. | None. [ollama.com](https://ollama.com) |

## Privacy & security

- **Centralized egress sanitization.** OCR text, rolling context, learning hints, and few-shot examples all pass through one outbound privacy layer before any cloud request.
- **Sixteen PII/secret detectors.** Credit cards (Luhn-validated), SSNs, email, phone, IPv4/IPv6, JWTs, bearer tokens, GitHub/OpenAI/Google/AWS-style keys, private-key blocks, IBANs, password/token assignments. High-risk active input suppresses the prediction instead of redacting.
- **DPAPI-encrypted API keys.** Encrypted at rest using Windows Data Protection API, scoped to your user account. Never on disk in plaintext. Legacy plaintext keys auto-migrate on upgrade.
- **No raw window titles outbound.** Titles stay local for app classification; prompts see coarse labels like `chrome (Browser)`.
- **No telemetry.** Completion feedback is stored locally in SQLite and auto-pruned. Learning stays on your machine.
- **Local-only option.** Ollama runs predictions entirely on-device with zero cloud calls.
- **Custom privacy rules.** Add your own regex-based redaction/blocking patterns at `%AppData%/Keystroke/privacy-rules.json`.

For the full threat model see [SECURITY.md](SECURITY.md); for what is captured and how to delete it see [PRIVACY.md](PRIVACY.md); for the end-to-end pipeline from keystroke to suggestion see [DATA_FLOW.md](DATA_FLOW.md).

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

All settings live at `%AppData%/Keystroke/config.json` and are editable through the Settings UI (right-click the tray icon). **Quick Presets** — Minimal, Balanced, Maximum — configure everything at once.

| Setting | Default | Notes |
|---|---|---|
| Engine | Gemini | `gemini`, `claude`, `gpt5`, `openrouter`, `ollama` |
| Max suggestions | 3 | `1` disables cycling; `2–5` enables `Ctrl+Up/Down` |
| Completion length | Extended | Brief / Standard / Extended / Unlimited |
| Temperature | 0.3 | Auto-adjusted per app category (code tightest, chat loosest) |
| Min characters | 3 | Characters typed before predictions start |
| Debounce | 300&nbsp;ms / 100&nbsp;ms fast | After word boundary / mid-word |
| OCR | Enabled | Screen reading for context injection |
| Learning (Pro) | Disabled | Opt-in acceptance tracking for few-shot style learning |
| Theme | Midnight | Midnight, Ember, Forest, Rose, Slate |

Current recommended models per engine are selected automatically on first run and can be switched in the Settings UI; the model list updates as providers release new versions.

## Architecture

The runtime pipeline is a straight line: **input listener → typing buffer → debounce → outbound privacy layer → engine (streaming, multi-alternative) → suggestion overlay → acceptance feedback → learning store.** Each stage has a single service that owns it.

```
src/
  KeystrokeApp/            # WPF app: tray, overlay, prediction pipeline
    Services/              # Engines, privacy, learning, OCR, context
    Views/                 # Suggestion panel, Settings, Onboarding
  KeystrokeHook/           # Low-level Windows input hook
tests/
  KeystrokeApp.Tests/      # Unit + integration tests
website/                   # Marketing site + Lemon Squeezy webhook
installer/                 # Inno Setup installer script
docs/                      # Reliability matrix, testing guide, plans
```

The engine layer is one interface (`IPredictionEngine`) with a shared base (`PredictionEngineBase`) that handles prompt assembly, rate limiting, anti-loop detection, and whole-word trimming. Adding a new engine is a single file.

Learning state lives in a local SQLite database (`LearningDatabase` / `LearningRepository`) — accepted completions, per-category style profiles, vocabulary fingerprints, correction patterns, and intelligence scores. It is never transmitted.

## Build & test

```bash
# Run the full test project
powershell -ExecutionPolicy Bypass -File .\test.ps1

# Skip rebuild/restore
powershell -ExecutionPolicy Bypass -File .\test.ps1 -NoBuild -NoRestore
```

Avoid `dotnet test Keystroke.sln` in this repo — the reliable paths are `.\test.ps1`, invoking the test project directly, or `.\build.ps1` (which builds the app projects and then runs the test project explicitly).

## Contributing & issues

Issues and merge requests live on [GitLab](https://gitlab.com/LearningEverythingFirstTIme/keystroke). Bug reports with a short repro and the active engine / OS build are the most useful; include anonymized logs from `%AppData%/Keystroke/logs` if relevant.

## License

MIT. The source is open and you can fork, modify, and redistribute under the MIT terms.

Pro features (the learning stack, unlimited usage) are gated at runtime by an ECDSA-signed license key check in [LicenseService.cs](src/KeystrokeApp/Services/LicenseService.cs). The enforcement lives in the open — nothing is obfuscated — and a determined fork could bypass it. If you use Pro features for real work, buying a key at [keystroke-app.com](https://www.keystroke-app.com/#pricing) is what keeps the project alive. $20, once, no subscription.
