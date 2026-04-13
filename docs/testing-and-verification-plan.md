# Learning System Upgrade — Testing & Verification Plan

**Created**: April 2026 session  
**Scope**: Phase 1 (Correction Learning), Phase 2 (Per-Context Adaptive Settings), Phase 6 (Learning UI Upgrade)

---

## 1. Automated Tests Written

All 116 tests pass (0 errors). The following test files were added or expanded:

### CorrectionDetectorTests.cs (11 tests) — NEW
Covers the core post-acceptance correction capture mechanism:
- **Basic signals**: no interaction → no edit; backspace sets EditDetected; multiple backspaces counted
- **Character typing without backspace**: does NOT set EditDetected (backward-compatible quality scoring)
- **Replacement capture**: backspace + type → captures replacement text
- **Typo-in-replacement**: backspace after typing removes from replacement buffer, not deletion count
- **Typo past all typed**: backspacing through all replacement chars increments deletion count again
- **Watch window supersession**: second StartWatching cancels first, only second callback fires
- **No-op safety**: OnBackspace/OnCharacterTyped are safe when no watch is active
- **CorrectionType classification**: minor (≤2 backspaces/chars), truncated (deletion only), replaced_ending (deletion + replacement)

### LearningContextMaintenanceServiceTests.cs (7 tests) — NEW
Covers the ClearAssistData and ClearContext maintenance operations:
- **ClearAssistData selective removal**: removes only `suggestion_full_accept`, `suggestion_partial_accept`, `accepted_text_untouched` — keeps `manual_continuation_committed`, `suggestion_dismiss`, `suggestion_typed_past`
- **Cross-context isolation**: only the targeted context is affected
- **Edge cases**: empty context key is a no-op; nonexistent file doesn't throw
- **Case insensitivity**: context key matching is case-insensitive
- **ClearContext**: removes ALL events for a context (contrast with ClearAssistData)
- **InvalidateDerivedArtifacts**: deletes all 5 derived JSON files; doesn't throw when files are absent

### ContextAdaptiveProfileTests.cs (13 tests) — NEW
Covers the data models driving adaptive behavior:
- **HasSufficientData threshold**: exactly 10 events required (various accepted/dismissed combos)
- **LengthInstruction mapping**: all 4 presets map to correct word-count ranges
- **Unknown preset fallback**: falls back to extended instruction
- **Temperature semantics**: precision-tuned profiles have negative adjustment; variety-boosted have positive
- **CorrectionInfo model**: HasCorrection requires BackspaceCount > 0
- **CorrectionType classification theory**: parameterized test covering none/minor/truncated/replaced_ending
- **AdaptiveSettingsData freshness**: verifies staleness window behavior
- **CorrectionPatterns model**: empty state has no patterns, word replacement captures data correctly

### PredictionEngineBaseTests.cs (6 tests, 4 NEW)
Expanded with adaptive temperature integration tests:
- **No adaptive service**: returns category base temperature (Chat=0.5, Code=0.15, etc.)
- **Lower bound clamping**: temperature never goes below 0.10
- **No app context**: falls back to global Temperature property
- **All length presets valid**: every preset returns non-empty instruction text

### PromptPreviewBuilderTests.cs (FIX)
Fixed 2 pre-existing build errors caused by the new `correctionPatternService` parameter added during Phase 1. Both call sites now pass `correctionPatternService: null`.

---

## 2. What Can't Be Unit Tested (and How to Verify)

### Services with Hardcoded Paths

`CorrectionPatternService` and `ContextAdaptiveSettingsService` both construct their file paths in the constructor from `Environment.SpecialFolder.ApplicationData`. This means you can't redirect them to temp directories in tests.

**To make them fully testable**, refactor the constructors to accept optional path parameters (the same pattern `LearningContextMaintenanceService` already uses):

```csharp
// Current (hardcoded):
public CorrectionPatternService()
{
    var appData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keystroke");
    _patternPath = Path.Combine(appData, "correction-patterns.json");
    _dataPath = Path.Combine(appData, "tracking.jsonl");
    _logPath = Path.Combine(appData, "correction-patterns.log");
}

// Refactored (injectable):
public CorrectionPatternService(string? appDataPath = null)
{
    var appData = appDataPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keystroke");
    _patternPath = Path.Combine(appData, "correction-patterns.json");
    _dataPath = Path.Combine(appData, "tracking.jsonl");
    _logPath = Path.Combine(appData, "correction-patterns.log");
}
```

Apply the same pattern to `ContextAdaptiveSettingsService`. This is a non-breaking change — existing code passes no arguments. After this refactoring, you can write integration tests that:

