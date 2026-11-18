# Proposal: WS1-parallel-lanes (desktop acts that run at the same time)

## Status
The pattern-act lane is **implemented** (2026-11-08, owner request: handle several tasks at once). The window-message lane at the end is **a design only, not built**, and needs an owner decision.

## The constraint
Windows has one hardware cursor and one keyboard focus per interactive session. Real mouse clicks and typing therefore cannot happen in parallel, and OmniWin does not pretend they can. What can run in parallel is everything that does not use that shared input.

## What the engine did before
- Every UI Automation call runs on **one** dedicated MTA thread (`UiaWorker`). All the state that makes refs and generations safe (`UiaDesktop`, `DesktopIndex`, the per-window snapshots and histories) lives on that thread and is not locked, because only that thread touches it.
- A single `SemaphoreSlim` (`_input`) was held for the **whole** act: precheck, safety gate, dispatch, typing loop **and the verification wait** (up to 300 ms, or the `expect` timeout, up to 30 s). So a slow verification on one window held up every other act, including pattern acts on other windows.
- Reads (`windows_list`, `desktop_observe`, `wait_for`, `describe`) never took `_input`; they only queued on the worker.

## What changed
`ActLanes` (new, `src/OmniWin.Desktop/ActLanes.cs`) replaces the single semaphore with two kinds of lock:

| Lane | Used by | Rule |
|---|---|---|
| **Physical lane** (one lock) | click, double_click, right_click, type, focus, desktop_keys, desktop_scroll, `BringToForeground` (anything with `DesktopCommand.UsesSharedInput`) | strictly one at a time, as before, and it keeps every existing guard (focus lock, foreground and occlusion checks, hit tests, per-batch boundary checks, the no-delete gate) |
| **Window lock** (one per window id) | every act, for the window it works on | two acts on the same window never overlap |

- A **pattern act** (`invoke`, `set_value`, `clear`, `toggle`, `select`, `expand`, `collapse`, `scroll_into_view`) asks the index which window its ref belongs to (`UiaDesktop.WindowIdOfRef`, one cheap worker call) and takes **only** that window's lock. It never waits for the physical lane.
- A **physical act** takes the physical lane, runs its first (read-only) `Prepare`, then takes the lock of the window it found and keeps both until it ends. So a pattern act and a physical act on the same window never overlap, in either order.
- **Lock order** is always physical lane, then window lock. A pattern act never waits for the physical lane, so there is no cycle and no deadlock. Every wait takes the act's own token (linked to the stop key), so the stop key cancels every waiting act in every lane at once; acts already running stop at their next boundary check, as before.
- Refs and generations stay unique and monotonic: they are still allocated only on the worker thread. An act is still re-verified against the live element in `Prepare` and again at dispatch, unchanged.
- The user-activity pause stays off (owner decision); nothing here brings it back.

## What really runs in parallel now, and what does not
- **Overlaps:** the safety-gate wait, the verification wait (`expect`, event waits, up to 30 s), the typing acknowledgement waits, and the idle gaps between UI Automation calls. Pattern acts and reads on different windows, and a physical act on a third window, make progress together.
- **Does not overlap:** the UI Automation calls themselves. They still run one after another on the single worker. If an app hangs, a call to it can occupy the worker for up to the UI Automation transaction timeout (1 s by default), and other windows wait behind it (head-of-line blocking).
- **Why no pool of workers:** `UiaDesktop`, `DesktopIndex` and the snapshot histories are confined to one thread. Several workers sharing one `UiaDesktop` would need locks around the whole index and every snapshot, a large change to code that is accepted and tested. Several workers each with their own `UiaDesktop` would break the global rules that refs (`eN`) and window ids (`wN`) are unique, monotonic and never reused across the whole session. A safe pool needs a shared, thread-safe ref/window allocator and a per-window affinity map first. **Not done; recorded as a limit.**

## Risks kept and how they are handled
- A pattern act can make the target app change focus as part of its own response (the tool description already says so). A physical act running at the same moment on another window checks its foreground and focus before each input batch, so it stops with `focus_changed` or `input_interrupted` and an honest `input_sent`, instead of typing into the wrong window.
- Window locks are never removed (one small semaphore per window id seen in the session). Window ids are never reused, so this stays small.
- A pattern act whose ref is unknown uses one shared gate (`?`) and then fails fast with `stale` or `not_found`.

## Tests (fakes only)
`tests/OmniWin.Desktop.Tests/ActLanesTests.cs`: same-window serialization; different windows overlap; physical acts one at a time; a pattern act never waits for the physical lane; physical and pattern on the same window do not overlap in either order; the stop token cancels every waiting lane and leaks nothing; a cancelled window wait releases the physical lane; a lease holds one window and dispose is idempotent; 100 parallel mixed acts finish without deadlock and keep every exclusion; refs are never reused and each names its own window.

## Design only: the window-message lane (not built)

**Idea.** For classic Win32 controls, send input straight to a window handle with `PostMessage`/`SendMessage` (`WM_LBUTTONDOWN`/`UP` at a client point, `WM_CHAR`, `WM_KEYDOWN`/`UP`, `WM_SETTEXT`, `BM_CLICK`). This does not move the real cursor and does not need the foreground, so it can run in parallel with a physical act and with other message acts. Open-source "multi-cursor" desktop agents work this way (or with accessibility actions).

**Where it works.** Standard Win32 controls (Button, Edit, ListBox, ComboBox), classic WinForms, many older native apps.

**Where it does not.**
- Modern UWP/WinUI/XAML apps, Chromium and Electron windows, and games ignore or mishandle posted mouse and key messages (they read raw input or use their own input stack).
- No hit test: a posted click lands on whatever child window owns that point at the moment, and the app's own z-order and occlusion are not consulted.
- Apps can tell the message is synthetic; some ignore it (password boxes, UAC, elevated windows are blocked by UIPI).
- Key state is not tracked (shift, ctrl), so chords and IME input are unreliable.

**Checks it would need before it is allowed to run.**
1. Eligibility: the target control must be a standard Win32 class from an allow-list (`Button`, `Edit`, `ComboBox`, `ListBox`, `SysListView32`, `SysTreeView32`, `msctls_*`) in a non-elevated process; anything else falls back to the physical lane or to `blocked unsupported_action`.
2. Re-verification right before posting: the control still exists (`IsWindow`), has the expected class and text, is enabled and visible, and the click point is inside its client rectangle; use `ChildWindowFromPointEx` to confirm that child is the target.
3. The same safety gate as any act (descriptor with the control's name and class), so the no-delete rule applies; the stop key and the per-act cancellation check before each posted message; `input_sent=true` as soon as the first message is posted.
4. A verification step by readback (`WM_GETTEXT`, `BM_GETCHECK`, UIA value) so a lost message becomes `unverified` or `contradicted`, never `confirmed`.
5. A window lock like any other act, so messages to one window never interleave with another act on it.
6. Messages are `PostMessage` only (never `SendMessage` to a hung window); a timeout means `error`, not retry.

**Recommendation.** Build it only for the allow-listed classic controls and only after the pattern lane has run in the live session. It adds little for modern apps, where the pattern lane and the browser lane already cover background work.

## Impact on other streams
- WS0: contract notes for 2.5 (below). No Safety change.
- Server: no registration change; `DesktopEngine`'s constructor and public surface are unchanged.

### Contract notes (for "Changes in 2.5")
- §5 process model: desktop acts that use only UI Automation patterns run concurrently across windows; real mouse/keyboard acts stay serialized; same-window acts are serialized; UI Automation calls still share one worker thread.
- §13.3: the stop key cancels waiting and running acts in every lane.
