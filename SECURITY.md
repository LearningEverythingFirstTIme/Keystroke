# Security

Keystroke uses several Windows capabilities that overlap with tooling often associated with malware — keyboard hooks, OCR, foreground-app inspection, and clipboard-based text injection. It uses them for autocomplete, but the implementation is intentionally constrained. This document explains the threat model, the guardrails, and how to report a vulnerability.

## Reporting a vulnerability

Please email **support@keystroke-app.com** with a description of the issue and, if possible, a reproduction. I'll acknowledge within 72 hours and aim to have a fix or mitigation within 14 days for anything high-severity.

- **Do** use this channel for: remote code execution, privilege escalation, exfiltration paths, bypasses of the outbound privacy layer, license-key forgery, webhook abuse, or anything that could endanger a user's data.
- **Don't** use this channel for non-security bugs — open a [GitLab issue](https://gitlab.com/LearningEverythingFirstTIme/keystroke/-/issues) instead.
- Please don't publicly disclose before I've had a chance to respond. If you don't hear back within 72 hours, escalating publicly is fair.

**Safe harbor.** Good-faith research is welcome. I won't pursue legal action against anyone who:

- Tests only on their own installation or test accounts.
- Avoids accessing, modifying, or destroying other users' data.
- Doesn't disrupt the service for other users.
- Gives me a reasonable window to respond before publishing.

## Threat model

Keystroke must:

- Observe typed text in the focused application.
- Know which app the user is in, so suggestions can be filtered and categorized.
- Optionally read visible text on-screen for context.
- Insert accepted text back into the focused application.

Keystroke should not:

- Exfiltrate unrelated local data.
- Send raw window titles upstream.
- Persist more data than needed for local functionality.
- Override clipboard changes made by something else.
- Continue operating without explicit consent.

## Guardrails

- **Consent-first.** Hooks and the prediction pipeline do not activate until the user explicitly grants consent in onboarding.
- **Centralized outbound privacy layer.** All prompt-bound text — typed prefix, OCR context, rolling context, few-shot learning examples — flows through a single sanitization point before any cloud request is built. Sixteen detectors cover credit cards (Luhn-validated), SSNs, email, phone, IPv4/IPv6, JWTs, bearer tokens, provider-style API keys (GitHub, OpenAI, Google, AWS), private-key blocks, IBANs, and password/token assignments.
- **Block-on-high-risk.** If the actively-typed prefix looks like a secret, the prediction is suppressed entirely rather than redacted-and-sent.
- **No raw window titles outbound.** Titles stay local for app classification; prompts see only a coarse label like `chrome (Browser)`.
- **Clipboard hygiene.** Suggestion acceptance uses clipboard paste as the default injection path because it's the most reliable way to deliver text across varied Windows apps. The clipboard is restored after paste, but the restore is skipped if the clipboard changed externally during the operation. If paste fails, Keystroke falls back to SendInput and surfaces the fallback to the user rather than hiding it.
- **Opt-in learning, local only.** The adaptive learning system (Pro) is disabled by default. Accepted completions and derived profiles are stored in a local SQLite database and never transmitted.
- **DPAPI-encrypted secrets.** API keys are encrypted at rest using the Windows Data Protection API, scoped to the user account. Legacy plaintext keys from older versions are re-encrypted on startup.
- **Custom privacy rules.** Advanced users can define additional regex-based redaction/blocking patterns in `%AppData%/Keystroke/privacy-rules.json`.
- **No telemetry, no background updater.** All behavioral data stays on the machine. Updates are manual.

## License-key and webhook surface

Pro unlocks are enforced by an ECDSA P-256 signature check performed locally against an embedded public key — no network call, no account. The private key stays offline. If you find a way to forge a key, produce a key from public data, or recover the signing key from the binary, please report it.

License fulfillment runs through a serverless webhook (`website/api/lemon-webhook.js`) that receives Lemon Squeezy payment events, mints a signed key, and emails it via Resend. The webhook verifies the Lemon Squeezy HMAC signature, is idempotent on order ID, scrubs logs of key material, and keeps an audit trail. Webhook-related vulnerabilities (signature bypass, replay, log leakage, key exfiltration) are in scope.

## Review notes

If you are auditing this repository for legitimacy or reviewing the privacy and security posture, the most relevant files are:

- [`src/KeystrokeApp/Services/InputListenerService.cs`](src/KeystrokeApp/Services/InputListenerService.cs) — low-level input observation.
- [`src/KeystrokeApp/Services/OutboundPrivacyService.cs`](src/KeystrokeApp/Services/OutboundPrivacyService.cs) — the single egress sanitization layer.
- [`src/KeystrokeApp/Services/SensitiveDataDetector.cs`](src/KeystrokeApp/Services/SensitiveDataDetector.cs) — the detector rules.
- [`src/KeystrokeApp/Services/TextInjection.cs`](src/KeystrokeApp/Services/TextInjection.cs) — clipboard paste and SendInput fallback.
- [`src/KeystrokeApp/Services/LicenseService.cs`](src/KeystrokeApp/Services/LicenseService.cs) — offline ECDSA license validation.
- [`src/KeystrokeApp/App.TrayIcon.cs`](src/KeystrokeApp/App.TrayIcon.cs) — tray UI and the global toggle.
- [`src/KeystrokeApp/Views/ConsentDialog.xaml`](src/KeystrokeApp/Views/ConsentDialog.xaml) — consent surface.
- [`website/api/lemon-webhook.js`](website/api/lemon-webhook.js) — license fulfillment webhook.

See also [PRIVACY.md](PRIVACY.md) for what is captured and how to delete it, and [DATA_FLOW.md](DATA_FLOW.md) for the runtime pipeline from keystroke to suggestion acceptance.
