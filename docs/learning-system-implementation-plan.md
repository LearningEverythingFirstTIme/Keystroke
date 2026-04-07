# Learning System Implementation Plan

## Goal

Upgrade Keystroke's learning system from "accepted completion memory" into a context-aware writing model that learns from what the user actually writes, distinguishes native voice from autocomplete preference, and adapts per context with confidence-aware online updates.

This plan is grounded in the current architecture:

- Event capture is centered in `CompletionFeedbackService` and `App.KeyboardHandlers.cs`.
- Retrieval and session hints live in `AcceptanceLearningService`.
- Prompt injection lives in `PredictionEngineBase` and the engine-specific few-shot builders.
- Learned summaries live in `StyleProfileService`, `VocabularyProfileService`, and `LearningScoreService`.
- The current UI entry point is the learning section in `SettingsWindow.xaml(.cs)`.

## Current State Summary

The current system already has a useful skeleton:

- `CompletionFeedbackService` writes accepted, dismissed, and ignored events to `%AppData%/Keystroke/completions.jsonl`.
- `AcceptanceLearningService` reads that file, keeps accepted and dismissed examples in memory, and selects few-shot examples with lexical matching, recency, category, process, and quality weighting.
- `PredictionEngineBase` injects negative examples, optional style/vocabulary hints, and session accepts into prompts.
- `StyleProfileService` and `VocabularyProfileService` regenerate coarse category profiles after every N accepted completions.
- `SettingsWindow` shows category-level counts, acceptance rate, quality, and intelligence cards.

The core limitations are structural:

1. The system mostly learns from model suggestions the user accepted, not from the text the user wrote on their own.
2. The context model stops at coarse category and process/window labels, so "Slack with Alex about launch docs" collapses into "Chat".
3. Retrieval is still a heuristic selector over accepted completions rather than a true ranked evidence system.
4. Accepted model text and native user-authored text are not separated by trust level.
5. Hint injection is gated by global thresholds rather than local confidence within the active context.

## Design Principles

1. Capture everything once, derive many views later.
   Raw learning events should be append-only and rich enough to support new ranking, UI, and analytics without changing the input hook again.

2. Prefer local, privacy-safe identifiers for contexts.
   Store normalized context keys and hashed subcontext IDs locally; never send raw recipient/thread/document labels outbound.

3. Separate evidence types.
   A manually written continuation is stronger than a full accept. A full accept left untouched is stronger than a full accept corrected immediately.

4. Roll out with dual-write, then cut over.
   Keep `completions.jsonl` working while the v2 event pipeline and retrieval layer prove themselves.

5. Make confidence explicit.
   Every hint or retrieved example should carry a confidence score so prompt injection becomes proportional instead of binary.

## Target Architecture

### 1. Unified learning event log

Add a new append-only store, for example `%AppData%/Keystroke/learning-events.v2.jsonl`, backed by a new `LearningEventService`.

Each event should include:

- `eventId`
- `sessionId`
- `suggestionId`
- `requestId`
- `timestampUtc`
- `eventType`
- `processName`
- `category`
- `safeContextLabel`
- `contextKeys`
- `typedPrefix`
- `shownCompletion`
- `acceptedText`
- `userWrittenText`
- `latencyMs`
- `cycleDepth`
- `editedAfterAccept`
- `untouchedForMs`
- `sourceWeight`
- `confidence`

Suggested `eventType` values:

- `suggestion_shown`
- `suggestion_full_accept`
- `suggestion_partial_accept`
- `suggestion_dismiss`
- `suggestion_typed_past`
- `manual_continuation_committed`
- `accepted_text_untouched`

### 2. Hierarchical context identity

Add a `ContextFingerprintService` that derives a stable hierarchy for each prediction and commit:

- Global: always available
- Category: current `AppCategory`
- App/process: process name
- Window family: normalized safe window label
- Subcontext: recipient/thread/document/project key

Start with deterministic extraction from process name, sanitized window title, and OCR/rolling context hints:

- Slack/Discord/Teams: conversation or channel key
- Email: recipient/thread key
- Docs/Notion/Word/Obsidian: document key
- IDE/terminal: project/repo key

Store only normalized opaque IDs locally, plus a human-readable safe label for UI when privacy-safe.

### 3. Evidence store and derived profiles

Build a `LearningRepository` that materializes typed events into ranked evidence records:

- `NativeWritingExample`
- `AssistAcceptanceExample`
- `NegativePreferenceExample`
- `ContextProfileSnapshot`

Evidence weighting should distinguish source quality:

- Manual continuation committed: `1.0`
- Full accept left untouched after commit window: `0.7`
- Partial accept: `0.4`
- Full accept immediately edited: `0.2`
- Dismiss/typed-past: negative preference evidence only

### 4. Candidate generation + reranking

