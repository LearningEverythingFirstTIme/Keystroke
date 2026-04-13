# Writing Analytics Dashboard — Implementation Plan

## Goal

Add a dedicated analytics surface that turns Keystroke's existing learning data into a visible, ongoing narrative about how the personalized AI is working for the user. The current system learns silently; this feature makes that learning legible, trackable over time, and lightly gamified so paid users see concrete evidence of value every time they open Settings.

The dashboard should answer the three questions a paying user actually cares about:

1. **"Is it working?"** — acceptance rates, quality trends, learning progress
2. **"Is it getting better?"** — week-over-week comparisons, improvement trajectories
3. **"Where is it strongest?"** — per-context and per-category breakdowns, most-improved highlights

## Current State

### Data already captured

Every learning event in `tracking.jsonl` already contains:

- `TimestampUtc` — sub-second precision
- `EventType` — full_accept, partial_accept, dismiss, typed_past, manual_continuation, untouched
- `Category` — Chat, Email, Code, Document, Browser, Terminal
- `ContextKeys` — ProcessKey, WindowKey, SubcontextKey with human-readable labels
- `QualityScore` — 0.0–1.0 composite of latency, cycle depth, and post-edit behavior
- `LatencyMs` — time from suggestion visible to user action
- `CycleDepth` — how many alternatives the user scrolled through
- `EditedAfterAccept` / `CorrectionType` — whether and how the user changed the accepted text
- `ShownCompletion` / `AcceptedText` / `UserWrittenText` — the actual content (PII-scrubbed)

The legacy `completions.jsonl` file has a subset of these fields with `Action` instead of `EventType`.

### Aggregated data already computed

- `LearningScoreService` — 0–100 intelligence scores per category, 3-snapshot trend history
- `AcceptanceLearningService.GetStats()` — per-category accepted/dismissed counts, avg quality, top 8 context summaries
- `CorrectionPatternService` — truncation rate, word replacements, avoided words
- `VocabularyProfileService` — per-category vocabulary fingerprints with structural metrics
- `ContextAdaptiveSettingsService` — per-context accept rate, avg latency, avg word count

### What's missing

1. **Time-series aggregation.** No daily/weekly rollups exist. Every metric is computed as an all-time aggregate. There's no way to answer "how did last week compare to this week."
2. **Extended score history.** `LearningScores` keeps only 3 snapshots — enough for trend arrows, not enough for sparklines or trajectory visualization.
3. **Cross-session streak tracking.** The system doesn't know whether the user has used Keystroke on consecutive days.
4. **Milestone recognition.** No thresholds are defined for progress markers (first 50 accepts, 100, 500, etc.).
5. **Comparative framing.** Stats are presented as raw numbers, not as changes ("up 12% from last week").

## Design Principles

1. **Aggregate once, display many ways.** Compute daily rollups from raw events and persist them. The UI reads rollups, never raw events. This keeps the Settings window fast even after months of data.

2. **No new capture hooks.** Everything needed is already in `tracking.jsonl` and `completions.jsonl`. The analytics layer is purely a new read-side projection over existing data.

3. **Additive to the existing UI.** The analytics dashboard is a new nav tab, not a rework of the Personalization section. Intelligence cards and profile management stay where they are. Analytics is the "show me" companion to "let me configure."

4. **Charts without libraries.** Use WPF Canvas + Path/Polyline for sparklines, Rectangle for bar charts. No external charting dependencies. The dark theme and existing color system (green/blue/amber/muted) already provide the palette.

5. **Gamification without cringe.** Milestones are quiet factual callouts, not badges or celebrations. Streaks are informational. The tone should match the rest of the app: confident, practical, not performative.

6. **Pro-only.** The entire Analytics tab is gated behind a valid license, with a teaser card visible to free users that shows what they'd see. This directly ties visibility of learning value to the paid tier.

## Target Architecture

### 1. Daily rollup model

A new `AnalyticsDailyRollup` record captures one day of activity:

