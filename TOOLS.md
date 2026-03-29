# TOOLS.md - Project Notes

## Project

- **Keystroke** — Windows system-wide AI autocomplete app
- **Language:** C# / .NET 8
- **Workspace:** C:\Users\nickk\Projects\Keystroke

## Key APIs (Learn These)

| API | What it does | P/Invoke DLL |
|-----|-------------|--------------|
| `SetWindowsHookEx` | Intercept keyboard input system-wide | user32.dll |
| `UnhookWindowsHookEx` | Remove a keyboard hook | user32.dll |
| `SendInput` | Simulate keystrokes (type into any app) | user32.dll |
| `GetForegroundWindow` | Get handle of active window | user32.dll |
| `GetWindowText` | Get title of a window | user32.dll |
| `GetWindowThreadProcessId` | Get process ID of a window | user32.dll |
| `GetCaretPos` | Get cursor position (app-dependent) | user32.dll |

## UI Automation (C# built-in)

- Namespace: `System.Windows.Automation`
- Key classes: `AutomationElement`, `TextPattern`, `ValuePattern`
- Tool: Accessibility Insights for Windows (free from Microsoft)

## Telegram

- Nick's chat ID: 5462896026
- Switch to this agent: `/agent keystroke`
- Switch back to GLM: `/agent main`