1. Create a temp directory
2. Write synthetic `tracking.jsonl` data
3. Instantiate the service with the temp path
4. Call `Start()` + `OnCorrectionDetected()` / `OnAccepted()`
5. Wait for recomputation (the services use `Task.Run`)
6. Assert patterns/settings via `GetPatterns()` / `GetAllSettings()`

### Integration Tests to Write After Refactoring

**CorrectionPatternService integration**:
- Write 10+ correction events to tracking.jsonl with the same word replacement ("option" → "plan")
- Trigger `OnCorrectionDetected()` enough times to hit the interval
- Verify `GetPatterns()` returns `FrequentReplacements` containing "option" → "plan"
- Verify `GetCorrectionHint()` includes the replacement in hint text
- Verify truncation rate is computed correctly from truncated vs replaced events
- Verify 7-day staleness suppression (set LastUpdated 8 days ago, confirm null hint)

**ContextAdaptiveSettingsService integration**:
- Write 20+ accept/dismiss events grouped by subcontext
- Trigger `OnAccepted()` enough times to hit the interval
- Verify `GetSettings("ctx-1", "Chat")` returns correct AcceptRate
- Verify temperature thresholds: 80%+ accept rate + <600ms → -0.08; ≤35% accept → +0.12
- Verify length preset derivation: avg <5 words → "brief", <12 → "standard"
- Verify fallback chain: subcontext key → category → null
- Verify 14-day staleness suppression
- Verify `accepted_text_untouched` events are skipped (deduplication with `suggestion_full_accept`)

### SettingsWindow UI (Manual Verification)

The WPF UI is built imperatively in code-behind — no practical way to unit test it. Verify manually:

1. **Intelligence Cards**: Open Settings → Learning tab. Each category card should show:
   - Existing pills (samples, quality score)
   - NEW: Adaptive settings pills (length preset, temperature adjustment) when sufficient data exists
   - NEW: Correction pattern pills (corrections tracked count) when corrections exist
   - NEW: Learned traits summary line at the bottom of each card

2. **Context Cards**: Scroll down to the context breakdown. Each card should show:
   - Existing pills (native examples, assists, match rate)
   - NEW: Per-context learned traits summary (temperature tuning, length preset, truncation habits)
   - NEW: "Reset assist data" button (only visible when assist count > 0)

3. **Reset Assist Data**: Click the button. Confirm dialog should appear. After confirming:
   - Assist count should drop to 0
   - Native writing examples should be preserved
   - Dismiss/typed-past negative evidence should be preserved

4. **Adaptive Tuning Panel**: After the context breakdown, there should be an "Adaptive tuning" section:
   - Shows per-context adaptive profile cards
   - Each card has temperature adjustment pill (color-coded: green=precision, orange=variety)
   - Accept rate, quality, and length preset pills
   - Last-computed timestamp

---

## 3. Verifying Real-World Value

These features are deterministic (no LLM involved in the analysis), so you can verify value by checking the log files in `%APPDATA%/Keystroke/`:

### Correction Patterns (`correction-patterns.log`)
- After normal use, check if the service is detecting patterns
- Look for log lines like: `Chat: 12 corrections, 4 truncations, 2 word replacements`
- If no corrections appear after many acceptances, the CorrectionDetector watch window may not be triggering — check that `App.KeyboardHandlers.cs` calls `_correctionDetector.OnBackspace()` and `_correctionDetector.OnCharacterTyped(c)` during the watch window

