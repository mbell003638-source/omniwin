# OmniWin

**Let an AI use your whole Windows PC, safely, and learn your everyday tasks.** OmniWin is a Windows MCP server (contract 2.6,
36 tools): it reads the real buttons and fields of desktop apps and of its own browser, acts on them with checks before and after
every input, refuses anything that looks like deleting, and stops at once on Ctrl+Alt+Shift+K. A task the AI does once is saved as a
recipe and, after three clean replays, runs again from the user's words in one call.

- **Start here (people and AIs):** `handoff/START_HERE.md`: install from the Release zip (self-contained, no .NET needed), connect
  your AI, import the learned tasks.
- **Learned tasks:** `learned/` (38 everyday tasks, 260 wordings, taught on a Windows 11 English PC; `learned/README.md`).
- **The app:** `app/` (dictation, command box, polish selected text; built from Handy, MIT).
- **Every tool:** `docs/TOOLS.md`. **Install details:** `docs/INSTALL.md`. **Full history and contract:** `handoff/`.
- Licence: MIT (`LICENSE`).

---

## Developer notes (from the project)


A Windows MCP server that lets an AI client observe the desktop and browser as lists of real elements and act on them with checks before and after every input. The plan and the build contract live in `C:\Users\<you>\Desktop\OmniWin_Handoff\`; read `02_Architecture_and_Contracts.md` first.

**Status** — contract 2.5 (see "Contract 2.5" below; the sentence that follows describes the 26 tools of 2.4, to which 2.5 adds `omniwin_guide`, `omniwin_do` and `engines_list`, and optionally `engine_call` and the power tools). The server registers 26 tools: status and `wait_for`; the desktop tools; the browser tools (`browser_tabs`, `browser_nav`, `browser_observe`, `browser_act`, `browser_view`); the system tools; `screen_ocr` and the look-only `screen_view`; and the run log and workflow tools. Nothing is blocked except deleting. Install and connect it with `docs/INSTALL.md`; every tool is in `docs/TOOLS.md`; per-workstream status is in `docs/status/`.

## Build and test

```
dotnet build OmniWin.sln -warnaserror
dotnet test --solution OmniWin.sln
```

Requires the .NET 10 SDK (pinned in `global.json`).

The desktop input fixture is opt-in. It briefly takes control of the physical mouse and keyboard and holds `Local\OmniWin.UiInputTestLock`. On an unlocked interactive desktop, set `OMNIWIN_UI_TESTS=1` before running the desktop tests. Leave that variable unset for the regular test suite.

The SDK is installed at `C:\Program Files\dotnet\dotnet.exe` on this machine. A shell opened before installation may need that full path.

## Background operation

Controls that advertise UIA patterns support `desktop_act` with `invoke`, `set_value`, `clear`, `toggle`, `select`, `expand`, `collapse`, or `scroll_into_view` while you work in another window. `set_value` replaces the control's full text.

Physical `click`, `type`, `desktop_keys`, and wheel scrolling use the shared desktop input path and its foreground, focus, visibility, and user-activity checks. App responses such as opening a dialog can change focus.

## Run

The MCP client starts the server over stdio:

```
src\OmniWin.Server\bin\Debug\net10.0-windows10.0.19041.0\OmniWin.Server.exe [--data-dir <path>] [--test-mode] [--allow-elevated]
```

Use the EXE so its PerMonitorV2 manifest takes effect. Launching the DLL with `dotnet` bypasses that executable manifest. Use the desktop test EXE for opt-in input scenarios for the same reason.

stdout carries only MCP JSON-RPC; logs go to stderr. Config, logs and workflows live in `%LOCALAPPDATA%\OmniWin` unless `--data-dir` is given. Tests must always pass `--data-dir`.

**Moving OmniWin to another PC:** see `MOVING_TO_ANOTHER_PC.md`.

## Contract 2.6 (learning, built 2026-11-15; unit suites passed, live checks NOT run yet)

- **Do it once, then replay it.** The AI calls `omniwin_do` with your words; if OmniWin has no way to do it, the AI does the task and then calls `task_done`. The steps that worked are saved as a recipe (`workflows\` and `recipes\` in the data folder; plain files, nothing is ever replaced or deleted). After 3 clean replays the same request, with new values, runs with no step-by-step AI work. A replay that meets a changed screen stops at that step and is repaired the next time the AI finishes the task.
- **Selected text:** `selection_get`, `selection_replace`, `polish_prompts` let the AI polish or rewrite what you highlighted, with no API key.
- **Decision model:** an optional slot, off (`decision.provider` is `none`); no model is wired.
- **Switches** in `config.json`: `learning.enabled`, `learning.auto_run`, `decision.*`. Details: `OmniWin_Handoff_Architecture_and_Contracts.md` section 19 and `26_Builder_Report.md`.
- **The app (built from Handy):** start `publish\app\omniwin-app.exe`. It has Handy's dictation and its speech-model list, plus an OmniWin page (command box, learned tasks) and three OmniWin hotkeys with no key until you set one (command box, voice command, polish selection). Its data stays in `publish\app\Data`. Not yet used by anyone; see report 26, addendum 7.
- **The app (interim):** start `publish\OmniWin.App.exe`. It puts an icon in the tray: a command box (type a request; it runs quick commands and learned tasks), Polish selected text, and Settings. Its hotkeys are off until you tick them in Settings. Dictation and spoken commands are built in too (whisper.cpp on this PC): set the path of whisper-cli.exe and press Download for a model in Settings, then tick the hotkeys. None of that has been tried on a real microphone yet.
- **Not done:** the live checks; nobody has looked at the app's windows yet; dictation and voice.

## Contract 2.5 (mixed engines, built 2026-11-13; full suites passed; live checks passed 2026-11-14: hover, drag, labelled view, `omniwin_do`, one click through each engine, nine tasks 20/20, clicks, typing)

- **Safety note:** the no-delete rule covers OmniWin's own tools. What a bridged engine does through `engine_call` is not checked for deleting and does not pause for your mouse or keyboard (`docs/TOOLS.md`, "Engines"; `engines.enabled` in `config.json` turns `engine_call` off).

- **`retry=` on every action result line:** `done`, `safe`, `observe_first`, `check_state`, `wait` or `no`, derived in one place (`RetryAdvice`) from the status, the reason and `input_sent`. Once input may have been sent and the result is not confirmed it is always `check_state` (never repeat blindly).
- **`omniwin_guide(topic)`:** version-matched usage guides for the model (`start`, `desktop`, `browser`, `system`, `workflows`, `recovery`, `engines`, `router`, `power`, `tools`). The `tools` topic is generated from the tools that are really registered.
- **`desktop_act` `hover` and `drag`:** same pointer checks as a click, on both ends of a drag; the mouse button is always released; a drop onto the Recycle Bin or a delete action is refused. **`screen_view labels=true`** draws the refs of the window's last `desktop_observe` on the picture (password areas stay black and carry no label). **`media` `play` and `pause`** are safe to repeat (nothing is sent if it is already so).
- **Bridged engines:** `engines_list` and (when an engine is installed) `engine_call` forward to Windows-MCP and agent-desktop, which OmniWin starts as child MCP servers from `tools\` (see `tools\README.md` for versions and hashes). Their results are never `confirmed`; an engine call holds the input lane so OmniWin and an engine never drive the mouse together; the stop key ends the engine. Windows-MCP runs with telemetry off, its own input protection and indicator off (`--no-control`) and its PowerShell, Registry, FileSystem and Process tools filtered out unless power tools are on.
- **Power tools (`shell`, `files`, `registry`, `process`) are OFF** (`power_tools.enabled` in `config.json`). Deleting and replacing files also need `power_tools.allow_delete`.
- **`omniwin_do(text)`:** a quick local router for short commands (pause, volume 30, switch to chrome, open notepad, run workflow x). Only an exact, unambiguous match runs, through the normal tools; everything else comes back as `needs_brain` with nothing sent. Stage B (the Laya model, `tools\laya`) is wired but **stays off: it failed its offline gate on 2026-11-14** (61% accuracy, 4.8% wrong actions even at 0.995 confidence, 827 ms median on this CPU; details in `tools\README.md`).

## Behaviour worth knowing

- **Result statuses** are `confirmed`, `unverified`, `contradicted`, `timeout`, `blocked`, `stale`, `denied` and `error`; `input_sent` always says whether anything reached the app. A workflow run is `confirmed` only when every step was `confirmed`; otherwise it is `unverified` and names the steps.
- **Channels** include `key:F24`: `window_manage focus` reports it when the focus fallback had to tap the F24 key.
- **Typing** sends one character at a time and waits for each. A lost character is sent again at most once, and an act that re-sent anything is `confirmed` only when the text then stayed unchanged for 500 ms and matches exactly. The time limit grows by 60 ms per character, up to 120 s. During a long `type`, reads are still answered between characters, and another act is told at once (`blocked rate_limit`, nothing sent) instead of waiting.
- **File dialogs:** `set_value` and `clear` on a File name box or Explorer's address bar return `unverified`, because a matching readback does not prove the app used the value. Use `type`.
- **Remote Desktop:** the server needs a connected, unlocked session. When the session's screen cannot be reached, actions return an error instead of sending input.

## Layout and ownership

Every path has one owning workstream (contract §4). Don't edit files you don't own; propose changes in `docs/contract-proposals/`.

WS1 integration includes the Safety registration, shared contract update, and minimal input fixture required to exercise the desktop engine. Browser, system-tools/OCR, workflows, and the broader benchmark suite are tracked in the handoff folder.
