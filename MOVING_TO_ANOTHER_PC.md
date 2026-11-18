# Moving OmniWin to another PC

Written 2026-11-16. Simple steps; nothing here changes the PC you copy from.

## What OmniWin is made of

| Part | Where it is now | What it does |
|---|---|---|
| The server ("robot hands") | `C:\Users\<you>\Desktop\OmniWin\publish\OmniWin.Server.exe` | What your AI talks to. Sees and works the screen with every safety check. |
| The app ("buttons and microphone") | `C:\Users\<you>\Desktop\OmniWin\publish\app\omniwin-app.exe` | Dictation, the OmniWin command box, voice command, polish. Optional. |
| The memory | `%LOCALAPPDATA%\OmniWin` (paste that into File Explorer's address bar) | `config.json`, learned tasks (`workflows`, `recipes`), logs. |
| The app's own data | `publish\app\Data` (made on first start) | The app's settings, downloaded speech models, history. |
| Extra engines (optional) | `C:\Users\<you>\Desktop\OmniWin\tools` | Windows-MCP and agent-desktop, used through `engine_call`. |

## What the new PC needs

1. **Windows 10 or 11.**
2. **No .NET install for the server**: since 2026-11-16 the server carries its own .NET (the `publish` folder is about 200 MB because of it). Only the older interim app `publish\OmniWin.App.exe`, which you do not need, still wants the ".NET 10 Desktop Runtime".
3. **Microsoft Visual C++ Redistributable (x64)**, only if the app says a file such as `VCRUNTIME140.dll` is missing. Most PCs already have it.
4. **Microsoft Edge WebView2**: already part of Windows 11; on Windows 10 it usually comes with Edge.
5. **Nothing else.** Rust, CMake, the Vulkan SDK, Bun and the C++ Build Tools were only needed to *build* the app, not to run it.

## Steps

1. **Copy the `publish` folder** (about 200 MB, plus any downloaded speech models) to the new PC, for example to `C:\OmniWin\publish`. Keep `publish\app` inside it: the app finds the server by looking for a `publish` folder above itself.
2. **Keep what OmniWin learned (optional):** copy `%LOCALAPPDATA%\OmniWin` from the old PC to `%LOCALAPPDATA%\OmniWin` on the new one. Skip it to start fresh.
3. **Tell your AI where the server is.** In the AI app's MCP server settings (Claude, Antigravity, Codex …) add a server whose command is the new path, for example `C:\OmniWin\publish\OmniWin.Server.exe`. No arguments are needed.
4. **Check it:** ask your AI to call `omniwin_status`. The answer starts with `omniwin … contract=2.6`.
5. **Start the app (optional):** double-click `publish\app\omniwin-app.exe`. Windows may warn because it is not signed: "More info", then "Run anyway". On first start it asks you to pick a speech model to download (Parakeet or Whisper base are good starts on a PC without a graphics card).
6. **Set your hotkeys:** on the app's **OmniWin** page and its **General** page. Every hotkey starts empty. Never use Ctrl+Alt+Shift+K: that is OmniWin's stop key.

## Things that do not move cleanly

- **Extra engines (`tools`):** the server finds the `tools` folder only by looking in the folders **above itself** (for `C:\OmniWin\publish\OmniWin.Server.exe`, that is `C:\OmniWin\tools`). Their Python setup also remembers its folder, so a copy to a different path may not start. The safest move is to keep the old layout: copy both `publish` and `tools` into `C:\Users\<you>\Desktop\OmniWin\` on the new PC. Otherwise put `tools` next to `publish`, and if the engines then do not start, set them up again there (see `tools\README.md`). OmniWin works without them; `engine_call` just reports that no engine is installed. (Reviewer correction, 2026-11-16.)
- **Learned tasks depend on the screen.** A task learned in an app on the old PC replays only where that app looks the same. If it differs, the replay stops at that step and sends nothing more; your AI then finishes and the task is repaired.
- **Running under a different Windows user:** `%LOCALAPPDATA%` is per user; copy the memory into that user's folder.

## Quick check list

- [ ] `publish` copied, `publish\app` inside it
- [ ] AI app points at the new `OmniWin.Server.exe`
- [ ] `omniwin_status` answers
- [ ] (optional) learned tasks copied, app started, speech model picked, hotkeys set
