using KeystrokeApp.Services;

namespace KeystrokeApp.Tests;

public class TypingBufferTests
{
    [Fact]
    public void AddChar_AppendsTextAndRaisesBufferChanged()
    {
        var buffer = new TypingBuffer();
        var changes = new List<string>();
        buffer.BufferChanged += changes.Add;

        buffer.AddChar('h');
        buffer.AddChar('i');

        Assert.Equal("hi", buffer.CurrentText);
        Assert.Equal(new[] { "h", "hi" }, changes);
    }

    [Fact]
    public void RemoveLastChar_FromSingleCharacter_RaisesBufferCleared()
    {
        var buffer = new TypingBuffer();
        var cleared = 0;
        buffer.BufferCleared += () => cleared++;
        buffer.AddChar('x');

        buffer.RemoveLastChar();

        Assert.True(buffer.IsEmpty);
        Assert.Equal(1, cleared);
    }

    [Fact]
    public void SetText_UpdatesBufferWithoutRaisingEvents()
    {
        var buffer = new TypingBuffer();
        var changed = 0;
        var cleared = 0;
        buffer.BufferChanged += _ => changed++;
        buffer.BufferCleared += () => cleared++;

        buffer.SetText("hello");

        Assert.Equal("hello", buffer.CurrentText);
        Assert.Equal(0, changed);
        Assert.Equal(0, cleared);
    }
}