### Adaptive Settings (`context-adaptive.log`)
- Look for recomputation log lines: `Recomputing from N events...`
- Check that contexts are appearing: `Recomputation complete: 3 contexts, 2 categories`
- If no recomputations happen, verify that `OnAccepted()` is being called (check the interval setting — it's `StyleProfileInterval / 3`)

### Prompt Impact
- Use the Prompt Preview in Settings to see whether correction hints are injected
- Look for text like: `Correction patterns: User prefers "plan" over "option" (3x).`
- Check that the length instruction in the system prompt changes per context (e.g., "Write 3-5 words" in one context vs "Write 15-30 words" in another)

---

## 4. Guidance for the Next AI Model

### Architecture Quick-Start

Read these files in this order to understand the learning pipeline:

1. **`ContextSnapshot.cs`** — The data bundle available at prediction time
2. **`LearningEventService.cs`** (bottom half) — `LearningEventRecord` model, including the 4 correction fields
3. **`CorrectionDetector.cs`** — How corrections are captured during the 1500ms watch window
4. **`LearningCaptureCoordinator.cs`** — Wires correction data into tracking.jsonl events
5. **`CorrectionPatternService.cs`** — Extracts word replacements, avoided words, truncation rate
6. **`ContextAdaptiveSettingsService.cs`** — Computes per-context temperature/length from accept/dismiss history
7. **`PredictionEngineBase.cs`** (lines 485-514, 365-373) — Where adaptive settings affect prediction
8. **`App.KeyboardHandlers.cs`** — Where all services are triggered on acceptance
9. **`App.xaml.cs`** — Where all services are declared, started, and wired

### Data Flow

```
User accepts completion
  → CorrectionDetector.StartWatching(callback)
  → [1500ms watch window: OnBackspace() / OnCharacterTyped()]
  → callback fires with CorrectionInfo

LearningCaptureCoordinator receives CorrectionInfo
  → Appends to tracking.jsonl with 4 correction fields
  → Calls CorrectionPatternService.OnCorrectionDetected()
  
App.KeyboardHandlers calls on every acceptance:
  → ContextAdaptiveSettingsService.OnAccepted()

After N events, services recompute (background Task.Run):
  → CorrectionPatternService → correction-patterns.json
  → ContextAdaptiveSettingsService → context-adaptive-settings.json

At prediction time (PredictionEngineBase):
  → GetDynamicTemperature() adds adaptive temp adjustment
  → GetEffectiveLengthInstruction() returns per-context length
  → BuildLearningHints() includes correction hint text

At UI time (SettingsWindow):
  → Intelligence cards show adaptive/correction pills
  → Context cards show learned traits summary
  → Adaptive tuning panel shows per-context profiles
```

### Key Thresholds to Know

| Threshold | Value | Location |
|-----------|-------|----------|
| Correction watch window | 1500ms | CorrectionDetector.WatchWindowMs |
| Min corrections for patterns | 5 | CorrectionPatternService.MinCorrectionsForAnalysis |
| Min replacement frequency | 2 | CorrectionPatternService.MinReplacementFrequency |
| Pattern staleness | 7 days | CorrectionPatternService.MaxPatternAge |
| Min events for adaptation | 10 | ContextAdaptiveSettingsService.MinEventsForAdaptation |
| Min events per category | 15 | ContextAdaptiveSettingsService.MinEventsPerCategory |
| Adaptive staleness | 14 days | ContextAdaptiveSettingsService.MaxSettingsAge |
| Temp clamp range | [0.10, 0.70] | PredictionEngineBase.GetDynamicTemperature |
| Precision threshold | ≥80% accept + <600ms | ContextAdaptiveSettingsService.ComputeProfile |
| Variety threshold | ≤35% accept | ContextAdaptiveSettingsService.ComputeProfile |
| Truncation hint threshold | ≥30% rate | CorrectionPatternService.BuildHintText |

### Known Limitations

1. **CorrectionPatternService and ContextAdaptiveSettingsService have hardcoded paths** — See Section 2 above for the refactoring needed to make them injectable. This is the highest-value testability improvement remaining.

2. **ComputeProfile is private static** — The core adaptation math in `ContextAdaptiveSettingsService.ComputeProfile()` could be made `internal` with `[InternalsVisibleTo]` to enable direct unit testing of temperature adjustment thresholds and length preset derivation without file I/O.

3. **AnalyzeCorrections is private static** — Same story for `CorrectionPatternService.AnalyzeCorrections()`. Making it internal would let you test word replacement extraction, avoided word detection, and truncation rate calculation directly.

4. **No telemetry on adaptive impact** — There's currently no way to measure whether adaptive temperature/length changes actually improve accept rates over time. A future phase could add A/B logging: record what the adaptive adjustment was at prediction time, then measure whether the accept rate improves when adjustments are active vs. when they're not.

5. **SettingsWindow is code-behind, not MVVM** — The UI construction is imperative (BuildContextTraitsSummary, PopulateAdaptiveTuningPanel, etc.). This is fine for the current scope but makes automated UI testing impractical. If the UI grows much more complex, extracting view models would help.

### Recommended Next Steps

1. **Refactor paths** in CorrectionPatternService and ContextAdaptiveSettingsService (30 min)
2. **Write integration tests** for both services using temp directories (1-2 hours)
3. **Make ComputeProfile/AnalyzeCorrections internal** and add direct unit tests (1 hour)
4. **Add adaptive impact logging** — record temp adjustment + length preset in each prediction event, compare accept rates with/without adaptation (design + implement: half day)
5. **Dog-food for 1 week** — use the app normally, then review the generated `correction-patterns.json` and `context-adaptive-settings.json` to see if the extracted patterns match your actual preferences