```
AnalyticsDailyRollup
├── Date                    : DateOnly
├── TotalAccepted           : int
├── TotalDismissed          : int
├── TotalTypedPast          : int
├── TotalPartialAccepts     : int
├── TotalNativeCommits      : int
├── TotalUntouched          : int
├── WordsAssisted           : int         (sum of word counts in AcceptedText)
├── WordsNative             : int         (sum of word counts in UserWrittenText)
├── TotalCorrections        : int
├── AvgQualityScore         : float
├── AvgLatencyMs            : double
├── CategoryBreakdown       : Dictionary<string, CategoryDayStats>
├── HourDistribution        : int[24]     (accepts per hour-of-day, local time)
├── TopContexts             : List<ContextDayStats>  (top 5 by event count)
└── MilestonesCrossed       : List<string>           (computed at rollup time)

CategoryDayStats
├── Accepted    : int
├── Dismissed   : int
├── AvgQuality  : float
└── Corrections : int

ContextDayStats
├── ContextKey   : string
├── ContextLabel : string
├── Category     : string
├── Accepted     : int
├── Dismissed    : int
└── AvgQuality   : float
```

### 2. Analytics store

A new `AnalyticsAggregationService` manages the lifecycle:

```
%AppData%/Keystroke/analytics-daily.json
├── LastAggregatedEventTimestamp : DateTime   (watermark — only parse new events)
├── Rollups                     : List<AnalyticsDailyRollup>  (one per active day)
├── CumulativeAccepted          : int        (running total for milestone checks)
├── CumulativeNative            : int
├── CurrentStreak               : int        (consecutive calendar days with ≥1 event)
├── LongestStreak               : int
├── StreakAnchorDate             : DateOnly   (last day the streak was active)
├── ScoreHistory                : Dictionary<string, List<ScoreSnapshot>>
│                                 (per-category, up to 90 snapshots)
└── WeeklySummaries             : List<WeekSummary>  (rolling 12-week window)

ScoreSnapshot
├── Date  : DateOnly
└── Score : int

WeekSummary
├── WeekStart           : DateOnly
├── TotalAccepted       : int
├── TotalDismissed      : int
├── WordsAssisted       : int
├── AvgQuality          : float
├── AcceptanceRate      : float
├── TopCategory         : string
├── TopCategoryRate     : float
└── MilestonesCrossed   : List<string>
```

### 3. Extended score history

Currently `LearningScoreService` keeps 3 snapshots per category in `learning-scores.json`. Rather than expanding that file (which serves the intelligence cards), the analytics store maintains its own `ScoreHistory` with up to 90 daily snapshots. When `LearningScoreService.Recompute()` runs, the analytics service listens for the result and appends a dated snapshot.

### 4. Milestone definitions

Milestones are cumulative thresholds that trigger once:

| Threshold | Label |
|-----------|-------|
| 10 accepted | "First steps — your profile is starting to form" |
| 50 accepted | "Getting personal — patterns are emerging" |
| 100 accepted | "In sync — Keystroke is adapting to your voice" |
| 250 accepted | "Deep understanding — strong context-specific patterns" |
| 500 accepted | "Writing partner — the system knows your style cold" |
| 1000 accepted | "Veteran — over a thousand personalized completions" |
| 10 native commits | "Your own words — native writing is shaping the profile" |
| 50 native commits | "Authentic voice — your manual writing is the strongest signal" |
| 7-day streak | "Week streak — seven days of active writing" |
| 30-day streak | "Monthly streak — consistently building your profile" |

### 5. UI layout

The dashboard lives in a new **"Analytics"** nav tab, inserted after "Your AI Profile" and before "App Control":

```
Nav sidebar:
  Overview
  Suggestions
  Your AI Profile
  Analytics          ← new
  App Control
  Appearance
  Advanced
```

#### Section structure

