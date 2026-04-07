using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class PredictionCacheTests
{
    [Fact]
    public void TryGet_ReturnsStoredValue()
    {
        var cache = new PredictionCache();
        cache.Put("hel", "hello");

        var found = cache.TryGet("hel", out var completion);

        Assert.True(found);
        Assert.Equal("hello", completion);
    }

    [Fact]
    public void Put_EvictsLeastRecentlyUsedEntry()
    {
        var cache = new PredictionCache(maxSize: 2);
        cache.Put("a", "alpha");
        cache.Put("b", "bravo");
        cache.TryGet("a", out _);

        cache.Put("c", "charlie");

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new PredictionCache();
        cache.Put("a", "alpha");

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("a", out _));
    }
}
