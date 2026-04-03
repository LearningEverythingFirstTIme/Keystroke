using System;
using System.Text;

namespace KeystrokeApp.Services;

/// <summary>
/// Accumulates typed characters into a buffer.
/// Handles backspace (remove last char) and clears on Enter/Escape/arrows.
/// Thread-safe: accessed from listener callbacks and debounce timer threads.
/// </summary>
public class TypingBuffer
{
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();

    /// <summary>
    /// Current text in the buffer.
    /// </summary>
    public string CurrentText { get { lock (_lock) return _buffer.ToString(); } }

    /// <summary>
    /// Number of characters in the buffer.
    /// </summary>
    public int Length { get { lock (_lock) return _buffer.Length; } }

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
        string text;
        lock (_lock)
        {
            _buffer.Append(c);
            text = _buffer.ToString();
        }
        BufferChanged?.Invoke(text);
    }

    /// <summary>
    /// Remove the last character (Backspace).
    /// Does nothing if buffer is empty.
    /// </summary>
    public void RemoveLastChar()
    {
        bool cleared = false;
        string? text = null;

        lock (_lock)
        {
            if (_buffer.Length > 0)
            {
                _buffer.Remove(_buffer.Length - 1, 1);

                if (_buffer.Length == 0)
                    cleared = true;
                else
                    text = _buffer.ToString();
            }
        }

        if (cleared)
            BufferCleared?.Invoke();
        else if (text != null)
            BufferChanged?.Invoke(text);
    }

    /// <summary>
    /// Clear the buffer completely.
    /// Called on Enter, Escape, arrow keys, etc.
    /// </summary>
    public void Clear()
    {
        bool wasNonEmpty;
        lock (_lock)
        {
            wasNonEmpty = _buffer.Length > 0;
            _buffer.Clear();
        }

        if (wasNonEmpty)
            BufferCleared?.Invoke();
    }

    /// <summary>
    /// Directly set the buffer content without firing any events.
    /// Used after partial-word acceptance so the buffer stays in sync
    /// without triggering a new prediction debounce cycle.
    /// </summary>
    public void SetText(string text)
    {
        lock (_lock)
        {
            _buffer.Clear();
            _buffer.Append(text);
        }
    }

    /// <summary>
    /// Check if buffer is empty.
    /// </summary>
    public bool IsEmpty { get { lock (_lock) return _buffer.Length == 0; } }

    public override string ToString() => CurrentText;
}