```
Analytics
├── Hero Summary Card (LearningCardStyle)
│   ├── Headline:  "Your writing profile is 78% tuned — up from 65% last week"
│   ├── Subtitle:  "342 words assisted this week across 4 contexts"
│   └── Pills:     [streak: "12-day streak"] [milestone: "Deep understanding"] [top: "Email 91%"]
│
├── Week-over-Week Comparison Row (3-column InsetTileStyle)
│   ├── Col 0: Acceptance Rate    "72% → 78%"  with delta arrow
│   ├── Col 1: Words Assisted     "280 → 342"  with delta arrow
│   └── Col 2: Avg Quality        "0.68 → 0.74" with delta arrow
│
├── Score Trajectory Card (CardStyle)
│   ├── Title: "Intelligence score over time"
│   ├── Category selector pills (clickable: All, Chat, Email, Code, ...)
│   └── Sparkline chart (Canvas, 90-day window, one line per selected category)
│       └── Y-axis: 0–100, X-axis: dates, hover tooltip with score+date
│
├── Daily Activity Card (CardStyle)
│   ├── Title: "Daily activity — last 30 days"
│   └── Bar chart (Canvas, one bar per day)
│       ├── Green segment: accepted
│       ├── Muted segment: dismissed
│       └── Teal segment: native writing
│
├── Category Breakdown Card (CardStyle)
│   ├── Title: "By category this week"
│   └── Horizontal bar rows (one per category)
│       ├── Category icon + name
│       ├── Accept rate bar (green fill on muted track)
│       ├── Accept rate %
│       └── Delta from last week ("↑ 6%")
│
├── Time-of-Day Card (CardStyle)
│   ├── Title: "When you write best"
│   └── 4-bucket heatmap row: Morning (6–11), Afternoon (12–17), Evening (18–22), Night (23–5)
│       └── Each bucket: acceptance rate + event count, colored by rate
│
├── Correction Trends Card (CardStyle)
│   ├── Title: "Correction frequency"
│   ├── Subtitle: "How often you edit accepted completions"
│   └── Sparkline: correction rate over last 30 days
│       └── Declining = good (system is learning your preferences)
│
├── Context Leaderboard Card (CardStyle)
│   ├── Title: "Strongest contexts"
│   └── Ranked list (top 5 by confidence/score)
│       ├── Context label + category badge
│       ├── Match rate %
│       ├── Event count
│       └── "Most improved" badge on the context with biggest positive delta
│
└── Milestones Card (CardStyle)
    ├── Title: "Milestones"
    └── Timeline of achieved milestones
        ├── Each: icon + label + date achieved
        └── Next upcoming milestone shown dimmed with progress bar
```

#### Hero summary logic

The headline is generated from the weighted average intelligence score across all active categories:

- Score < 30: "Your writing profile is just getting started"
- Score 30–59: "Your writing profile is building — {score}% tuned"
- Score 60–79: "Your writing profile is {score}% tuned — {comparison to last week}"
- Score 80+: "Keystroke knows your style — {score}% tuned"

The comparison suffix uses the week-over-week delta:
- Delta > 0: "up from {prev}% last week"
- Delta == 0: "holding steady"
- Delta < 0: "down from {prev}% — your patterns may be shifting"

## Phased Implementation

### Phase A: Aggregation engine and daily rollups

**Objective:** Build the data layer that everything else reads from.

**New files:**
- `Services/AnalyticsAggregationService.cs` — rollup computation, persistence, watermark tracking
- `Services/AnalyticsModels.cs` — `AnalyticsDailyRollup`, `CategoryDayStats`, `ContextDayStats`, `WeekSummary`, `ScoreSnapshot`, `AnalyticsStore`

**Changes to existing files:**
- `Services/LearningScoreService.cs` — add `ScoreComputed` event that fires after `Recompute()` with (category, score, timestamp). The analytics service subscribes to this to build extended score history without changing the score service's persistence.
- `Services/AppConfig.cs` — add `AnalyticsEnabled` flag (default `true` for Pro, ignored for free tier)

**Implementation details:**

The aggregation flow:

1. On Settings window open (or on a background timer after each `LearningScoreService.Recompute()`), call `AnalyticsAggregationService.RefreshAsync()`.
2. The service reads the last watermark timestamp from `analytics-daily.json`.
3. It scans `tracking.jsonl` from the watermark forward, parsing only new events.
4. For legacy data on first run, it also scans `completions.jsonl` and converts legacy records into rollup contributions (marked as lower-confidence).
5. Each event is bucketed by `DateOnly` (local time) into the appropriate rollup.
6. After all new events are processed:
   - Update streak counters by checking consecutive days with events.
   - Check milestone thresholds against cumulative counts.
   - Recompute the current week's `WeekSummary`.
   - Prune rollups older than 90 days (keep weekly summaries indefinitely).
   - Atomic-write `analytics-daily.json`.

