---
type: project
tags: [project, windows, ai, autocomplete, idea]
status: idea
tech: C#, .NET 8, Win32 API, UI Automation
---

# Windows Autocomplete App

> [!idea] The Idea
> A system-wide AI-powered text prediction app for Windows that works **everywhere** — Discord, Telegram, Slack, Notepad, browsers, Office, any app that accepts text input. Think Lightkey, but it actually works across all your apps instead of just Office + Chrome.

## Why This Doesn't Exist Yet

- **Lightkey** — works in Office + Chrome/Edge only. Not context-aware (learns your patterns, doesn't read the conversation). $50/year for the full version.
- **Windows Copilot** — it's a chat sidebar, not inline autocomplete. Different thing entirely.
- **Text Blaze** — Chrome extension only. Template-based, not AI-powered.
- **Continue.dev / Tabby** — code editor autocomplete only. Doesn't touch Slack, email, etc.

The gap is real. Nobody has shipped a good system-wide autocomplete that works in native desktop apps like Discord, Telegram, and Slack on Windows.

## The Big Picture

This app does five things, in order:

1. **Watches you type** — intercepts keystrokes across all apps
2. **Reads what you've typed** — grabs the text in the current field
3. **Predicts what comes next** — sends context to a prediction engine (AI or pattern-based)
4. **Shows you the suggestion** — displays a floating panel near your cursor
5. **Types it for you** — when you press Tab, it "types" the completion into the app

That's it. Everything else is details.

## Phased Approach

> [!tip] Don't try to build the whole thing at once. Each phase is usable on its own.

### Phase 1: "Hello World" — Type Into Notepad (Weekend Project)

The goal: prove you can hook the keyboard and type characters into another app programmatically.

**What you'll build:**
- A tiny C# console app
- It listens for a specific key combo (e.g., Ctrl+Shift+Space)
- When triggered, it types "Hello from my autocomplete app!" into whatever app you're in

**What you'll learn:**
- How to set up a .NET 8 C# project
- How to use `SetWindowsHookEx` (the Win32 API that lets you watch all keyboard input)
- How to use `SendInput` (the Win32 API that lets you simulate typing)

**Step by step:**

1. **Install .NET 8 SDK** if you don't have it
   - Download from https://dotnet.microsoft.com/download
   - Open PowerShell and run `dotnet --version` to verify

2. **Create a new C# project**
   ```powershell
   mkdir WindowsAutocomplete
   cd WindowsAutocomplete
   dotnet new console
   ```

3. **Add NuGet packages** (pre-built libraries that wrap the Win32 APIs)
   ```
   dotnet add package NHook -- or just use P/Invoke directly (more learning, more control)
   ```
   Actually, for learning purposes, use **P/Invoke** (calling Windows APIs directly from C#). It's the foundation you need.

4. **The keyboard hook** — This is the core piece. In your `Program.cs`:
   - Call `SetWindowsHookEx` with `WH_KEYBOARD_LL` (low-level keyboard hook)
   - This gives you a callback function that fires every time any key is pressed on the entire system
   - In the callback, check if the user pressed your trigger key combo
   - If yes, call `SendInput` to simulate typing your text

5. **The text injection** — `SendInput` takes an array of `INPUT` structures. Each one represents a key press or key release. To type "Hello", you send:
   - Key down H, Key up H
   - Key down E, Key up E
   - Key down L, Key up L
   - Key down L, Key up L
   - Key down O, Key up O
   - There's a `SendKeys` helper in .NET that does this for you, but understanding `SendInput` is important

**Success criteria:** You press Ctrl+Shift+Space in Notepad and "Hello World" appears.

> [!warning] Security note
> Keyboard hooks are literally the same mechanism keyloggers use. Antivirus software might flag your app. This is normal and expected — it's a false positive. You're not doing anything malicious, you're just using the same API. If Windows SmartScreen blocks it, click "More info" → "Run anyway."

### Phase 2: Buffer Typing and Show Suggestions (1-2 Weeks)

The goal: accumulate what the user types, show a suggestion, and accept it with Tab.

**What you'll build:**
- A Windows system tray app (runs in the background, has a settings icon in the taskbar)
- It accumulates keystrokes into a text buffer
- Every few keystrokes (or after a pause), it generates a prediction
- The prediction appears in a small floating panel near the cursor
- Press Tab to accept (inject the completion), Escape to dismiss

**What you'll learn:**
- WPF (Windows Presentation Foundation) — the UI framework for the floating panel
- System tray apps — how to make an app that lives in the background
- Window positioning — how to find where the cursor is on screen and place a panel there
- Basic prediction logic — even a simple approach works for v1

**Step by step:**

1. **Convert to a WPF app**
   ```powershell
   dotnet new wpf -n WindowsAutocomplete
   ```
   WPF gives you proper windows, transparency, and UI controls.

2. **Create the floating suggestion panel**
   - A borderless WPF window with these properties:
     - `WindowStyle="None"` — no title bar or borders
     - `AllowsTransparency="True"` — can be semi-transparent
     - `Topmost="True"` — always appears above other windows
     - `ShowInTaskbar="False"` — doesn't show in the taskbar
   - Style it with a semi-transparent dark background and light text
   - Position it using `Cursor.Position` (gives you screen X,Y of the mouse)

3. **Build the typing buffer**
   - In your keyboard hook callback, when you see regular character keys (letters, numbers, space), add them to a `StringBuilder`
   - When you see Backspace, remove the last character
   - When you see Enter, Escape, or Tab, clear the buffer (and handle the action)
   - When you see any non-character key (arrow keys, F-keys, etc.), ignore it

4. **Trigger predictions**
   - After each character is added to the buffer, start a short timer (300ms — a "debounce")
   - If no new character arrives within 300ms, generate a prediction
   - This prevents firing a prediction on every single keystroke

5. **The prediction engine (v1 — dead simple)**
   For v1, you don't even need AI. Options:
   - **Option A:** Hard-code a dictionary of common phrases. If the buffer starts with "th", suggest "the", "there", "they're", "thanks". Simple lookup table.
   - **Option B:** Call an LLM API. Send the buffer text + window title to GPT/GLM/whatever and ask "complete this". This is more impressive but adds latency and cost.
   - **Option C:** Use a local n-gram model. Read a bunch of text files (your emails, chat logs) and build a simple frequency table of what words follow what. This is what Lightkey does under the hood.

   > [!tip] Recommendation: Start with Option B (API call). It's the most impressive for the least effort. You already know how to call APIs. Swap to on-device later.

6. **Accept with Tab**
   - In your keyboard hook, detect Tab key press
   - If a suggestion is showing, cancel the Tab (return 1 from the hook to swallow the key)
   - Clear the buffer and use `SendInput` to type the full suggested text

**Success criteria:** You type "hel" in Notepad, a floating panel shows "hello world", you press Tab, and the full text appears.

### Phase 3: Read Context from Apps (2-3 Weeks)

The goal: the app can read what's already on screen (previous messages, email threads, etc.) and use that as context for predictions.

**What you'll build:**
- A context-gathering layer that reads text from the active window
- This text gets sent to the prediction engine alongside what you're typing
- The prediction engine now has full conversation context

**What you'll learn:**
- UI Automation — the Windows API for reading text and controls from other apps
- Accessibility Insights for Windows — a free Microsoft tool for debugging what UI Automation can see
- Window management APIs — getting the active window, process info, etc.

**Step by step:**

1. **Install Accessibility Insights for Windows** (free from Microsoft)
   - This is essential. It lets you point at any app and see what text/UI elements Windows can read from it.
   - You'll use this constantly to figure out what works and what doesn't.
   - Download: search "Accessibility Insights for Windows" or get it from the Microsoft Store

2. **Get the active window info**
   ```csharp
   // These are Win32 APIs called via P/Invoke
   GetForegroundWindow()       // → window handle (HWND)
   GetWindowText()            // → window title (e.g., "Nick Kessler - Slack")
   GetWindowThreadProcessId() // → process ID
   ```

3. **Try reading text via UI Automation**
   ```csharp
   using System.Windows.Automation;

   // Get the element at the cursor position
   var element = AutomationElement.FromPoint(new Point(x, y));

   // Try to get text from it
   if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj)) {
       var textPattern = (TextPattern)patternObj;
       var text = textPattern.DocumentRange.GetText(-1);
   }
   ```

4. **Test what works and what doesn't**
   - Open Notepad → point Accessibility Insights at it → you can read all the text ✓
   - Open Word → works ✓
   - Open Slack (desktop) → probably partial or broken ✗
   - Open Discord → probably partial or broken ✗
   - Open a browser → partial (works on regular text fields, not rich editors) ⚠️

5. **Build a fallback strategy**
   For apps where UI Automation doesn't work (Electron apps like Slack/Discord):
   - **Clipboard trick:** Programmatically send Ctrl+A, Ctrl+C to select all and copy, read the clipboard, then Ctrl+Z to undo. Hacky but works.
   - **OCR fallback:** Use Windows' built-in OCR API to screenshot the window and extract text. Slower (~200ms) but universal.
   - **App name as context:** Even just knowing you're in "Slack - #general" is useful context for the LLM.

6. **Send context to the prediction engine**
   Instead of just sending "th" (what you typed), send:
   ```
   App: Slack - #general
   Recent messages:
   [Noah]: hey want to grab dinner tomorrow night?
   [Beck]: shipped the final launch assets
   [Nick]: th
   Complete what Nick would type next:
   ```

**Success criteria:** In Slack, someone asks you a question and your app suggests a relevant response based on the conversation context.

### Phase 4: Polish and Ship (2-3 Weeks)

**What you'll build:**
- Proper settings UI (enable/disable per-app, prediction sensitivity, API key input)
- App whitelist/blacklist (don't show suggestions in password fields, games, etc.)
- Startup on login
- Proper error handling
- An installer or portable executable

**Step by step:**

1. **Settings UI** — Add a settings window to your system tray app with:
   - Toggle on/off globally
   - Per-app enable/disable list
   - API key input (for cloud LLM)
   - Prediction sensitivity slider (how aggressively it suggests)
   - Trigger key customization (default: Tab, but let people change it)

2. **Password field detection** — When UI Automation detects a password field, suppress suggestions. This is a security must-have.

3. **Auto-start on login** — Add a registry entry or shortcut to the Startup folder.

4. **On-device model (optional but cool)** — Switch from cloud API to local inference:
   - Use `llama.cpp` with a small model (Qwen2.5-Coder 1.5B or similar)
   - Run it as a background process
   - Your ProArt GPU can handle this easily at 4-bit quantization
   - Latency drops to ~30-60ms (faster than cloud API)
   - No API costs, works offline, more private

5. **Package it up** — Use a tool like Inno Setup or WiX to create an installer.

**Success criteria:** You can install it on a fresh Windows machine and it just works.

## Things You'll Need to Learn

> [!warning] This looks like a lot, but you don't need to learn it all at once. Each phase only requires a subset.

### Already Know
- C# basics (from work)
- Calling APIs (Z.AI, OpenAI, etc.)
- General programming concepts

### Need to Learn

**1. Win32 API / P/Invoke (Most Important)**
- What it is: The low-level Windows APIs that control everything — keyboard input, windows, processes, etc.
- P/Invoke: How C# calls these C/C++ APIs. It's just adding `[DllImport("user32.dll")]` above a method signature.
- Key APIs you'll use:
  - `SetWindowsHookEx` — intercept keyboard input
  - `SendInput` — simulate typing
  - `GetForegroundWindow` — get the active window
  - `GetWindowText` — get the window title
  - `GetCaretPos` — get the text cursor position (tricky, see below)
- **Learn time: 1-2 weeks** for the subset you need
- **Resources:**
  - Pinvoke.net — has C# signatures for every Win32 API: https://www.pinvoke.net/
  - Microsoft Learn docs on Windows API

**2. WPF Basics**
- What it is: Windows Presentation Foundation — Microsoft's UI framework for desktop apps
- You need: borderless windows, transparency, positioning, system tray icons
- You already know HTML/CSS — WPF's XAML is similar in concept (markup for UI layout)
- **Learn time: 3-5 days**
- **Resources:**
  - Microsoft's WPF tutorials
  - Just Google "WPF borderless transparent window" — tons of examples

**3. UI Automation**
- What it is: Windows' accessibility API that lets you read text and controls from other apps
- You need: reading text content from the active window
- **Learn time: 1 week**
- **Resources:**
  - Accessibility Insights for Windows (free tool, essential)
  - System.Windows.Automation namespace docs

**4. LLM Integration (Easiest — You Already Know This)**
- You already call APIs. This is the same thing.
- The new part is prompt engineering for fast completions (short prompts, streaming responses, low latency)

## Common Pitfalls

> [!warning] I'm listing these now so you don't waste time on them later.

1. **The keyboard hook must return fast** — Your hook callback has ~10ms before Windows skips it. Don't do heavy work (API calls, file I/O) inside the callback. Just record the keystroke and return. Do the heavy work on a separate thread.

2. **SendInput sends to the FOREGROUND window** — It types into whatever app is currently focused. If your floating panel steals focus when it appears, SendInput will type into your panel, not the target app. Solution: your panel must never take focus. Use `ShowWithoutActivation()` in WPF.

3. **Electron apps (Slack, Discord, Notion) are the hardest** — They don't properly implement Windows accessibility APIs. Your app will work great in Notepad/Word/Outlook and be janky in Slack. This is a known Windows problem, not a you problem. Accept it and use fallbacks.

4. **Antivirus will flag your app** — Keyboard hooks trigger heuristics. This is expected. You'll need to handle it gracefully (maybe even sign the executable eventually).

5. **Don't build the perfect architecture first** — Build the ugliest working prototype you can. A console app that types "hello" into Notepad is a massive milestone. Celebrate it. Then iterate.

## Tech Stack Summary

| Component | Technology | Why |
|-----------|-----------|-----|
| Language | C# / .NET 8 | You already know it |
| UI Framework | WPF | Native Windows, supports transparency & borderless windows |
| Keyboard Hook | Win32 `SetWindowsHookEx` via P/Invoke | The only way to intercept all keystrokes system-wide |
| Text Injection | Win32 `SendInput` via P/Invoke | Simulates real typing, works everywhere |
| Context Reading | UI Automation + OCR fallback | Read text from other apps |
| Prediction Engine v1 | Cloud LLM API | Fastest to ship, impressive results |
| Prediction Engine v2 | On-device `llama.cpp` | Faster, free, offline, private |
| Packaging | Inno Setup or WiX | Create an installer |

## Project Name Ideas

- **Keystroke** — simple, descriptive
- **Ghost** — after "ghost text" (the faded suggestion)
- **FlowType** — emphasizes the flow of typing
- **Tap** — short, memorable, what you do to accept

## Time Estimate

| Phase | What | Time |
|-------|------|------|
| 1 | Keyboard hook + type into Notepad | 1 weekend |
| 2 | Floating panel + Tab to accept | 1-2 weeks |
| 3 | Context reading from apps | 2-3 weeks |
| 4 | Polish, settings, packaging | 2-3 weeks |
| **Total** | **Working product** | **6-8 weeks** (part-time) |

## Related

- [[Projects MOC]]
- [[Ideas/Instinct for Windows Research|Instinct for Windows Research]] — the deep dive into why this is hard and what Instinct does
- [Lightkey](https://www.lightkey.io/) — the existing Windows option (Office + Chrome only)
- [Instinct](https://www.instinct.co/) — the Mac app that inspired this
- [Cotypist](https://cotypist.app/) — another Mac-only autocomplete app
