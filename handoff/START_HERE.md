# OmniWin: start here (for an AI importing this repository)

OmniWin is a Windows MCP server that lets an AI client (Claude, Codex, Antigravity, any MCP client) see and use the whole Windows
desktop: real buttons and fields through UI Automation, its own dedicated browser, apps, windows, audio and media. Every action is
checked (did it really happen?) and passes a safety gate. Since contract 2.6 it **learns**: a task the AI does once is saved as a
*recipe* and next time runs from the user's words in one call.

## 1. Install (Windows 10 or 11, x64)

Option A, prebuilt (recommended): download the zip from this repository's latest **Release**, unzip it (for example to
`C:\OmniWin\publish`). The server is self-contained: no .NET install is needed. The Release notes list the SHA-256 of the zip.

Option B, build from source: .NET 10 SDK, then from the repository root
`dotnet publish src/OmniWin.Server -c Release -r win-x64 --self-contained -o publish` (see `docs/INSTALL.md`).
The app in `app/` needs Rust, CMake, the MSVC Build Tools, the Vulkan SDK and Bun (`app/BUILD.md`, `app/build-omniwin-app.ps1`).

## 2. Connect your AI

Add an MCP server (stdio) whose command is the full path to `publish\OmniWin.Server.exe`, no arguments. Then ask the AI to call
`omniwin_status` (the first line ends with `contract=2.6`) and `omniwin_guide` (how to use the tools, in the order to try them).
36 tools are registered by default. Details: `docs/TOOLS.md`, `docs/INSTALL.md`.

## 3. Import the learned tasks (optional)

`learned/workflows` and `learned/recipes` hold the tasks taught on the reference PC (`learned/README.md` lists them and the
measured times). Copy both folders into `%LOCALAPPDATA%\OmniWin\` (create it if needed; or into the folder you pass with
`--data-dir`). Then `workflow_list` shows them.

**What to expect:** a recipe remembers controls by their names and layout. On a PC with the same Windows language and app versions
it runs as it did here. Where something differs, it stops safely at that step with nothing more sent (`stale` or
`needs_brain reason=recipe_not_ready`), the AI finishes the task, calls `task_done`, and a repaired version is saved. Imported
recipes keep the trust they earned on the reference PC (it is computed from `recipes/events.jsonl`). To make every imported recipe
earn trust again on the new PC, delete `recipes/events.jsonl` before copying, or disable any recipe with `workflow_manage`.

## 4. Safety you can rely on (and its limits)

- Nothing is blocked except deleting: anything that looks like a delete (a Delete button, a delete command typed into a terminal,
  accepting a "delete?" dialog) is refused. The stop key Ctrl+Alt+Shift+K stops all OmniWin input at once.
- Power tools (shell, files, registry, process) exist but are off (`power_tools.enabled` false).
- Bridged engines (`engine_call`: Windows-MCP, agent-desktop) are **not** checked for deleting and do not pause for the user;
  they are optional and are not included here (see `tools/README.md` to set them up).
- A learned recipe runs by itself only after 3 clean replays, never with a browser step, a Risky step or a step that could not be
  recorded; every replayed step passes the same checks and gate as a single tool call.
- Without a live desktop (a locked screen, or a disconnected Remote Desktop session) OmniWin cannot see or act. To run with nobody
  connected, move the session to the console (`tscon <session id> /dest:console`) and keep the PC from locking or sleeping.

## 5. Where the history is

`handoff/` is the full project history: the contract (`02_Architecture_and_Contracts.md`, the source of truth), the build
orchestration, every builder report and every review, numbered in order (`README.md` there is the index). Personal and machine
details were replaced with placeholders such as `<you>` and `<pc>` before publishing.