**Performance budget:**
- Initial aggregation (parsing 4000-line tracking.jsonl): < 200ms.
- Incremental refresh (parsing new events since watermark): < 20ms typical.
- The Settings window should not block on aggregation — load cached data first, then refresh in background and update UI.

**Definition of done:**
- `analytics-daily.json` is populated with correct daily rollups from existing event data.
- Streak and milestone counters are accurate.
- Weekly summaries are correct for the current and previous weeks.
- Score history extends to 90 snapshots per category.
- Unit tests cover rollup computation, streak logic, milestone thresholds, and incremental refresh.

### Phase B: Analytics nav tab and hero card

**Objective:** Wire up the new tab with the top-level summary that immediately communicates value.

**Changes to existing files:**
- `Views/SettingsWindow.xaml` — add `NavAnalyticsButton` radio button in the nav sidebar, add `AnalyticsSection` StackPanel in the main tab control
- `Views/SettingsWindow.xaml.cs` — add `LoadAnalytics()` method, wire it to `NavigateSection_Checked`, gate visibility behind license check

**New XAML elements:**
- `AnalyticsSection` (StackPanel, Visibility="Collapsed")
- `AnalyticsHeroCard` (LearningCardStyle border)
- `AnalyticsHeroTitle`, `AnalyticsHeroSubtitle` (TextBlocks)
- `AnalyticsHeroPills` (WrapPanel with PillStyle borders)
- `AnalyticsWeekCompareGrid` (3-column Grid with InsetTileStyle)
- `AnalyticsLockedCard` (shown to free users instead of the full dashboard)

**Hero card data flow:**
```
AnalyticsAggregationService.GetStore()
  → current week summary + previous week summary
  → compute deltas
  → format headline, subtitle, pills
  → set TextBlock values
```

**Free-tier teaser:**
- Show a single card with the analytics icon and text: "See how your writing profile is evolving — acceptance rates, quality trends, and context insights. Unlock with a license key."
- Include a dimmed/blurred mock sparkline behind the text as a visual hook.

**Definition of done:**
- Analytics tab appears in nav for all users.
- Pro users see the hero card with accurate current-week stats and week-over-week comparison.
- Free users see the locked teaser card.
- Tab loads in < 100ms from cached analytics data.

### Phase C: Score trajectory sparkline

**Objective:** Visualize intelligence score progression over time — the most impactful single chart.

**Implementation approach:**

Build a reusable `SparklineControl` as a lightweight custom control (not a full UserControl — just a class extending `FrameworkElement` or using a Canvas):

```
SparklineControl
├── Properties:
│   ├── DataPoints : List<(DateOnly Date, double Value)>
│   ├── MinValue   : double (default 0)
│   ├── MaxValue   : double (default 100)
│   ├── LineColor  : Color
│   ├── FillColor  : Color (gradient fill below line)
│   └── ShowDots   : bool
├── Rendering:
│   ├── Canvas background: transparent
│   ├── Polyline for the data series
│   ├── Optional LinearGradientBrush fill below the line
│   └── Ellipses at data points if ShowDots=true
└── Interaction:
    └── MouseMove tooltip showing (date, score) at nearest point
```

**Score trajectory card:**
- Category selector: a row of clickable pill-style toggles (All, Chat, Email, Code, etc.)
- "All" shows the weighted average sparkline. Individual categories show their own line.
- Multiple categories can be selected simultaneously (each gets a distinct color from the existing score-color palette: green ≥80, blue ≥60, amber ≥40, muted <40).
- X-axis: last 90 days (or however many snapshots exist). Y-axis: 0–100.
- Current score shown as a large number to the right of the chart, matching the intelligence card style.

**Data source:** `AnalyticsStore.ScoreHistory[category]` — list of `(DateOnly, int)` pairs.

**Definition of done:**
- Sparkline renders correctly with 1–90 data points.
- Category selectors filter the displayed lines.
- Tooltip shows date and score on hover.
- Chart scales properly when the Settings window is resized.
- Colors match the existing intelligence card score-color scheme.

### Phase D: Daily activity bar chart and category breakdown

**Objective:** Show recent activity volume and per-category acceptance rates with week-over-week context.

**Daily activity chart:**
- Stacked vertical bar chart, 30 bars (one per day), rendered on a Canvas.
- Each bar has up to 3 segments stacked bottom-to-top:
  - Green (`#3FB950`): accepted completions
  - Teal (`#2EA98F`): native writing commits
  - Muted (`#8B949E`): dismissed/typed-past (stacked on top)
