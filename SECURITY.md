# Security

This project uses several Windows capabilities that overlap with tooling often associated with malware: keyboard hooks, OCR, foreground-app inspection, and clipboard-based text injection. Keystroke uses them for autocomplete, but the implementation is intentionally constrained.

## Threat model

Keystroke must:

- Observe typed text in the focused application
- Know which app the user is in so suggestions can be filtered and categorized
- Optionally read visible text for context
- Insert accepted text back into the focused application

Keystroke should not:

- Exfiltrate unrelated local data
- Send raw window titles upstream
- Persist more data than needed for local functionality
- Override clipboard changes made by something else
- Continue operating without explicit consent

## Guardrails

- Consent is required before hooks and prediction flow activate
- Outbound text goes through a centralized privacy layer before cloud requests
- Raw window titles stay local; prompts use safe app/category labels
- High-risk active input suppresses prediction instead of sending
- Clipboard restore is skipped if the clipboard changed externally
- Local learning data is opt-in and stored only on disk
- API keys are encrypted with Windows DPAPI
- There is no telemetry or background updater

## Injection behavior

Suggestion acceptance prefers clipboard paste because it is the most reliable way to deliver text into varied Windows apps. If clipboard-based injection fails, Keystroke can fall back to SendInput and now surfaces that condition to the user instead of hiding it in traces.

## Review notes

If you are evaluating this repository for legitimacy, the most relevant files are:

- [`src/KeystrokeApp/Services/InputListenerService.cs`](src/KeystrokeApp/Services/InputListenerService.cs)
- [`src/KeystrokeApp/Services/OutboundPrivacyService.cs`](src/KeystrokeApp/Services/OutboundPrivacyService.cs)
- [`src/KeystrokeApp/Services/SensitiveDataDetector.cs`](src/KeystrokeApp/Services/SensitiveDataDetector.cs)
- [`src/KeystrokeApp/Services/TextInjection.cs`](src/KeystrokeApp/Services/TextInjection.cs)
- [`src/KeystrokeApp/App.TrayIcon.cs`](src/KeystrokeApp/App.TrayIcon.cs)
- [`src/KeystrokeApp/Views/ConsentDialog.xaml`](src/KeystrokeApp/Views/ConsentDialog.xaml)
