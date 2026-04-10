# Privacy

Keystroke is a local Windows autocomplete app. It watches text input in the focused app so it can suggest continuations, but it is designed to keep that behavior bounded, inspectable, and user-controlled.

## What Keystroke captures

- Active typed text in the focused app
- A coarse app label derived from the current process and app category
- Optional OCR text from the visible screen
- Optional recently accepted text from the current session
- Optional local learning data derived from accepted and dismissed suggestions

## What can leave the machine

When a prediction request is allowed, Keystroke may send these fields to the selected model provider:

- The current typed prefix
- A privacy-safe app label such as `code (Code)` or `olk (Email)`
- Optional OCR text
- Optional rolling context
- Optional learning hints built from local data

Keystroke does not send raw window titles in prompts.

## What is blocked or redacted

- High-risk active input is blocked entirely instead of being sent
- OCR, rolling context, learning hints, and few-shot examples are sanitized before cloud requests
- Sensitive patterns such as secrets, tokens, private keys, and common PII are scrubbed or blocked by the outbound privacy layer

## What stays local

- API keys are encrypted at rest with Windows DPAPI
- Learning logs and profiles stay on disk under `%AppData%/Keystroke`
- Reliability logs and debug logs stay local
- There is no telemetry, analytics, crash reporting, or update beacon

## User controls

- First-run consent gates all input processing
- `Ctrl+Shift+K` pauses Keystroke globally
- Per-app filtering lets you block apps or run from a tight allow list
- OCR, rolling context, and learning can each be disabled in Settings
- Settings now include a live Privacy / Data Flow inspector showing the prompt shape that would be sent
- Learning data can be reset from Settings

## Clipboard behavior

Accepting a suggestion uses clipboard paste by default so the text reaches the target app reliably. Keystroke then attempts to restore the previous clipboard contents. If the clipboard changes before restoration, Keystroke leaves it alone instead of overwriting newer clipboard data.
