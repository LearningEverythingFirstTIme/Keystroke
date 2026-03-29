using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace KeystrokeApp.Services;

/// <summary>
/// System-wide low-level keyboard hook that fires events for key presses.
/// Supports intercepting (swallowing) specific keys.
/// </summary>
public class KeyboardHookService : IDisposable
{
    // ==================== P/Invoke Signatures ====================

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ==================== Constants ====================

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;

    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_BACK = 0x08;
    private const int VK_RETURN = 0x0D;
    private const int VK_LEFT = 0x25;
    private const int VK_UP = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;
    private const int VK_HOME = 0x24;
    private const int VK_END = 0x23;
    private const int VK_PRIOR = 0x21;
    private const int VK_NEXT = 0x22;
    private const int VK_DELETE = 0x2E;

    // ==================== Types ====================

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // ==================== Events ====================

    /// <summary>
    /// Fired when a printable character is typed.
    /// </summary>
    public event Action<char>? CharacterTyped;

    /// <summary>
    /// Fired when a special key is pressed. Handler can set ShouldSwallow = true to block the key.
    /// </summary>
    public event Action<SpecialKeyEventArgs>? SpecialKeyPressed;

    public enum SpecialKey
    {
        Tab,
        Escape,
        Backspace,
        Enter,
        LeftArrow,
        RightArrow,
        UpArrow,
        DownArrow,
        Home,
        End,
        PageUp,
        PageDown,
        Delete,
        CtrlUpArrow,
        CtrlDownArrow,
        CtrlShiftK
    }

    /// <summary>
    /// Event args for special key events, allowing handlers to swallow the key.
    /// </summary>
    public class SpecialKeyEventArgs
    {
        public SpecialKey Key { get; }
        public bool ShouldSwallow { get; set; }

        public SpecialKeyEventArgs(SpecialKey key)
        {
            Key = key;
            ShouldSwallow = false;
        }
    }

    // ==================== State ====================

    private IntPtr _hookId = IntPtr.Zero;
    private readonly HookProc _hookCallback;
    private bool _disposed;
    private readonly HashSet<int> _keysDown = new();

    // ==================== Constructor ====================

    public KeyboardHookService()
    {
        _hookCallback = HookCallback;
    }

    // ==================== Public Methods ====================

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
            return;

        _hookId = SetHook(_hookCallback);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to set keyboard hook. Error code: {error}");
        }
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _keysDown.Clear();
    }

    // ==================== Hook Setup ====================

    private IntPtr SetHook(HookProc proc)
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    // ==================== Hook Callback ====================

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int msgType = wParam.ToInt32();
        
        if (msgType == WM_KEYDOWN || msgType == WM_SYSKEYDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vkCode = (int)hookStruct.vkCode;

            if (!_keysDown.Contains(vkCode))
            {
                _keysDown.Add(vkCode);
                
                bool shouldSwallow = ProcessKeyDown(vkCode);
                
                if (shouldSwallow)
                {
                    // Swallow this key - don't pass to next hook
                    return new IntPtr(1);
                }
            }
        }
        else if (msgType == WM_KEYUP)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            _keysDown.Remove((int)hookStruct.vkCode);
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ==================== Key Processing ====================

    private bool ProcessKeyDown(int vkCode)
    {
        if (IsSpecialKey(vkCode, out SpecialKey specialKey))
        {
            var args = new SpecialKeyEventArgs(specialKey);
            SpecialKeyPressed?.Invoke(args);
            return args.ShouldSwallow;
        }

        char? character = VirtualKeyToChar(vkCode);
        if (character.HasValue)
        {
            CharacterTyped?.Invoke(character.Value);
        }

        return false; // Don't swallow
    }

    private bool IsSpecialKey(int vkCode, out SpecialKey specialKey)
    {
        bool ctrlPressed = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        bool shiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        // Ctrl+Shift+K for global toggle
        if (ctrlPressed && shiftPressed && vkCode == 0x4B) { specialKey = SpecialKey.CtrlShiftK; return true; }

        // Ctrl+Up/Down for cycling suggestions
        if (ctrlPressed && vkCode == VK_UP) { specialKey = SpecialKey.CtrlUpArrow; return true; }
        if (ctrlPressed && vkCode == VK_DOWN) { specialKey = SpecialKey.CtrlDownArrow; return true; }

        specialKey = vkCode switch
        {
            VK_TAB => SpecialKey.Tab,
            VK_ESCAPE => SpecialKey.Escape,
            VK_BACK => SpecialKey.Backspace,
            VK_RETURN => SpecialKey.Enter,
            VK_LEFT => SpecialKey.LeftArrow,
            VK_RIGHT => SpecialKey.RightArrow,
            VK_UP => SpecialKey.UpArrow,
            VK_DOWN => SpecialKey.DownArrow,
            VK_HOME => SpecialKey.Home,
            VK_END => SpecialKey.End,
            VK_PRIOR => SpecialKey.PageUp,
            VK_NEXT => SpecialKey.PageDown,
            VK_DELETE => SpecialKey.Delete,
            _ => default
        };

        return vkCode is VK_TAB or VK_ESCAPE or VK_BACK or VK_RETURN or
                          VK_LEFT or VK_RIGHT or VK_UP or VK_DOWN or
                          VK_HOME or VK_END or VK_PRIOR or VK_NEXT or VK_DELETE;
    }

    private char? VirtualKeyToChar(int vkCode)
    {
        if (vkCode is >= 0x41 and <= 0x5A)
        {
            char c = (char)vkCode;
            bool shiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            return shiftPressed ? c : char.ToLower(c);
        }
        
        if (vkCode is >= 0x30 and <= 0x39)
        {
            bool shiftPressed = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            if (shiftPressed)
            {
                return vkCode switch
                {
                    0x30 => ')',
                    0x31 => '!',
                    0x32 => '@',
                    0x33 => '#',
                    0x34 => '$',
                    0x35 => '%',
                    0x36 => '^',
                    0x37 => '&',
                    0x38 => '*',
                    0x39 => '(',
                    _ => null
                };
            }
            return (char)vkCode;
        }

        if (vkCode == 0x20)
            return ' ';

        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        
        return vkCode switch
        {
            0xBA => shift ? ':' : ';',
            0xBB => shift ? '+' : '=',
            0xBC => shift ? '<' : ',',
            0xBD => shift ? '_' : '-',
            0xBE => shift ? '>' : '.',
            0xBF => shift ? '?' : '/',
            0xC0 => shift ? '~' : '`',
            0xDB => shift ? '{' : '[',
            0xDC => shift ? '|' : '\\',
            0xDD => shift ? '}' : ']',
            0xDE => shift ? '"' : '\'',
            _ => null
        };
    }

    // ==================== IDisposable ====================

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~KeyboardHookService()
    {
        Dispose();
    }
}