Replace direct selection in `AcceptanceLearningService.GetExamples` with a two-stage retrieval pipeline:

1. Candidate generation:
   Pull recent evidence from matching global/category/app/subcontext buckets.
2. Reranking:
   Score each candidate with a richer feature set and return the top few examples plus per-example confidence.

Initial reranker features:

- Prefix lexical similarity
- Same subcontext match
- Same app/process match
- Same category match
- Recency decay
- Native-writing bonus
- Untouched-after-commit bonus
- High-quality acceptance bonus
- Dismissed/typed-past penalty
- Diversity penalty to avoid near-duplicates

Semantic similarity should be added behind an interface such as `IExampleSemanticScorer`.
Rollout recommendation:

- Phase 1: ship the new reranker with non-semantic features first.
- Phase 2: add embeddings once the event model and candidate pipeline are stable.

## Phased Implementation

## Phase 0: Instrumentation and compatibility scaffolding

Objective: prepare for a larger refactor without breaking the current system.

Changes:

- Add `sessionId`, `requestId`, and `suggestionId` generation in the prediction pipeline.
- Thread those IDs through `App.Prediction.cs`, `App.KeyboardHandlers.cs`, and the suggestion panel lifecycle.
- Introduce a new `LearningEventService` but keep `CompletionFeedbackService` intact.
- Dual-write existing accepted/dismissed actions to both stores.
- Add config flags for staged rollout:
  - `LearningV2Enabled`
  - `LearningContextV2Enabled`
  - `LearningRerankerEnabled`
  - `LearningUiV2Enabled`

Primary touch points:

- `src/KeystrokeApp/App.Prediction.cs`
- `src/KeystrokeApp/App.KeyboardHandlers.cs`
- `src/KeystrokeApp/Services/CompletionFeedbackService.cs`
- `src/KeystrokeApp/Services/AppConfig.cs`

Definition of done:

- Every shown suggestion has a stable ID.
- Accept/dismiss events are dual-written.
- No change to current user-visible behavior.

## Phase 1: Capture gold outcomes from real user writing

Objective: learn from what the user actually wrote, not just what they accepted.

Changes:

- Track `suggestion_shown` whenever a suggestion becomes fully visible.
- Track `suggestion_partial_accept` from `AcceptNextWord`.
- Detect `suggestion_typed_past` when the user keeps typing after a visible suggestion and the typed text diverges beyond the shown completion prefix.
- Detect `suggestion_dismiss` on Enter/Escape and cursor movement when a suggestion is visible.
- Add a commit detector that observes when a buffered phrase is completed by punctuation, Enter, or a context switch and logs `manual_continuation_committed`.
- Promote a `suggestion_full_accept` into `accepted_text_untouched` if it survives the watch window and commit boundary without correction.

Implementation notes:

- Extend `TypingBuffer` events or add a `LearningCaptureCoordinator` subscribed to buffer changes and clear events.
- Reuse the existing correction detector concept, but broaden it from "backspace soon after accept" to "did this accepted text survive until commit".
- Keep raw event capture lightweight and non-blocking; expensive derivation happens off the hot path.

Primary touch points:

- `src/KeystrokeApp/App.KeyboardHandlers.cs`
- `src/KeystrokeApp/App.Prediction.cs`
- `src/KeystrokeApp/Services/TypingBuffer.cs`
- New `src/KeystrokeApp/Services/LearningEventService.cs`
- New `src/KeystrokeApp/Services/LearningCaptureCoordinator.cs`

Definition of done:

- The system can answer: "What suggestion was shown?" and "What did the user actually do next?"
- Partial accepts and typed-past cases are no longer invisible.

## Phase 2: Introduce hierarchical context learning

Objective: make learning context-specific enough to feel personal.

Changes:

- Expand `ContextSnapshot` with derived context identity:
  - `Category`
  - `ProcessKey`
  - `WindowKey`
  - `SubcontextKey`
  - `ContextConfidence`
- Add `ContextFingerprintService` and call it when creating prediction context and when logging learning events.
- Refactor `RollingContextService` to segment by `SubcontextKey` instead of raw window equality alone.
- Update event storage to persist context hierarchy with each event.

Primary touch points:

- `src/KeystrokeApp/Services/ContextSnapshot.cs`
- `src/KeystrokeApp/Services/AppContextService.cs`
- `src/KeystrokeApp/Services/OutboundPrivacyService.cs`
- `src/KeystrokeApp/Services/RollingContextService.cs`
- New `src/KeystrokeApp/Services/ContextFingerprintService.cs`

Definition of done:

- Learning queries can target global, category, app, or subcontext buckets.
- Rolling context and learning evidence stop bleeding across unrelated conversations/documents as often.

## Phase 3: Replace example selection with a real retrieval pipeline

Objective: stop treating retrieval as a filtered list scan.

Changes:

