# NOTICE: OmniWin app (a modified copy of Handy)

This folder is a modified copy of **Handy** by CJ Pais (https://github.com/cjpais/Handy), commit
`aca8985190c3ad7f361d8a80cb30f179513ed690` (2026-11-14, version 0.10.1), used under the MIT License. Handy's licence text is kept
unchanged in `LICENSE`. Handy's name, logo and brand assets are not covered by that licence, so this copy does not use them.

Changes made for OmniWin (2026-11-16):
- Renamed to **OmniWin** (`productName`, identifier `com.omniwin.app`, binary `omniwin-app`); the app icons, tray icons and the
  in-app logo were replaced with a plain OmniWin mark (`scripts/make-omniwin-icon.py`); "Handy" in the language files was
  replaced by "OmniWin".
- Self-update removed: no update feed, no update key, update checks always off.
- Added `src-tauri/src/omniwin.rs`: the app starts its own `OmniWin.Server.exe` and talks to it over MCP; it may call only a fixed
  list of OmniWin tools.
- Added an **OmniWin** page (command box with Right / Wrong, learned tasks, server status) and three hotkeys that have no key until
  the owner sets one: command box, voice command (the spoken words go into the command box, nothing runs until Enter) and polish
  selection (rewrites the selected text with the selected post-processing prompt, through OmniWin's selection tools).
- A hotkey with no key is not registered.
