# OllieBoard for Windows

Anime desktop pets for Windows 10 and 11. The characters wander around your Desktop, sleep, play with each other, and kick and tidy your real Desktop icons. Every AI coding agent session you have running (Claude Code, Codex, Gemini…) also gets its own mascot that tells you when the agent is done or needs your approval.

This is a port of the macOS app ([OllieBoard](https://github.com/vfa-khuongdv/OllieBoard)) to C# / .NET 8 (WinForms). It has the same characters, special moves, play-room scenes, dialogue (Vietnamese, English, Japanese) and sprite sheets. The app lives in the notification area (the paw icon next to the clock) and has no taskbar button.

## Install

1. Download `OllieBoard-<version>-win-x64-setup.exe` from [Releases](https://github.com/khdev3jk/OllieBoard-win/releases/latest) and run it. It installs for your user only (no admin prompt) into `%LOCALAPPDATA%\Programs\OllieBoard`. You can tick **Start OllieBoard when Windows starts** during setup.
2. The installer isn't code-signed, so SmartScreen may warn: choose **More info** → **Run anyway**.
3. If the paw icon is hidden, click **^** next to the clock, or pin it via Settings → Personalization → Taskbar → Other system tray icons.

Requirements: Windows 10 or 11, x64 (ARM64 PCs run it through emulation).

Uninstall from Settings → Apps. Your settings stay in `%APPDATA%\OllieBoard`; delete that folder to remove them too.

## Usage

Click the paw icon (left or right click) to open the menu. It follows the language picked under **Ngôn ngữ / Language / 言語** (Vietnamese by default).

| Menu item | What it does |
| --- | --- |
| Characters | One submenu per series (plus Politics); turn each character on or off. The menu stays open so you can tick several. Only Nezuko is on by default. |
| Play room | Show the room, pick its scenery, choose who is inside it, and move or resize it. |
| Language | Change the menu and dialogue language. |
| Characters for AI agents | Turn agent mascots on or off, pick which agents, install the Claude Code hook. |
| Check for updates / Update to … | See [Updates](#updates). |

Interacting with a character:

- **Click:** the character is happy. Clicking 4 times within 3 seconds annoys it, except Bocchi, who melts. Clicking a sleeping character wakes it up.
- **Drag and drop:** pick a character up and put it somewhere else.
- **Right-click:** the menu of that character's special moves.

### When icons don't move

- **⚠️ Can't reach the Desktop icons:** Explorer isn't running or isn't responding. Hover the item to see the error.
- **⚠️ Desktop Auto arrange is on:** choose **Turn off Auto arrange icons on the Desktop**, or right-click the Desktop → View → uncheck **Auto arrange icons**.
- **ℹ️ Align icons to grid is on:** icons still move, but land on grid cells (a carried icon moves in steps). Choose **Turn off Align icons to grid on the Desktop** for smooth moves, or leave it on.
- If **Show desktop icons** is off (Desktop → View), the characters have nothing to kick.
- Explorer may put moved icons back in their old places after it restarts or the screen layout changes; that's normal.

## AI agent mascots

OllieBoard scans the process list every 2 seconds for `claude`, `codex`, `gemini`, `opencode`, `cursor-agent` and `aider` sessions, both native `.exe` installs and npm/pip installs run through `node` or `python`. Each session gets a mascot labeled with the agent and its working folder (for example `Claude · OllieBoard`). A mascot only appears while its agent is working or waiting for you. When the agent finishes or needs approval, the mascot runs to your mouse cursor. Click it to bring that session's terminal window (Windows Terminal, VS Code, a console window…) to the front.

By default an agent's state is guessed from its CPU usage. For Claude Code you can install hooks to get the exact state: menu → **Characters for AI agents** → **Install the Claude Code status hook…**. This adds hooks for `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PermissionRequest`, `Notification` and `Stop` to `%USERPROFILE%\.claude\settings.json` that run `OllieBoard.exe --hook <event>`. Your existing hooks are kept, and the previous file is backed up as `settings.json.ollieboard-backup`. The hooks only apply to Claude Code sessions started after installing them. They point at the installed exe, so install the hook from the installed app, not from a build folder.

## Updates

An installed OllieBoard checks GitHub for a newer release when it starts and every 6 hours. When one with a Windows installer is out, the menu shows **⬆️ Update to X**: click it and the app downloads the installer, quits, and the installer updates it silently and starts it again.

To publish a release the updater picks up, push a `vX.Y.Z` tag: the [Windows workflow](.github/workflows/windows.yml) builds `OllieBoard-X.Y.Z-win-x64-setup.exe` and attaches it to that release (creating the release if needed). The updater looks for the `-setup.exe` asset of the latest release of this repository.

## Build from source

Requirements: Windows with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0); for the installer also [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).

```powershell
.\build.ps1                  # build\publish\OllieBoard.exe and build\OllieBoard-<version>-win-x64-setup.exe
.\build.ps1 -Version 1.3.0   # also sets the app version the updater compares with the latest release
dotnet run --project src\OllieBoard   # run from source (no self-update)
```

The project also compiles on macOS or Linux (`EnableWindowsTargeting` is on), which is handy for checking that it builds, but the app itself only runs on Windows.

### Secret scanning

Commits are scanned for secrets with [gitleaks](https://github.com/gitleaks/gitleaks) through [pre-commit](https://pre-commit.com). Enable the hook once per clone:

```sh
pre-commit install
pre-commit run --all-files   # scan the whole tree
```

### Regenerating assets from the macOS project

The sprite sheets and dialogue come from the macOS project:

```sh
cp ../OllieBoard/Sources/OllieBoard/Resources/Sprites/{*.png,layout.json} Assets/Sprites/
python3 scripts/convert_dialogue.py ../OllieBoard/Sources/OllieBoard   # writes Assets/dialogue.json
swift scripts/make_icon.swift Assets/OllieBoard.ico                    # the paw icon (macOS only)
```

A new character also needs its entry in `Characters.cs` (and new `Skill`/`Line` cases plus its moves in `Pet.cs`), mirroring `Characters.swift`, `Dialogue.swift` and `Pet.swift`.

## How it differs from the macOS version

| macOS | Windows |
| --- | --- |
| Menu bar item | Notification-area icon |
| Finder icons moved through AppleScript (needs Automation permission) | Explorer's desktop list view, read and moved directly; no permission prompt |
| Finder "Sort By" locks icons | Explorer "Auto arrange icons" locks icons |
| Core Animation effects | The same effects drawn with GDI+ into per-pixel-alpha windows |
| Room below the desktop icons | Room just above the desktop (it covers icons behind it) and below app windows |
| Updates from a `.dmg` | Updates from a silent `-setup.exe` |

## Project layout

```
src/OllieBoard/
  Program.cs          Entry point (also the `--hook` mode for Claude Code)
  TrayApp.cs          Notification-area icon and menu
  World.cs            Owns the characters, the animation clock and icon assignment
  Pet.cs              Behavior and special moves of one character
  PetWindow.cs        Transparent window that draws a character, its effects and bubbles, and handles the mouse
  Effects.cs          Rings, strokes, glows, moving images and particles (Core Animation stand-ins)
  LayeredWindow.cs    Per-pixel-alpha, never-activated windows
  Room.cs             The play room
  RoomScenes.cs       The play room's scenery for each series
  DesktopIcons.cs     Reads and moves Desktop icons through Explorer's list view
  AgentMonitor.cs     Detects running AI agent sessions
  ClaudeHooks.cs      Installs the status hooks for Claude Code
  Updater.cs          Checks GitHub for a newer release and installs it
  Characters.cs       Character list and personalities
  Dialogue.cs         Dialogue lookup (text in Assets/dialogue.json)
  UIText.cs           Menu and alert labels per language
  SpriteSheet.cs      Cuts frames out of the sprite sheets
  Geometry.cs         Coordinates (points, y up, like macOS), screens, clock, main-thread dispatch
  Settings.cs         %APPDATA%\OllieBoard\settings.json
  Native.cs           Win32 declarations
Assets/               Sprite sheets, dialogue.json, the app icon
installer/            Inno Setup script
scripts/              Asset conversion from the macOS project
build.ps1             Publishes the app and builds the installer
```