- Bar height proportional to total events that day, scaled to the max-day in the window.
- Today's bar highlighted with a subtle glow border.
- X-axis: abbreviated date labels every 7 days. Y-axis: implied by bar heights (no explicit axis needed).
- Hover tooltip: "Mon Apr 6 — 23 accepted, 8 dismissed, 5 native"

**Category breakdown:**
- One horizontal row per category that had activity this week.
- Each row contains:
  - Category icon + name (left-aligned)
  - Horizontal acceptance-rate bar (green fill on `#21262D` track, same style as intelligence card score bars)
  - Accept rate percentage (right of bar)
  - Week-over-week delta pill: "↑ 6%" in green or "↓ 3%" in amber
- Rows ordered by acceptance rate descending.

**Data source:** Current week's `WeekSummary` and previous week's `WeekSummary` from `AnalyticsStore`.

**Definition of done:**
- Bar chart renders 30 days of data with correct stacked segments.
- Category rows show accurate rates and deltas.
- Both elements resize gracefully with the window.

### Phase E: Time-of-day heatmap and correction trends

**Objective:** Surface temporal patterns and show that corrections are declining.

**Time-of-day card:**
- 4 rectangular tiles in a row, one per time bucket:
  - Morning (6:00–11:59)
  - Afternoon (12:00–17:59)
  - Evening (18:00–22:59)
  - Night (23:00–5:59)
- Each tile shows:
  - Bucket label
  - Acceptance rate for that bucket (computed from `HourDistribution` across the last 14 days of rollups)
  - Event count
  - Background tint intensity proportional to acceptance rate (higher rate = more saturated green tint)
- The bucket with the highest acceptance rate gets a subtle "Best" badge.

**Aggregation:** Sum the `HourDistribution[0..23]` arrays across the last 14 rollups. Map each hour to its bucket. Compute acceptance rate per bucket by cross-referencing with dismissed events in the same hours (requires adding `HourDismissDistribution : int[24]` to the rollup model).

**Correction trends sparkline:**
- Reuse `SparklineControl` from Phase C.
- Data: daily correction rate = `TotalCorrections / TotalAccepted` for each of the last 30 days.
- Declining line = good. Show a brief annotation if the trend is negative (improving): "Corrections are declining — the system is adapting to your preferences."
- If correction data is too sparse (< 7 days with data), show a placeholder message instead of a chart.

**Definition of done:**
- Time-of-day buckets show accurate acceptance rates.
- Correction sparkline shows a meaningful trend when sufficient data exists.
- Both degrade gracefully with sparse data.

### Phase F: Context leaderboard and milestones

**Objective:** Highlight the strongest contexts (where learning is most effective) and show progress milestones.

**Context leaderboard:**
- Top 5 contexts by confidence score (from `AnalyticsStore` or `LearningRepository` context summaries).
- Each row:
  - Context label (e.g., "Slack — #engineering")
  - Category badge pill (e.g., "Chat")
  - Match rate % with colored indicator
  - Event count
  - "Most improved" pill on the context with the largest positive score delta in the last 7 days
- Clicking a context row could eventually navigate to the Personalization tab's context drill-down (future integration point).

**Milestones timeline:**
- Vertical timeline, newest first.
- Each achieved milestone: colored dot + label + date achieved.
- The next unachieved milestone: dimmed dot + label + progress bar showing current count / threshold.
- If all milestones are achieved, show a "completionist" message.

**Milestone tracking:**
- When `AnalyticsAggregationService.RefreshAsync()` detects a new cumulative threshold crossed, it adds the milestone to `AnalyticsStore.AchievedMilestones` with timestamp.
- Milestones are never removed (even if data is cleared, the achievement stands — or optionally, clearing learning data also clears milestones, matching the "reset" semantic).

**Definition of done:**
- Context leaderboard accurately ranks by confidence.
- "Most improved" badge appears on the correct context.
- Milestones display in chronological order with correct dates.
- Next milestone progress bar is accurate.

### Phase G: Polish, performance, and testing

**Objective:** Ensure the dashboard is fast, accurate, and visually cohesive.