- Split `AcceptanceLearningService` into:
  - `LearningRepository` for loading/indexing evidence
  - `LearningRetrievalService` for candidate generation
  - `LearningReranker` for scoring and diversity
- Keep the existing `GetExamples` API shape initially so engine integrations stay stable.
- Expand negative retrieval to include dismissals and typed-past evidence, not just explicit dismissals.
- Return structured metadata with each example:
  - `SourceType`
  - `ContextMatchLevel`
  - `Confidence`
  - `WasUntouched`

Reranker formula recommendation:

- 30% context match
- 20% native-writing/source trust
- 15% untouched survival
- 15% lexical similarity
- 10% recency
- 10% quality and acceptance preference

Tune this in code, but keep the weights isolated in one scorer.

Primary touch points:

- `src/KeystrokeApp/Services/AcceptanceLearningService.cs`
- New `src/KeystrokeApp/Services/LearningRepository.cs`
- New `src/KeystrokeApp/Services/LearningRetrievalService.cs`
- New `src/KeystrokeApp/Services/LearningReranker.cs`

Definition of done:

- Example retrieval is context-first and source-aware.
- The current engines still consume few-shot examples without breaking.

## Phase 4: Separate native writing style from assist preference

Objective: stop reinforcing autocomplete artifacts as if they were the user's real voice.

Changes:

- Split profile inputs into two channels:
  - Native writing profile: manual continuations and untouched committed text
  - Assist preference profile: accepted suggestions and partial accepts
- Refactor `StyleProfileService` to generate:
  - `GlobalNativeProfile`
  - `CategoryNativeProfiles`
  - `SubcontextNativeProfiles`
  - optional `AssistPreferenceNotes`
- Refactor `VocabularyProfileService` to compute fingerprints over native text first, then optionally blend assist preference with lower weight.
- Add decay to profile inputs so stale contexts fade naturally.

Implementation recommendation:

- Keep the current JSON outputs for backward compatibility, but extend the schema rather than replacing it immediately.
- Only regenerate full LLM-written summaries where necessary; deterministic stats should update incrementally.

Primary touch points:

- `src/KeystrokeApp/Services/StyleProfileService.cs`
- `src/KeystrokeApp/Services/StyleProfileData.cs`
- `src/KeystrokeApp/Services/VocabularyProfileService.cs`
- `src/KeystrokeApp/Services/VocabularyProfile.cs`

Definition of done:

- Style and vocabulary hints are no longer built solely from accepted model completions.
- Strong local patterns can appear even if global accepted count is low.

## Phase 5: Make hint injection online and confidence-aware

Objective: use the right hint strength for the current context instead of a global on/off gate.

Changes:

- Replace the `MinEntriesForHints` + `MinQualityForHints` global gate in `PredictionEngineBase` with per-context confidence.
- Add a `LearningHintBundle` builder that returns:
  - `StyleHint`
  - `VocabularyHint`
  - `SessionHint`
  - `PreferredClosings`
  - `AvoidPatterns`
  - `Confidence`
- Scale hint inclusion by confidence:
  - High confidence: include subcontext style + vocabulary + examples
  - Medium confidence: include category-level hints and one example
  - Low confidence: keep only session hint and anti-repetition
- Preserve existing engine contracts by building these hints centrally in `PredictionEngineBase`.

Primary touch points:

- `src/KeystrokeApp/Services/PredictionEngineBase.cs`
- `src/KeystrokeApp/Services/Gpt5PredictionEngine.cs`
- `src/KeystrokeApp/Services/GeminiPredictionEngine.cs`
- `src/KeystrokeApp/Services/ClaudePredictionEngine.cs`
- optionally `OpenRouterPredictionEngine.cs` and `OllamaPredictionEngine.cs`

Definition of done:

- Prompt shaping responds to local evidence confidence.
- Strong context-specific patterns surface earlier without overfitting globally.

## Phase 6: Upgrade the Learning UI around contexts

Objective: make learning legible and controllable in the way users actually experience it.

Changes:

- Expand the learning panel in `SettingsWindow` from category totals to context drill-downs.
- Add a context explorer with:
  - context name
  - context type
  - number of native examples
  - number of accepted assists
  - match rate
  - freshness
  - learned traits summary
- Add actions:
  - clear context
  - pin context
  - disable learning for context
  - reset only assist-preference data
- Add metrics that map to perceived value:
  - "match rate in Email"
  - "learned your closing style in Slack with Alex"
  - "confidence in this project context"

Implementation recommendation:

- Reuse the current intelligence card style for the top layer.
- Add a secondary panel or modal for context drill-down instead of trying to cram every context into the existing card row.

Primary touch points:

- `src/KeystrokeApp/Views/SettingsWindow.xaml`
- `src/KeystrokeApp/Views/SettingsWindow.xaml.cs`
- `src/KeystrokeApp/Services/LearningScoreService.cs`
- New view models for context summaries

