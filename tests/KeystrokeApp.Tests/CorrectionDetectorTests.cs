using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class CorrectionDetectorTests : IDisposable
{
    private readonly CorrectionDetector _detector = new();

    // ── Basic correction signals ─────────────────────────────────────────────

    [Fact]
    public void StartWatching_NoInteraction_ReportsNoEdit()
    {
        CorrectionInfo? result = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(info =>
        {
            result = info;
            done.Set();
        });

        done.Wait(TimeSpan.FromSeconds(3));

        Assert.NotNull(result);
        Assert.False(result.EditDetected);
        Assert.Equal(0, result.BackspaceCount);
        Assert.Equal("", result.ReplacementText);
        Assert.False(result.HasCorrection);
        Assert.Equal("none", result.CorrectionType());
    }

    [Fact]
    public void OnBackspace_SingleBackspace_SetsEditDetected()
    {
        CorrectionInfo? result = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(info =>
        {
            result = info;
            done.Set();
        });

        _detector.OnBackspace();
        done.Wait(TimeSpan.FromSeconds(3));

        Assert.NotNull(result);
        Assert.True(result.EditDetected);
        Assert.Equal(1, result.BackspaceCount);
        Assert.True(result.HasCorrection);
    }

    [Fact]
    public void OnBackspace_MultipleBackspaces_CountsAllDeletions()
    {
        CorrectionInfo? result = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(info =>
        {
            result = info;
            done.Set();
        });

        _detector.OnBackspace();
        _detector.OnBackspace();
        _detector.OnBackspace();
        _detector.OnBackspace();
        _detector.OnBackspace();
        done.Wait(TimeSpan.FromSeconds(3));

        Assert.NotNull(result);
        Assert.Equal(5, result.BackspaceCount);
        Assert.Equal("truncated", result.CorrectionType());
    }

    [Fact]
    public void OnCharacterTyped_WithoutBackspace_DoesNotSetEditDetected()
    {
        CorrectionInfo? result = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(info =>
        {
            result = info;
            done.Set();
        });

        _detector.OnCharacterTyped('a');
        _detector.OnCharacterTyped('b');
        done.Wait(TimeSpan.FromSeconds(3));

        Assert.NotNull(result);
        Assert.False(result.EditDetected);
        Assert.Equal(0, result.BackspaceCount);
        Assert.Equal("ab", result.ReplacementText);
        Assert.False(result.HasCorrection);
    }

    // ── Replacement detection ────────────────────────────────────────────────

    [Fact]
    public void BackspaceThenType_CapturesReplacementText()
    {
        CorrectionInfo? result = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(info =>
        {
            result = info;
            done.Set();
        });

        // User deletes 3 chars of the completion and types "plan" instead
        _detector.OnBackspace();
        _detector.OnBackspace();
        _detector.OnBackspace();
        _detector.OnCharacterTyped('p');
        _detector.OnCharacterTyped('l');
        _detector.OnCharacterTyped('a');
        _detector.OnCharacterTyped('n');
        done.Wait(TimeSpan.FromSeconds(3));

        Assert.NotNull(result);
        Assert.True(result.EditDetected);
        Assert.Equal(3, result.BackspaceCount);
        Assert.Equal("plan", result.ReplacementText);
        Assert.Equal("replaced_ending", result.CorrectionType());
    }

    // ── Typo-in-replacement detection ────────────────────────────────────────

    [Fact]
    public void TypoCorrection_BackspaceAfterTyping_RemovesFromReplacementNotCompletion()
    {
        CorrectionInfo? result = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(info =>
        {
            result = info;
            done.Set();
        });

        // Delete 2 chars from completion
        _detector.OnBackspace();
        _detector.OnBackspace();
        // Start typing replacement
        _detector.OnCharacterTyped('p');
        _detector.OnCharacterTyped('l');
        // Oops, typo — backspace removes from replacement buffer, not deletion count
        _detector.OnBackspace();
        // Correct the typo
        _detector.OnCharacterTyped('a');
        _detector.OnCharacterTyped('n');
        done.Wait(TimeSpan.FromSeconds(3));

        Assert.NotNull(result);
        // BackspaceCount should still be 2 (into the original), not 3
        Assert.Equal(2, result.BackspaceCount);
        Assert.Equal("pan", result.ReplacementText);
    }

    [Fact]
    public void TypoCorrection_BackspacePastAllTyped_IncrementsDeletionCount()
    {
        CorrectionInfo? result = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(info =>
        {
            result = info;
            done.Set();
        });

        // Delete 2 chars from completion
        _detector.OnBackspace();
        _detector.OnBackspace();
        // Type 1 char
        _detector.OnCharacterTyped('x');
        // Backspace removes that char (typo correction)
        _detector.OnBackspace();
        // Backspace again — now this goes into the original completion
        _detector.OnBackspace();
        done.Wait(TimeSpan.FromSeconds(3));

        Assert.NotNull(result);
        Assert.Equal(3, result.BackspaceCount);
        Assert.Equal("", result.ReplacementText);
        Assert.Equal("truncated", result.CorrectionType());
    }

    // ── Watch window supersession ────────────────────────────────────────────

    [Fact]
    public void StartWatching_CalledTwice_OnlySecondCallbackFires()
    {
        bool firstFired = false;
        CorrectionInfo? secondResult = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(_ => { firstFired = true; });

        // Immediately supersede with a new watch
        _detector.StartWatching(info =>
        {
            secondResult = info;
            done.Set();
        });

        _detector.OnBackspace();
        done.Wait(TimeSpan.FromSeconds(3));

        Assert.False(firstFired);
        Assert.NotNull(secondResult);
        Assert.Equal(1, secondResult.BackspaceCount);
    }

    // ── No-op when not watching ──────────────────────────────────────────────

    [Fact]
    public void OnBackspace_WhenNotWatching_IsNoOp()
    {
        // Should not throw or affect subsequent watches
        _detector.OnBackspace();
        _detector.OnCharacterTyped('a');

        CorrectionInfo? result = null;
        var done = new ManualResetEventSlim();

        _detector.StartWatching(info =>
        {
            result = info;
            done.Set();
        });
        done.Wait(TimeSpan.FromSeconds(3));

        Assert.NotNull(result);
        Assert.False(result.EditDetected);
        Assert.Equal(0, result.BackspaceCount);
    }

    // ── CorrectionType classification ────────────────────────────────────────

    [Fact]
    public void CorrectionType_MinorEdit_ClassifiedAsMinor()
    {
        var info = new CorrectionInfo
        {
            EditDetected = true,
            BackspaceCount = 1,
            ReplacementText = "a"
        };

        Assert.Equal("minor", info.CorrectionType());
    }

    [Fact]
    public void CorrectionType_LargeDeletionNoReplacement_ClassifiedAsTruncated()
    {
        var info = new CorrectionInfo
        {
            EditDetected = true,
            BackspaceCount = 10,
            ReplacementText = ""
        };

        Assert.Equal("truncated", info.CorrectionType());
    }

    [Fact]
    public void CorrectionType_DeletionWithReplacement_ClassifiedAsReplacedEnding()
    {
        var info = new CorrectionInfo
        {
            EditDetected = true,
            BackspaceCount = 8,
            ReplacementText = "better ending"
        };

        Assert.Equal("replaced_ending", info.CorrectionType());
    }

    public void Dispose()
    {
        _detector.Dispose();
    }
}
