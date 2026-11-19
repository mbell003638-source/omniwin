# Installing and running OmniWin

OmniWin is a Windows program that AI apps talk to over MCP. There is no installer: you publish it to a folder and point your AI app at the program in that folder. Nothing touches the registry, PATH, startup entries or file associations.

Requirements: Windows 11, the .NET 10 SDK to build it (pinned in `global.json`), and Microsoft Edge or Google Chrome for the browser tools. It must run as a normal (non-elevated) program.

## Build and publish

```
dotnet build OmniWin.sln -c Release -warnaserror
dotnet test --solution OmniWin.sln -c Release
dotnet publish src/OmniWin.Server -c Release -o publish
```

The program is `publish\OmniWin.Server.exe`. Always start the `.exe` (its display-scaling manifest only applies to the exe, not to `dotnet OmniWin.Server.dll`).

## First run

On first start OmniWin creates, under `%LOCALAPPDATA%\OmniWin` (or the folder you give with `--data-dir`):

- `config.json`: settings with safe defaults;
- `logs\`: the audit log (`audit-YYYYMMDD.jsonl`);
- `workflows\`: saved workflows;
- `browser-profile\`: OmniWin's own browser profile, created on the first browser tool call. OmniWin never uses your own Edge or Chrome profile, cookies or sign-ins.

`config.json` still accepts a `rate_limit` section so old files keep loading, but it is ignored: OmniWin does not throttle or refuse calls for being too frequent (nothing is blocked except deleting).

Command line: `OmniWin.Server.exe [--data-dir <path>] [--test-mode] [--allow-elevated]`. stdout carries only MCP messages; logs go to stderr.

To try it by hand without an AI app, use a temp data folder: `publish\OmniWin.Server.exe --data-dir %TEMP%\omniwin-try`.

## Connect an AI app (MCP client)

The app starts OmniWin itself over stdio. You only tell it where the program is. Replace the path with the real full path of your published `OmniWin.Server.exe`.

### Antigravity

You add OmniWin yourself, in Antigravity's own screen. **Do not edit Antigravity's settings files by hand**, and nothing else (including OmniWin and the AI that built it) should either.

1. In Antigravity, open its **Manage MCP servers** screen.
2. Add a server and paste this entry (use the real full path of your published `OmniWin.Server.exe`; backslashes are doubled in JSON):

```json
"omniwin": { "command": "C:\\Users\\<you>\\Desktop\\OmniWin\\publish\\OmniWin.Server.exe", "args": [] }
```

3. Save, refresh or restart the MCP connection in that screen, then ask Antigravity to call `omniwin_status`.

If the screen asks for the whole `mcpServers` object instead of one entry, wrap the line above as `{ "mcpServers": { <the entry> } }`.

### Claude Code

```
claude mcp add omniwin -- "C:\Users\<you>\Desktop\OmniWin\publish\OmniWin.Server.exe"
```

or put the same `command`/`args` entry under `mcpServers` in the project's `.mcp.json`.

## Settings you may want

Set these in `config.json` (data folder) or as environment variables in the client's server entry (`"env": { ... }`).

| Setting | Default | What it does |
|---|---|---|
| `OMNIWIN_PARALLEL_DESKTOP=1` | off | Lets several desktop acts on different windows run at the same time (desktop lanes). Off by default: desktop acts run one at a time. Browser tabs already run in parallel and need no setting. |
| `OMNIWIN_BROWSER_HEADLESS=1` | off | Starts OmniWin's own browser without a window. |
| `user_activity_cooldown_ms` | `0` | Milliseconds OmniWin pauses after your last mouse or keyboard input. `0` means it never pauses. |
| `rate_limit` | ignored | Still accepted so old files load; OmniWin never throttles or refuses calls for being too frequent. |
| `power_tools.enabled` (2.5) | `false` | Registers `shell`, `files`, `registry` and `process`, and lifts the engines' matching tool filters. Owner decision 2026-11-13: off. |
| `power_tools.allow_delete` (2.5) | `false` | Lets the power tools delete or overwrite. Needs `enabled`; on its own it is a config error. |
| `safety.allow_delete` (2026-11-19) | `false` | Turns off the no-delete rule for OmniWin's desktop, browser and typing tools: Delete buttons and menu items, delete keys in file views, drops on the Recycle Bin, delete commands typed or pasted into a terminal (`desktop_act`, `desktop_keys`, `selection_replace`, `text_insert`), and the terminal Enter check (no OCR is done). Those actions are allowed and labelled `risk=risky` in the result and the audit log, and `omniwin_status` shows `safety_gate=off`. Still refused: blocked processes (secure desktop, sign-in, OmniWin itself, `process_blocklist_extra`), password fields, and the stop key, which keeps working. Power tools keep their own `power_tools.allow_delete`. A server older than 2026-11-19 refuses a config.json that has a `safety` section (unknown key). |
| `engines.enabled` (2.5) | `true` | Offers `engine_call` for the engines found under `tools\` (Windows-MCP, agent-desktop). **Engine actions are not checked for deleting** and do not pause for your mouse or keyboard (see `docs/TOOLS.md`, "Engines"). `false` hides `engine_call`; `engines_list` stays. |
| `decision.provider` (2026-11-19) | `"none"` | The optional decision model that `omniwin_do` asks when the phrase table is unsure. `"openai"` or `"openai_compatible"` talks to any service with an OpenAI-style `/chat/completions` API (OpenAI, a gateway, Ollama, LM Studio, vLLM, or your own model such as Jev). |
| `decision.mode` | `"off"` | `"shadow"` asks the model and logs what it would pick, but never acts on it. `"act"` uses a confident answer. Start with `"shadow"`. |
| `decision.endpoint` | none | Base URL, e.g. `"https://api.example.com/v1"` or `"http://localhost:11434/v1"`. `/chat/completions` is added. Required for `openai`. |
| `decision.model` | none | The model name the service expects, e.g. `"jev-1"`. Required for `openai`. |
| `decision.api_key_env` | none | Name of an environment variable that holds the API key (safer). When it is set and not empty it wins over `api_key`. |
| `decision.api_key` | none | The API key pasted into config.json. **It is stored in plain text** in the data folder; prefer `api_key_env`. Never logged. Leave both empty for a local service with no key. |
| `decision.allow_remote` | `false` | Must be `true` for any endpoint that is not `localhost`/`127.0.0.1`, because the request wording and option names leave the PC. |
| `decision.max_latency_ms` / `decision.timeout_ms` | `400` / `1500` | An answer slower than `max_latency_ms` is never "confident"; the call is cancelled at `timeout_ms`. For an internet service use about `3000` / `5000`. |
| `decision.threshold` / `decision.margin` | `0.95` / `0.30` | How sure the model must be, and by how much its top pick must beat the second. |
| `router.laya.enabled` (2.5) | `false` | Stage B of `omniwin_do` (the Laya model). **Keep it `false`:** Laya failed its offline gate on 2026-11-14 (61% accuracy, 4.8% wrong actions even at 0.995 confidence, 827 ms median on this CPU). `omniwin_do` uses the phrase table only. |

Example entry with environment variables:

```json
"omniwin": { "command": "C:\\Users\\<you>\\Desktop\\OmniWin\\publish\\OmniWin.Server.exe", "args": [], "env": { "OMNIWIN_BROWSER_HEADLESS": "1" } }
```

## Check it works

Call `omniwin_status`. It lists the engines and safety parts and says whether the browser is attached (it shows `detached` until the first browser call, which is normal). Then try `windows_list`.

## Moving to another PC

Since 2026-11-16 `publish\` is a self-contained win-x64 build: the server carries its own .NET 10 runtime (10.0.14), so the new PC needs no .NET install (only the interim `publish\OmniWin.App.exe` still needs the .NET 10 Desktop Runtime). The step-by-step guide for the owner is `MOVING_TO_ANOTHER_PC.md` in the project folder. Keep `publishpp` inside `publish`, and keep `tools` in a folder above `publish`: the server looks upward from its own folder for it.

## The OmniWin app (from Handy's source)

`publish\app\omniwin-app.exe` (2026-11-16) is the planned app. It is built from a copy of Handy's source (MIT; `LICENSE-Handy.txt` and `NOTICE.md` are next to it) and renamed OmniWin. It is portable: its settings, speech models and history stay in `publish\app\Data`. It starts only when you open it, adds nothing to Windows start-up, never updates itself, and has no installer. It is unsigned, so Windows SmartScreen may warn the first time.

- **What it adds to Handy:** an OmniWin page with a command box (your words go to `omniwin_do`; Copy when it needs your AI; Right / Wrong after a learned task), your learned tasks, and three hotkeys: command box, voice command (the words fill the box, and nothing runs until Enter) and polish selected text. All three have **no key** until you set one.
- **What it keeps from Handy:** dictation with local speech models. A model is downloaded from Handy's own host, checked against Handy's catalog checksums, and only when you pick one. Post-processing and polish use the provider and prompt you choose in its settings. A key you paste there is stored in plain text in `publish\app\Data`.
- **How it reaches OmniWin:** it starts its own `OmniWin.Server.exe` and may call only `omniwin_do`, `omniwin_status`, `task_done`, `workflow_list`, `workflow_manage`, `selection_get`, `selection_replace`, `text_insert` and `polish_prompts`. OmniWin never clicks or types in the app's windows.
- **Every hotkey starts with no key**, Handy's dictation keys included; set the ones you want in Settings. Requests to AI services identify the app as OmniWin.
- **Not tried yet:** nobody has used its windows, microphone or a speech model.

## The OmniWin app (optional, interim; replaced by the app above)

`publish\OmniWin.App.exe` is a small tray app (2026-11-16). It is **not** the planned app built from Handy's source, which needs build tools only the owner can install. It starts only when you open it and adds nothing to Windows start-up. It starts its own `OmniWin.Server.exe` and only calls OmniWin's tools, so every safety rule applies, and OmniWin never clicks or types in the app's own windows.

- **Tray menu:** Command box (sends your words to `omniwin_do`, with Right / Wrong after a learned task ran), Polish selected text, Settings, Quit.
- **Hotkeys:** command box, polish, dictation and voice command. All are off until you tick them in Settings. Ctrl+Alt+Shift+K stays the stop key and cannot be assigned.
- **Polish:** sends the selected text to the chat address you set (a model on this PC, or one elsewhere only after you tick "allow remote"; the key is read from an environment variable you name), then puts the answer back with `selection_replace`.
- **Dictation and voice command:** record from the microphone, turn speech into text with whisper.cpp's `whisper-cli.exe` (you download it yourself and set its path in Settings), then type the words with `text_insert` (dictation) or put them in the command box to check and run with Enter (voice command; nothing spoken runs by itself). A Whisper model (75 MB to 3 GB) is downloaded only when you press its Download button.
- **Settings:** `config.json` is saved only after `OmniWin.Server.exe --validate-config` accepts it. The power tools are shown but cannot be switched on here.
- **Model downloads are checked:** each address is pinned to one commit of `huggingface.co/ggerganov/whisper.cpp`, and a file is kept only when its size and SHA-256 equal the publisher's values recorded in the app (the reviewer compared all eight with the publisher's own file list on 2026-11-16). A different or cut-off file is thrown away and nothing is installed.
- **Not tried yet:** nobody has seen its windows, used a hotkey, recorded from a microphone or run `whisper-cli.exe` with it, and no model has been downloaded.

## Safety you can rely on

- Nothing is blocked except deleting: deleting files, items or history, and typing delete commands, is refused.
- The stop key (Ctrl+Alt+Shift+K) stops OmniWin at once; press it again to resume.
- OmniWin does **not** pause while you use the mouse or keyboard (owner decision 2026-11-08), so it keeps working even when you are active. The catch: if you type at the same moment OmniWin types into a window, your keystrokes can mix with its. The element, focus and window checks still stop it from typing into the wrong window, and the stop key still works. To bring the pause back, set `"user_activity_cooldown_ms": 3000` (milliseconds to wait after your last input) in `config.json`.
- Acts check the element right before sending any input, and results say honestly whether anything was sent (`input_sent`).
- The audit log records every tool call; typed text and passwords are not stored.

See `docs/TOOLS.md` for every tool.
