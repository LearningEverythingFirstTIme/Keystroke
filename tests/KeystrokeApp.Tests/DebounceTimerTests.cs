using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

/// <summary>
/// Exercises the debounce primitive on the hot path. Uses real delays with
/// generous margins — a TestClock-style abstraction would be cleaner, but
/// refactoring DebounceTimer is out of scope for the build-hygiene pass.
/// Timings are slack on purpose so CI noise doesn't cause flakes.
/// </summary>
public class DebounceTimerTests
{
    private const int BaseDelayMs = 60;
    private const int LongWaitMs = 500;
    private const int ShortWaitMs = 20;

    [Fact]
    public async Task Restart_FiresOnceAfterDelay()
    {
        using var timer = new DebounceTimer(BaseDelayMs);
        var fireCount = 0;
        var fired = new TaskCompletionSource();
        timer.DebounceComplete += () =>
        {
            Interlocked.Increment(ref fireCount);
            fired.TrySetResult();
        };

        timer.Restart();
        var completed = await Task.WhenAny(fired.Task, Task.Delay(LongWaitMs));
        Assert.Equal(fired.Task, completed);
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task Restart_CoalescesBurstIntoSingleFire()
    {
        using var timer = new DebounceTimer(BaseDelayMs);
        var fireCount = 0;
        timer.DebounceComplete += () => Interlocked.Increment(ref fireCount);

        // 5 restarts within the debounce window — expect one final fire.
        for (var i = 0; i < 5; i++)
        {
            timer.Restart();
            await Task.Delay(ShortWaitMs);
        }

        // Wait past the debounce period with headroom for the fire callback.
        await Task.Delay(BaseDelayMs * 4);
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task Cancel_PreventsFire()
    {
        using var timer = new DebounceTimer(BaseDelayMs);
        var fireCount = 0;
        timer.DebounceComplete += () => Interlocked.Increment(ref fireCount);

        timer.Restart();
        timer.Cancel();
        await Task.Delay(BaseDelayMs * 4);

        Assert.Equal(0, fireCount);
    }

    [Fact]
    public async Task Restart_AfterCancel_StillFires()
    {
        using var timer = new DebounceTimer(BaseDelayMs);
        var fired = new TaskCompletionSource();
        timer.DebounceComplete += () => fired.TrySetResult();

        timer.Restart();
        timer.Cancel();
        timer.Restart();

        var completed = await Task.WhenAny(fired.Task, Task.Delay(LongWaitMs));
        Assert.Equal(fired.Task, completed);
    }

    [Fact]
    public async Task Dispose_SilencesPendingFire()
    {
        var timer = new DebounceTimer(BaseDelayMs);
        var fireCount = 0;
        timer.DebounceComplete += () => Interlocked.Increment(ref fireCount);

        timer.Restart();
        timer.Dispose();
        await Task.Delay(BaseDelayMs * 4);

        Assert.Equal(0, fireCount);
    }

    [Fact]
    public void Restart_AfterDispose_DoesNotThrow()
    {
        var timer = new DebounceTimer(BaseDelayMs);
        timer.Dispose();

        // Restart on a disposed timer is a legitimate scenario during
        // Deactivate→Activate cycles — it must not throw.
        var ex = Record.Exception(() => timer.Restart());
        Assert.Null(ex);
    }
}
