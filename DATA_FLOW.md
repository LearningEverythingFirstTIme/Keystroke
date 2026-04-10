# Data Flow

This is the runtime path for a normal Keystroke prediction.

## 1. Capture

- `InputListenerService` receives keyboard events for the focused app
- `TypingBuffer` accumulates the current prefix
- Per-app rules decide whether Keystroke is allowed to run in that process

## 2. Prepare

- Debounce timers wait for either a word-boundary pause or a shorter mid-thought pause
- `AppContextService` captures the active process and window title
- `OutboundPrivacyService` sanitizes optional OCR text and rolling context
- `SensitiveDataDetector` can block the active typed prefix outright
- `ContextSnapshot` bundles the safe prompt context

## 3. Predict

- The selected engine builds a prompt from:
  - a safe app label
  - optional screen context
  - optional rolling context
  - optional learning hints
  - `<complete_this>` with the current prefix
- The provider returns a streamed or cached completion
- `SuggestionLifecycleController` tracks which request and suggestion are current so stale work is ignored

## 4. Show

- `SuggestionPanel` renders the active completion near the cursor
- Alternative suggestions can be attached for cycling
- Learning capture records that a specific suggestion id was shown for a specific request id

## 5. Accept or dismiss

- `Tab` accepts the full completion
- `Shift+Tab` or `Ctrl+Right` accept only the next word
- `Esc` or typing past the suggestion dismisses it

Acceptance now runs through a serialized path:

- Keystroke asks `TextInjection` to deliver the text
- Local suggestion/buffer state only advances after the injector reports delivery
- Clipboard restore failures, external clipboard changes, fallback injection, cancellation, and failure each have distinct outcomes
- Tray/debug surfaces report warning states in plain language

## 6. Learn locally

If learning is enabled:

- accepted and dismissed events are written locally
- style and vocabulary profiles are updated locally
- the rolling context window is updated from accepted text

## 7. User visibility

- The tray tooltip summarizes current app eligibility and the last acceptance result
- Settings include a live Privacy / Data Flow inspector showing the sanitized prompt shape that would be sent
