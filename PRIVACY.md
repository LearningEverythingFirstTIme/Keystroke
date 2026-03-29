# Keystroke Privacy Policy

**Last updated:** March 29, 2026

Keystroke ("the App") is a system-wide AI text completion tool for Windows. Because the App monitors keyboard input and optionally captures screen content, we take your privacy seriously. This policy explains exactly what data the App collects, where it goes, and how you control it.

## Summary

- Keystroke runs locally on your computer. There are no Keystroke servers.
- Your keystrokes are processed locally. Only the text you are currently typing is sent to your chosen AI provider for prediction.
- API keys are encrypted on your device using Windows DPAPI. They are never transmitted to anyone other than the AI provider you selected.
- Sensitive patterns (credit card numbers, Social Security numbers, email addresses, phone numbers, and API keys) are automatically redacted before any data leaves your device.
- Learning data is off by default. You must opt in before any completions are saved to disk.
- The App contains no analytics, telemetry, advertising, or tracking of any kind.

---

## 1. Data the App Collects

### 1.1 Keyboard Input

The App uses a system-wide low-level keyboard hook (`SetWindowsHookEx`) to detect what you type in any application. Captured keystrokes are accumulated in a temporary in-memory buffer and are **never written to disk in raw form**. The buffer is cleared each time you press Enter, Escape, or switch applications.

### 1.2 Active Window Information

The App detects the name and window title of the application you are currently using (for example, "chrome" and "Gmail - Inbox"). This is used to:

- Adjust the tone and style of predictions (e.g., more formal in email, more precise in code editors).
- Categorize learning data if you have opted in to the Learning feature.

Window titles are **not** transmitted to AI providers. Only the process name (e.g., "chrome") is included in API requests for context.

### 1.3 Screen Text (Optional)

When the OCR feature is enabled in Settings, the App periodically captures a screenshot of the active window and extracts visible text using the Windows built-in OCR engine. This happens locally on your device. The extracted text is:

- Cached in memory for up to 12 seconds.
- Limited to 2,000 characters.
- Scrubbed of sensitive patterns (see Section 3) before being included in any API request.

You can disable OCR at any time in Settings. When disabled, no screen content is captured or transmitted.

### 1.4 Rolling Context

To provide continuity across multiple completions, the App maintains a short rolling buffer (up to 500 characters) of text you recently accepted via Tab or Ctrl+Right. This buffer:

- Exists only in memory.
- Is cleared when you switch applications, switch windows, or after 5 minutes of inactivity.
- Is scrubbed of sensitive patterns before being included in API requests.

---

## 2. Data Transmitted to Third Parties

### 2.1 AI Provider API Requests

Each time the App generates a prediction, it sends a request to the AI provider you selected in Settings. The App supports three providers:

| Provider | Endpoint |
|----------|----------|
| Google (Gemini) | `generativelanguage.googleapis.com` |
| Anthropic (Claude) | `api.anthropic.com` |
| OpenAI (GPT) | `api.openai.com` |

**Each request contains:**

- The text you are currently typing (the completion target).
- The name of the active application process (e.g., "chrome", "code").
- The App's system prompt (behavioral instructions for the AI model).
- If OCR is enabled: up to 1,200 characters of screen text (PII-scrubbed).
- If Rolling Context is active: up to 400 characters of recently accepted text (PII-scrubbed).
- If Learning is enabled: up to 3 short examples of your previously accepted completions to help the model match your style.

**Each request also includes:**

- Your API key for authentication (transmitted securely via HTTPS headers).
- Model parameters (temperature, token limit) — these contain no personal data.

**The App does NOT send:**

- Your full keystroke history.
- Raw window titles or document names.
- Your name, email address, device identifiers, or any account information.
- Any data to any server other than the AI provider you selected.

### 2.2 How Providers Handle Your Data

Each AI provider has its own data handling policies:

- **Google Gemini API:** [https://ai.google.dev/terms](https://ai.google.dev/terms)
- **Anthropic Claude API:** [https://www.anthropic.com/policies/privacy](https://www.anthropic.com/policies/privacy)
- **OpenAI API:** [https://openai.com/policies/privacy-policy](https://openai.com/policies/privacy-policy)

We encourage you to review your chosen provider's privacy policy. In general, paid API usage (as opposed to free consumer products) is typically **not** used for model training by these providers, but you should verify this with the provider directly.

### 2.3 No Other Network Communication

The App makes no other network requests. There are no analytics services, crash reporters, update checkers, or telemetry of any kind. The only outbound connections are HTTPS requests to the single AI provider you have configured.

---

## 3. Automatic PII Redaction

Before any data is included in an API request or written to the learning file, the App automatically detects and replaces the following patterns:

| Pattern | Replaced With |
|---------|---------------|
| Credit card numbers (13-19 digit sequences) | `[CREDIT_CARD]` |
| Social Security numbers (XXX-XX-XXXX) | `[SSN]` |
| Email addresses | `[EMAIL]` |
| Phone numbers (US and international formats) | `[PHONE]` |
| IPv4 addresses | `[IP_ADDRESS]` |
| API keys and tokens (common prefixes) | `[API_KEY]` |
| Password fields (e.g., "password: ..." patterns) | `[PASSWORD_FIELD]` |

This redaction is applied to screen text, rolling context, and learning data. It is **not** applied to the text you are actively typing, since that text is needed for the AI to generate a relevant completion.

**Important:** While the PII filter catches common patterns, it cannot guarantee detection of all sensitive data in all formats. If you are working with highly sensitive information (medical records, financial data, classified documents), we recommend pausing Keystroke using the system tray icon or Ctrl+Shift+K.

---

## 4. Data Stored on Your Device

All local data is stored in `%AppData%\Keystroke\` (typically `C:\Users\<you>\AppData\Roaming\Keystroke\`).

### 4.1 Configuration (`config.json`)

Stores your settings: selected AI engine, model preferences, debounce timings, feature toggles, and your consent status. API keys are encrypted using Windows Data Protection API (DPAPI) and can only be decrypted by your Windows user account on your device.

### 4.2 Learning Data (`tracking.jsonl`) — Opt-In Only

**This file is only created if you enable Learning in Settings. It is off by default.**

When enabled, the App logs accepted and dismissed completions in a structured format. Each entry contains: a timestamp, the action taken, the PII-scrubbed prefix and completion text, the application category (e.g., "Chat", "Code"), and the application name (document-specific details are stripped from window titles before storage).

This data is used exclusively to provide few-shot examples in future predictions, helping the model match your writing style. It is stored locally and is never transmitted to any server. The file is automatically pruned to the most recent 2,000 entries.

You can clear all learning data at any time via Settings > Reset Learning Data.

### 4.3 Diagnostic Logs

The App writes diagnostic log files (`debug.log`, `gemini.log`, `claude.log`, `gpt5.log`, `ocr.log`, `learning.log`) for troubleshooting. These contain request metadata (timestamps, application names, response lengths, error messages) but do **not** contain full API request or response bodies.

### 4.4 No Cloud Storage

The App has no cloud storage, no user accounts, and no server-side component. All data listed above exists only on your local device.

---

## 5. Data Security

- **API key encryption:** Keys are encrypted at rest using Windows DPAPI, scoped to your Windows user account. They cannot be decrypted by other users on the same machine or by copying the config file to another device.
- **Transport security:** All API requests are made over HTTPS (TLS 1.2+).
- **No plaintext secrets:** API keys are never written to log files or included in error messages.
- **Memory handling:** Keystroke data is held in memory only for the duration needed and is cleared on application switch, buffer clear, or app exit.

---

## 6. Your Rights and Controls

### Pause or Disable Monitoring

- Press **Ctrl+Shift+K** at any time to toggle keystroke monitoring on or off.
- Right-click the system tray icon and select **Disable** to pause.
- Close the App entirely to stop all monitoring.

### Disable Optional Features

In Settings, you can independently control:

- **OCR Screen Reading** — toggle off to prevent screen content capture.
- **Learning** — toggle off to prevent any completion data from being saved to disk.

### Delete Your Data

- **Learning data:** Settings > Reset Learning Data, or delete `%AppData%\Keystroke\tracking.jsonl`.
- **All local data:** Delete the `%AppData%\Keystroke\` folder entirely.
- **API provider data:** Contact your AI provider directly regarding data they may have retained from API requests. See the links in Section 2.2.

### Withdraw Consent

Uninstalling the App or deleting the `%AppData%\Keystroke\` folder removes all locally stored data. To revoke consent, delete your data and stop using the App. If you reinstall, the consent dialog will appear again.

---

## 7. Children's Privacy

Keystroke is not intended for use by children under the age of 13. We do not knowingly collect data from children. If you believe a child has used the App, delete the local data folder as described in Section 6.

---

## 8. Sensitive Use Cases

Keystroke monitors all keyboard input in all applications while it is active. You should be aware that:

- Text typed into password fields may be sent to your AI provider for prediction. Most password fields do not emit characters to keyboard hooks, but behavior varies by application. When in doubt, pause Keystroke before entering passwords.
- If you work with protected health information (HIPAA), payment card data (PCI DSS), or other regulated data, you are responsible for ensuring that use of Keystroke and your chosen AI provider complies with applicable regulations.
- The PII redaction described in Section 3 is a best-effort safeguard and does not constitute a guarantee of complete data protection.

---

## 9. Changes to This Policy

We may update this Privacy Policy from time to time. Changes will be reflected in the "Last updated" date at the top of this document. Significant changes to data collection or transmission practices will be communicated through the App (for example, via an updated consent dialog).

---

## 10. Contact

If you have questions about this Privacy Policy or the App's data practices, please open an issue on the project's GitHub repository or contact the developer directly.

---

**In short:** Keystroke processes your keystrokes locally, sends only what's needed to the AI provider you chose, automatically redacts sensitive patterns, encrypts your credentials, stores nothing in the cloud, and gives you full control to pause, disable, or delete your data at any time.