**Performance:**
- Profile the full `LoadAnalytics()` path. Target: < 150ms from tab click to full render with 90 days of data.
- If sparkline rendering is slow with many points, implement point decimation (show every Nth point when zoomed out).
- Ensure `RefreshAsync()` doesn't block the UI thread — use `Task.Run` for aggregation, marshal results back to dispatcher.

**Visual polish:**
- Consistent use of `LearningCardStyle` / `CardStyle` / `InsetTileStyle` from the existing theme.
- Sparkline colors: use the score-color scheme (green ≥80, blue ≥60, amber ≥40, muted <40) for all chart elements.
- Ensure all text uses `TextPrimary`, `TextSecondary`, `TextMuted` from the resource dictionary.
- Add subtle fade-in animations on tab switch (matching any existing transitions in the Settings window).
- Test at minimum window size (1180x780) — charts must not clip or overflow.

**Edge cases:**
- Brand-new user with 0 events: show a welcoming empty state, not blank panels. "Start typing with Keystroke active and your analytics will appear here."
- User with only legacy data (no tracking.jsonl): aggregate from completions.jsonl only, show a note that richer analytics will appear as V2 events accumulate.
- User who clears learning data: reset analytics store, show empty state.
- Sparse data (e.g., only 3 days of use): charts should render correctly without looking broken. Hide the time-of-day card until 7+ active days exist.

**Testing:**
- Unit tests for `AnalyticsAggregationService`: rollup accuracy, incremental refresh, streak computation, milestone detection.
- Unit tests for `WeekSummary` comparison logic (week-over-week deltas).
- UI tests (manual): verify layout at multiple window sizes, verify pro/free gating, verify empty states.

## File-Level Worklist

### New files

| File | Purpose |
|------|---------|
| `Services/AnalyticsAggregationService.cs` | Rollup engine: parses events, computes daily/weekly aggregates, manages persistence |
| `Services/AnalyticsModels.cs` | Data models: `AnalyticsDailyRollup`, `AnalyticsStore`, `WeekSummary`, `ScoreSnapshot`, `CategoryDayStats`, `ContextDayStats` |
| `Controls/SparklineControl.cs` | Reusable sparkline chart (Canvas-based, supports multiple series, tooltips) |
| `Controls/StackedBarChart.cs` | Reusable stacked bar chart for daily activity visualization |
| `tests/KeystrokeApp.Tests/AnalyticsAggregationServiceTests.cs` | Rollup, streak, milestone, and incremental refresh tests |
| `tests/KeystrokeApp.Tests/WeekSummaryTests.cs` | Week-over-week comparison and delta computation tests |

### Modified files

| File | Change |
|------|--------|
| `Views/SettingsWindow.xaml` | Add `NavAnalyticsButton`, `AnalyticsSection` with all card layouts |
| `Views/SettingsWindow.xaml.cs` | Add `LoadAnalytics()`, `RefreshAnalyticsAsync()`, wire nav, gate behind license |
| `Services/LearningScoreService.cs` | Add `ScoreComputed` event (fires after `Recompute()` with category/score/timestamp) |
| `Services/AppConfig.cs` | Add `AnalyticsEnabled` config flag |
| `App.xaml.cs` | Instantiate and wire `AnalyticsAggregationService`, subscribe to `ScoreComputed` |

### Resource additions (in `SettingsWindow.xaml` or shared `ResourceDictionary`)

| Key | Purpose |
|-----|---------|
| `AnalyticsTint` | Background brush for analytics cards (suggestion: `#1A2030` — a cool blue-grey) |
| `AnalyticsBorder` | Border brush for analytics cards (suggestion: `#2D4A6F`) |
| `AnalyticsCardStyle` | Card style inheriting from `CardStyle` with analytics tint |
| `ChartGreen` | `#3FB950` — accepted completions in charts |
| `ChartTeal` | `#2EA98F` — native writing in charts |
| `ChartMuted` | `#484F58` — dismissed/typed-past in charts |
| `ChartBlue` | `#2F81F7` — secondary chart series |
| `ChartAmber` | `#F0883E` — warning/declining trends |

## Recommended Execution Order

