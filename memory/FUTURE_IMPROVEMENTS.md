# Future Improvements — From Claude Opus Review (2026-03-29)

> Priority-ordered roadmap from architecture review. Do NOT act on these yet — saved for planning.

## 1. Clipboard-Based Text Injection (BIGGEST WIN)
**Current:** 40 chars = 650ms (50ms delay + 15ms × 40 chars char-by-char)
**Proposed:** Ctrl+V clipboard injection = ~30ms

```csharp
private void InjectText(string text)
{
    Dispatcher.Invoke(() => Clipboard.SetText(text));
    Task.Run(async () =>
    {
        await Task.Delay(30);
        var sim = new WindowsInput.InputSimulator();
        sim.Keyboard.ModifiedKeyStroke(
            WindowsInput.Native.VirtualKeyCode.CONTROL,
            WindowsInput.Native.VirtualKeyCode.VK_V);
    });
}
```
Should save/restore clipboard contents to be polite.

## 2. Scale MaxOutputTokens to Preset
**Current:** Hardcoded 300 tokens regardless of preset
**Proposed:**
| Preset | Current | Should Be |
|--------|---------|-----------|
| brief | 300 | 30 |
| standard | 300 | 60 |
| extended | 300 | 100 |
| unlimited | 300 | 200 |

One-line change that could cut Gemini response time in half for brief/standard.

## 3. Gemini Streaming (streamGenerateContent)
Replace `generateContent` with `streamGenerateContent`. Get chunks instead of waiting for full response — first words visible in ~150ms. Also sets up event-driven architecture for OCR.

## 4. OCR Context Provider Architecture
OCR should be a **context provider**, not part of prediction pipeline:

```
┌─────────────────────────────────────────┐
│ CONTEXT PROVIDERS (run independently)    │
│                                          │
│ TypingBuffer ──┐                         │
│ OCR Cache ─────┼──→ ContextSnapshot ──→ Prediction Engine
│ Window Title ──┘                         │
│ (updated on window focus change)         │
└──────────────────────────────────────────┘
```

- Create `ContextSnapshot` class: aggregates typing buffer + OCR text + window info
- OCR runs on its own trigger (focus change, not every keystroke), caches result
- Prediction engine pulls from cached snapshot — never waits for OCR
- Use `Windows.Media.Ocr` (built-in, GPU-accelerated) over Tesseract

## Build Order (Opus Recommendation)
1. Clipboard-based injection — immediate latency win
2. MaxOutputTokens per preset — trivial, real speedup
3. ContextSnapshot abstraction — enriches PredictAsync interface, prepares for OCR
4. Streaming Gemini — swap to streamGenerateContent, progressive text in panel
5. OCR context provider — Windows.Media.Ocr on focus-change events
