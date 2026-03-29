# Keystroke Project State

> Last updated: 2026-03-29 02:52

## Current Phase

**Phase 3: In Progress (Steps 3.1-3.4 done)** — Settings GUI, prompt tuning

**Next: Step 3.7 — App context detector (context injection)**

---

## Phase 3 Progress

| Step | What | Status |
|------|------|--------|
| 3.1 | Settings window skeleton | ✅ Done |
| 3.2 | Wire settings to config | ✅ Done |
| 3.3 | Completion length presets | ✅ Done |
| 3.4 | System prompt editor | ✅ Done |
| 3.5 | Panel positioning fix | ✅ Done — fixed bottom-right anchor |
| 3.6 | Fix duplicate panels | ✅ Fixed — single panel, show/hide |
| 3.7 | App context detector | 🔲 |
| 3.8 | Context-aware predictions | 🔲 |
| 3.9 | Polish | 🔲 |
| 3.10 | Run on startup | 🔲 |

### Known Bugs (Priority Order)
1. ~~**Panel cut off by screen edge**~~ — FIXED: fixed bottom-right position
2. ~~**Duplicate panels stacking**~~ — FIXED: single panel show/hide
3. ~~**Panel intermittent invisibility**~~ — FIXED: Show() before PositionBottomRight()

---

## How to Run

```
C:\Users\nickk\Projects\Keystroke\src\KeystrokeApp\bin\Debug\net8.0-windows\KeystrokeApp.exe
```

Or: `dotnet run` from `src\KeystrokeApp`

Config: `%APPDATA%\Keystroke\config.json`
Logs: `%APPDATA%\Keystroke\gemini.log`, `%APPDATA%\Keystroke\panel.log`

---

## Project Structure

```
src/KeystrokeApp/
├── App.xaml / .cs                    # Main coordinator, tray icon, event wiring
├── TestWindow.xaml / .cs             # Debug window
├── Views/
│   ├── SuggestionPanel.xaml / .cs    # Floating suggestion (completion-only display)
│   └── SettingsWindow.xaml / .cs     # Settings GUI (3 cards, auto-save)
└── Services/
    ├── KeyboardHookService.cs        # Low-level hook w/ SpecialKeyEventArgs.ShouldSwallow
    ├── TypingBuffer.cs               # Character accumulator
    ├── DebounceTimer.cs              # 300ms pause detection
    ├── CursorPositionHelper.cs       # Mouse position fallback
    ├── IPredictionEngine.cs          # Interface
    ├── GeminiPredictionEngine.cs     # Gemini 2.5 Flash (thinkingBudget=0)
    ├── DummyPredictionEngine.cs      # Hard-coded for testing
    └── AppConfig.cs                  # JSON config with presets, prompt, temperature
```

---

## NuGet Packages

| Package | Version | Purpose |
|---------|---------|---------|
| InputSimulatorCore | 1.0.5 | Text injection via SendInput |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | System tray icon |
| System.Drawing.Common | 10.0.5 | Generate tray icon programmatically |

---

## Key Technical Decisions & Gotchas

| Topic | Detail |
|-------|--------|
| Thinking tokens | gemini-2.5-flash has thinking ON by default. Must send `thinkingConfig.thinkingBudget = 0` or 90% of tokens get wasted on internal reasoning |
| Tab swallowing | Hook must return `(IntPtr)1` to block Tab from reaching apps, otherwise a tab character gets inserted |
| Panel visibility | Do NOT call HideSuggestion() in OnBufferChanged — it hides the panel before user sees it. Only hide on buffer clear (Enter/Escape) |
| Panel focus | Must use `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` or SendInput types into the panel instead of target app |
| GetCaretPos | Unreliable cross-process — returns wrong coords for most apps. Mouse position is the practical fallback |
| DebounceTimer thread | Fires on background thread — must use Dispatcher.Invoke for all UI updates |
| ComboBox styling | WPF ComboBox dropdowns are white by default — need explicit ComboBoxItem style with Foreground |
| File editing | When making complex changes, rewrite entire files with `write` tool instead of `edit` — the edit tool mangles large changes |

---

## Gemini Config

```json
{
  "GeminiApiKey": "AIzaSyAypnS8MjzY5RnNvRDyJ3ReVpIQkxfAwCI",
  "PredictionEngine": "gemini",
  "GeminiModel": "gemini-2.5-flash",
  "DebounceMs": 300,
  "MinBufferLength": 3,
  "Temperature": 0.5,
  "CompletionPreset": "extended"
}
```

---

## Documentation
- **Obsidian:** `Projects/Keystroke/Phase 3 - Settings & Context.md`
- **Daily logs:** `memory/2026-03-28.md`, `memory/2026-03-29.md`
- **Project spec:** `PROJECT_SPEC.md`
