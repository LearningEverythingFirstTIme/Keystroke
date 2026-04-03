using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using WindowsInput;

namespace KeystrokeHook;

/// <summary>
/// Phase 1: "Hello World" Input Listener
/// 
/// Uses InputSimulator library (well-tested wrapper around SendInput).
/// </summary>

class Program
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

    private const int VK_F12 = 0x7B;

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

    // ==================== State ====================

    private static IntPtr _hookId = IntPtr.Zero;
    private static readonly HookProc _hookCallback = HookCallback;
    
    // Queue for typing requests
    private static readonly BlockingCollection<string> _typingQueue = new BlockingCollection<string>();
    private static Thread? _typingThread;
    private static volatile bool _isRunning = true;
    private static volatile bool _isTyping = false;  // Prevent queueing while typing
    
    // InputSimulator instance (thread-safe)
    private static readonly InputSimulator _simulator = new InputSimulator();

    // ==================== Main ====================

    static void Main(string[] args)
    {
        Console.WriteLine("Keystroke Hook - Phase 1");
        Console.WriteLine("Press F12 to type 'Hello World'");
        Console.WriteLine("Press Ctrl+C to exit\n");

        // Start the dedicated typing thread
        _typingThread = new Thread(TypingWorker)
        {
            IsBackground = true,
            Name = "TypingWorker"
        };
        _typingThread.Start();

        // Set up the input listener
        _hookId = SetHook(_hookCallback);

        if (_hookId == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Console.WriteLine($"Failed to set hook! Error code: {error}");
            return;
        }

        Console.WriteLine("Hook installed. Open Notepad and press F12...\n");

        // Run message loop
        MessageLoop();

        // Cleanup
        _isRunning = false;
        _typingQueue.CompleteAdding();
        UnhookWindowsHookEx(_hookId);
    }

    // ==================== Hook Setup ====================

    private static IntPtr SetHook(HookProc proc)
    {
        using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
        using (var curModule = curProcess.MainModule!)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    // ==================== Hook Callback ====================

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hookId, nCode, wParam, lParam);

        int msgType = wParam.ToInt32();
        if (msgType == WM_KEYDOWN || msgType == WM_SYSKEYDOWN)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            
            if (hookStruct.vkCode == VK_F12 && !_isTyping)
            {
                _typingQueue.TryAdd("Hello World");
                return new IntPtr(1);  // Block F12
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ==================== Typing Worker Thread ====================

    private static void TypingWorker()
    {
        foreach (var text in _typingQueue.GetConsumingEnumerable())
        {
            if (!_isRunning) break;
            
            _isTyping = true;
            
            try
            {
                // Small delay to let F12 release
                Thread.Sleep(50);
                
                // Type each character with a delay to ensure proper ordering
                foreach (char c in text)
                {
                    _simulator.Keyboard.TextEntry(c.ToString());
                    Thread.Sleep(15);  // 15ms between characters
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
            finally
            {
                _isTyping = false;
            }
        }
    }

    // ==================== Message Loop ====================

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private static void MessageLoop()
    {
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }
}