1. **Phase A** — aggregation engine (foundation; everything reads from this)
2. **Phase B** — nav tab + hero card (first visible output; validates the data pipeline end-to-end)
3. **Phase C** — score sparkline (highest visual impact single chart)
4. **Phase D** — daily activity + category breakdown (fills out the main dashboard body)
5. **Phase E** — time-of-day + correction trends (secondary insights)
6. **Phase F** — context leaderboard + milestones (gamification layer)
7. **Phase G** — polish + performance + edge cases

**Reasoning:**
- The aggregation engine must exist before any UI can render.
- The hero card validates the full data path (events → rollups → UI) with minimal UI surface area.
- Sparklines are the most visually compelling element and prove the `SparklineControl` which is reused in Phase E.
- Daily activity and category breakdown are the "meat" of the dashboard.
- Time-of-day and corrections are interesting but secondary — they can ship later without diminishing the initial release.
- Milestones and context leaderboard are the gamification layer — they're most effective once the core analytics are solid.
- Polish is last because it's continuous and shouldn't block shipping functional phases.

## First Slice

If this should ship incrementally, the minimal viable analytics release is:

1. `AnalyticsAggregationService` with daily rollups and weekly summaries
2. Analytics nav tab with hero summary card
3. Week-over-week comparison row
4. Score trajectory sparkline (reusable `SparklineControl`)
5. Pro/free gating with teaser card

That slice delivers the "is it working?" and "is it getting better?" answers with one chart and one comparison row. The daily activity bars, time-of-day, corrections, and milestones can follow as incremental additions without changing any of the Phase A/B/C infrastructure.

## Rollup Model Extension: HourDismissDistribution

Phase E's time-of-day acceptance rate requires knowing dismissals per hour, not just accepts. Extend `AnalyticsDailyRollup` during Phase A to include:

```
HourDismissDistribution : int[24]   (dismissals per hour-of-day, local time)
```

This costs nothing to add during initial rollup computation and avoids a schema migration later.

## Integration Points with Existing Features

### LearningScoreService → AnalyticsAggregationService

- New `ScoreComputed` event on `LearningScoreService` fires after every `Recompute()`.
- `AnalyticsAggregationService` subscribes and appends to `ScoreHistory`.
- This is the only new cross-service coupling introduced.

### SettingsWindow.LoadLearningStats() → LoadAnalytics()

- `LoadLearningStats()` continues to populate the Personalization tab as it does today.
- `LoadAnalytics()` is a separate method that reads from `AnalyticsAggregationService` only.
- No shared mutable state between the two paths.

### DriftDetected → Analytics hero

- When `LearningScoreService.DriftDetected` fires, the analytics hero subtitle can reflect this: "Score dropped in {category} — your writing patterns may be shifting."
- This is a display-only integration — the analytics service doesn't react to drift, it just renders it.

## Testing Plan

### Aggregation tests

- Empty event file → empty rollups, 0 streak, no milestones
- Single-day events → one rollup with correct counts
- Multi-day events → correct daily bucketing (respecting local timezone)
- Incremental refresh: add events after initial aggregation → only new events processed, watermark advances
- Legacy-only data: completions.jsonl events produce valid rollups
- Mixed legacy + V2: deduplication produces correct counts (no double-counting)
- Streak: consecutive days → correct streak count; gap resets streak; longest streak preserved
- Milestones: cumulative count crossing threshold → milestone recorded with correct date
- 90-day pruning: rollups older than 90 days are removed; weekly summaries survive

### Week summary tests

- Current week with partial data → correct rates and totals
- Week-over-week delta: acceptance rate up → positive delta; down → negative; no previous week → delta is 0
- Category breakdown: only categories active this week appear
- Context top-5: ranked by event count, correct labels

### Score history tests

- Score snapshots accumulate up to 90 per category
- Oldest snapshot pruned when limit exceeded
- Missing days produce gaps in sparkline (not interpolated zeros)

### UI tests (manual checklist)

- [ ] Analytics tab visible for Pro users, locked for free
- [ ] Hero card shows accurate headline with week-over-week comparison
- [ ] Sparkline renders with 1, 10, 30, 90 data points
- [ ] Category selector filters sparkline correctly
- [ ] Daily bars render at min window size without clipping
- [ ] Empty state shows welcoming message, not broken UI
- [ ] Tab switch is responsive (< 150ms to render)
- [ ] Hover tooltips display correct data on charts
