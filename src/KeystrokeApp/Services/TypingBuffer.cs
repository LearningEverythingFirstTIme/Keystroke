using System;
using System.Text;

namespace KeystrokeApp.Services;

/// <summary>
/// Accumulates typed characters into a buffer.
/// Handles backspace (remove last char) and clears on Enter/Escape/arrows.
/// </summary>
public class TypingBuffer
{
    private readonly StringBuilder _buffer = new();

    /// <summary>
    /// Current text in the buffer.
    /// </summary>
    public string CurrentText => _buffer.ToString();

    /// <summary>
    /// Number of characters in the buffer.
    /// </summary>
    public int Length => _buffer.Length;

    /// <summary>
    /// Fired when the buffer content changes (character added or removed).
    /// </summary>
    public event Action<string>? BufferChanged;

    /// <summary>
    /// Fired when the buffer is cleared completely.
    /// </summary>
    public event Action? BufferCleared;

    /// <summary>
    /// Add a character to the buffer.
    /// </summary>
    public void AddChar(char c)
    {
        _buffer.Append(c);
        BufferChanged?.Invoke(CurrentText);
    }

    /// <summary>
    /// Remove the last character (Backspace).
    /// Does nothing if buffer is empty.
    /// </summary>
    public void RemoveLastChar()
    {
        if (_buffer.Length > 0)
        {
            _buffer.Remove(_buffer.Length - 1, 1);
            
            if (_buffer.Length == 0)
                BufferCleared?.Invoke();
            else
                BufferChanged?.Invoke(CurrentText);
        }
    }

    /// <summary>
    /// Clear the buffer completely.
    /// Called on Enter, Escape, arrow keys, etc.
    /// </summary>
    public void Clear()
    {
        if (_buffer.Length > 0)
        {
            _buffer.Clear();
            BufferCleared?.Invoke();
        }
    }

    /// <summary>
    /// Check if buffer is empty.
    /// </summary>
    public bool IsEmpty => _buffer.Length == 0;

    public override string ToString() => CurrentText;
}
