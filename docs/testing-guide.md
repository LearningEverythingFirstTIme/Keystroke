# Testing Guide

This project now has a small `xUnit` test suite in `tests/KeystrokeApp.Tests`.

## What a test is

A test is a tiny program that checks one behavior and tells you whether the code still does what you expect.

Good first tests answer questions like:

- "If I pass in an email address, does it get redacted?"
- "If the cache is full, does the oldest entry get evicted?"
- "If I backspace the last character, does the typing buffer clear?"

## Why tests help

Tests give you a fast way to notice regressions after a change.

In this repo, the first batch of tests already found a real bug:
overlapping sensitive-data matches could crash `PiiFilter.Scrub(...)`.

## What is being tested first

The easiest and safest place to start is pure logic:

- `SensitiveDataDetector`
- `PiiFilter`
- `OutboundPrivacyService`
- `PredictionCache`
- `TypingBuffer`
- `ContaminationFilter`

These are great beginner targets because they:

- run fast
- do not depend on live UI windows
- do not need the keyboard hook to be active
- are easy to reason about from input to output

## How to run the tests

From the repo root:

```powershell
dotnet test tests\KeystrokeApp.Tests\KeystrokeApp.Tests.csproj
```

There is also a solution file now:

```powershell
dotnet test Keystroke.sln
```

If the app is running and holding the normal debug build open, the test project is already configured to build the referenced app into a separate test-only output folder.

## How to read a test

Most tests follow the same pattern:

1. Arrange: set up the object and input
2. Act: call the method
3. Assert: check the result

Example:

```csharp
[Fact]
public void Scrub_ReplacesAllRecognizedSensitiveValues()
{
    const string input = "Reach me at nick@example.com or call 555-123-4567.";

    var scrubbed = PiiFilter.Scrub(input);

    Assert.Equal("Reach me at [EMAIL] or call [PHONE].", scrubbed);
}
```

## Good next tests

After this, the next high-value targets are:

- `RollingContextService`
- `AppCategory`
- `CorrectionDetector`
- `LearningScoreService`

## What not to start with

Avoid these until you feel more comfortable:

- WPF window behavior
- cursor positioning
- global keyboard hooks
- full end-to-end AI prediction flows

Those are testable too, but they usually need more setup, more mocking, or a different style of test.

## Rule of thumb

When you fix a bug, try to add one test that would have caught it.

That habit is more important than having perfect coverage.
