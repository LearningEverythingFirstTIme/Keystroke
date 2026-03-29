# AGENTS.md - Keystroke Project Workspace

## About This Agent

This is a dedicated coding agent for building the Keystroke Windows autocomplete app. It has its own workspace, memory, and session history separate from GLM (the main agent).

## The Project

**Keystroke** is a system-wide AI-powered text prediction app for Windows that works everywhere — Discord, Telegram, Slack, Notepad, browsers, Office, any app that accepts text input.

**Full project spec:** See `PROJECT_SPEC.md` in this workspace.

**Obsidian reference:** `[[Projects/Windows Autocomplete App]]` in the Obsidian vault has the detailed step-by-step guide.

## Session Startup

Before doing anything else:

1. Read `SOUL.md` — who you are
2. Read `USER.md` — who you're helping
3. Read `PROJECT_SPEC.md` — what you're building
4. Read `memory/` for recent progress (today + yesterday if they exist)
5. Check the actual code in the project directory to see current state

## Working Directory

The project code lives here in this workspace. When creating new files, put them in appropriate subdirectories:

```
Keystroke/
├── src/           # Source code
├── docs/          # Technical docs and notes
├── memory/        # Session logs and progress
├── PROJECT_SPEC.md
├── SOUL.md
├── AGENTS.md
└── ...
```

## Coding Guidelines

- **C# / .NET 8** — the primary language
- **Write actual files** — use `edit` and `write` tools, don't just describe code
- **Explain what you're doing** — Nick is learning, not just delegating
- **Build and test** — run `dotnet build` and `dotnet run` to verify things work
- **Keep it simple** — prototype first, refactor later
- **Use P/Invoke** for Win32 APIs — this is a learning opportunity, not a shortcut

## Memory

- **Daily logs:** `memory/YYYY-MM-DD.md` — what was built, decisions made, problems hit
- **Write during work**, not after
- Track: what works, what breaks, what's confusing, what's next

## Windows-Specific Rules

- **Do NOT use `gateway config.patch`** — orphans the process on Windows
- **Do NOT use `gateway restart`** — same issue
- Edit files directly with the `edit` tool

## Safety

- Don't touch files outside this workspace
- Don't run destructive commands without asking
- `trash` > `rm`
- Ask before anything that leaves the machine
