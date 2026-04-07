# Reliability Test Matrix

Use this checklist when validating Keystroke reliability changes.

## Core Acceptance

- [ ] Notepad: prediction appears, `Tab` accepts, clipboard restores.
- [ ] WordPad or Word: prediction appears, `Tab` accepts, no duplicate paste.
- [ ] Chrome text area: prediction appears, `Tab` accepts, no stale suggestion after continued typing.
- [ ] Discord or Slack: prediction appears, `Tab` accepts, `Esc` dismisses cleanly.
- [ ] VS Code: prediction appears, `Shift+Tab` accepts one word, remainder stays correct.
- [ ] Windows Terminal: prediction appears, accept/dismiss works without ghost UI.

## Injection Reliability

- [ ] Accept two suggestions in rapid succession: second accept is either serialized or safely skipped.
- [ ] Clipboard contains rich content before accept: original clipboard survives restore.
- [ ] Clipboard changes externally during accept: Keystroke does not overwrite the newer clipboard.
- [ ] Force clipboard contention during accept: fallback path or failure is traceable in `reliability.log`.

## Prediction Lifecycle

- [ ] Type quickly through multiple prefixes: stale completions never overwrite the newest suggestion.
- [ ] Keep typing while a slow prediction is running: panel eventually reflects the latest prefix.
- [ ] Trigger cache hit: cached suggestion shows and remains tied to the current prefix only.
- [ ] Alternatives load only for the active suggestion, never for an older one.

## Privacy And Suppression

- [ ] Password field: prediction is suppressed.
- [ ] Secret-like input (API key, bearer token, credit card sample): prediction is suppressed.
- [ ] Suppressed prediction logs a reason in `reliability.log`.

## Edge Environments

- [ ] Non-US keyboard layout: typed buffer matches expected characters.
- [ ] Dead-key/accent input: no corrupted buffer or stray suggestions.
- [ ] IME composition session: confirm current behavior and log any incorrect prediction timing.
- [ ] Elevated app window: confirm whether capture/injection still works and inspect traces.
- [ ] Remote desktop / VM session: accept path still behaves correctly.

## Trace Review

- [ ] `reliability.log` contains `prediction/state` transitions for each request.
- [ ] `reliability.log` contains injection start/completion details for each accept.
- [ ] Debug window shows recent reliability events on open.