Definition of done:

- Users can see what Keystroke has learned per context and manage it directly.

## Phase 7: Cutover, migration, and cleanup

Objective: move the system onto v2 without losing existing value.

Changes:

- Backfill old `completions.jsonl` accepted/dismissed records into the new evidence model as low-confidence legacy events.
- Keep `AcceptanceLearningService` reading legacy data during the migration window.
- Once v2 retrieval proves stable, swap the engines to the new retrieval service by default.
- Deprecate direct profile generation from `completions.jsonl`.
- Keep `completions.jsonl` for debugging until at least one release after cutover.

Definition of done:

- New users run entirely on v2.
- Existing users retain useful history, clearly marked as lower-confidence legacy evidence.

## Testing Plan

Add a dedicated learning test suite under `tests/KeystrokeApp.Tests`.

Recommended test groups:

### Event capture

- Full accept writes `suggestion_full_accept`
- Partial accept writes `suggestion_partial_accept`
- Typing past a visible suggestion writes `suggestion_typed_past`
- Enter/Escape/cursor movement with visible suggestion writes `suggestion_dismiss`
- Manual typed continuation at commit writes `manual_continuation_committed`
- Untouched accepted text is promoted correctly

### Context extraction

- Browser title refinement still maps to category correctly
- Slack/email/document/project subcontext extraction is stable
- Sensitive raw titles do not leak into outbound prompt labels

### Retrieval and reranking

- Same-subcontext native examples outrank generic category examples
- Untouched accepts outrank edited accepts
- Negative evidence suppresses repeated rejected patterns
- Diversity filter prevents near-duplicate few-shot examples

### Profiles and confidence

- Native-writing profile ignores low-trust assist artifacts
- Confidence falls when evidence is stale
- Low-confidence contexts do not inject aggressive hints

### Migration

- Legacy `completions.jsonl` entries import as usable but lower-confidence evidence
- Dual-write mode does not regress current stats or settings UI

## Recommended Execution Order

Use this order for implementation:

1. Phase 0 scaffolding
2. Phase 1 event capture
3. Phase 2 context hierarchy
4. Phase 3 retrieval/reranker
5. Phase 5 confidence-aware prompt injection
6. Phase 4 profile separation and online updates
7. Phase 6 UI
8. Phase 7 cleanup and cutover

Reasoning:

- Event capture and context identity are the foundation; everything else depends on them.
- Retrieval should switch before the profile rewrite so the biggest quality gains land early.
- Confidence-aware prompt assembly can deliver meaningful improvements even while profile generation is still mid-migration.
- UI should come after the model semantics are stable enough to explain honestly.

## Concrete File-Level Worklist

Expected new files:

- `src/KeystrokeApp/Services/LearningEventService.cs`
- `src/KeystrokeApp/Services/LearningCaptureCoordinator.cs`
- `src/KeystrokeApp/Services/ContextFingerprintService.cs`
- `src/KeystrokeApp/Services/LearningRepository.cs`
- `src/KeystrokeApp/Services/LearningRetrievalService.cs`
- `src/KeystrokeApp/Services/LearningReranker.cs`
- `src/KeystrokeApp/Services/LearningHintBundle.cs`
- `tests/KeystrokeApp.Tests/Learning/*.cs`

Expected major edits:

- `src/KeystrokeApp/App.Prediction.cs`
- `src/KeystrokeApp/App.KeyboardHandlers.cs`
- `src/KeystrokeApp/Services/TypingBuffer.cs`
- `src/KeystrokeApp/Services/ContextSnapshot.cs`
- `src/KeystrokeApp/Services/CompletionFeedbackService.cs`
- `src/KeystrokeApp/Services/AcceptanceLearningService.cs`
- `src/KeystrokeApp/Services/PredictionEngineBase.cs`
- `src/KeystrokeApp/Services/StyleProfileService.cs`
- `src/KeystrokeApp/Services/VocabularyProfileService.cs`
- `src/KeystrokeApp/Services/LearningScoreService.cs`
- `src/KeystrokeApp/Services/AppConfig.cs`
- `src/KeystrokeApp/Views/SettingsWindow.xaml`
- `src/KeystrokeApp/Views/SettingsWindow.xaml.cs`

## First Slice Recommendation

If this should be implemented incrementally, the best first slice is:

1. Add suggestion IDs and a new v2 event log
2. Capture full accept, partial accept, dismiss, and typed-past
3. Add manual continuation commit capture
4. Build a simple context hierarchy with subcontext keys
5. Swap `GetExamples` to a reranked candidate pipeline without touching engine APIs

That slice delivers the biggest quality improvement with the least UI churn, and it leaves the existing style/vocabulary system operational while better data starts accumulating immediately.
