# Proposal: WS2-browser-mode (Q1)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Refuse non-dedicated modes and external attachments.

## Problem
Contract §14 specifies `browser.mode: "dedicated"`. The system needs an explicit invariant governing what happens if configuration attempts to supply another mode, if `profile_dir` resolves to a default browser user-data directory (e.g. `%LOCALAPPDATA%\Microsoft\Edge\User Data` or `%LOCALAPPDATA%\Google\Chrome\User Data`), or if an external browser process already listening on a debugging port is present.

## Proposed Change
1. Enforce that `OmniWinConfig.Browser.Mode` must be `"dedicated"`. Any other value fails validation at startup.
2. The browser launcher strictly checks resolved `profile_dir` against known default browser profile paths (Edge, Chrome, Brave) and refuses to launch if it points to or inside a default profile directory.
3. The launcher always launches a fresh browser process with dynamic port (`--remote-debugging-port=0`) and refuses to attach to any pre-existing or externally launched browser instance.
4. If the launched browser process exits or connection is lost, return `error browser_not_connected`.

## Impact on Other Streams
- WS0 (Safety/Config): Validated in `OmniWinConfig.Validate()` and `OmniWinPaths`.
- WS2 (Browser): Implemented in `BrowserLauncher`.
