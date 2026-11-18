# Proposal: WS2-js-dialogs (Q8)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Modal JavaScript dialogs block underlying page interaction with reason `modal_blocking` and require explicit dismissal or auto-dismiss policy.

## Problem
Contract §9.1 and §13.1 do not specify how JavaScript dialogs (`window.alert`, `window.confirm`, `window.prompt`, `beforeunload`) appear in observations or are handled during actions. A modal JS dialog completely suspends page JS execution and traps focus.

## Proposed Change
1. CDP `Page.javascriptDialogOpening` event is listened to. When open, the dialog state is recorded in the session.
2. If an action target is on a page currently blocked by an open JS dialog, `ActAsync` returns `blocked modal_blocking` with detail describing the open dialog.
3. In observation headers or lines, open dialogs are reported as modal dialog items with role `dialog`.
4. Add a safe policy to auto-dismiss non-essential alerts or provide explicit dismiss actions.

## Impact on Other Streams
- WS0 (Core / Formatter): Dialog state format in observations.
- WS2 (Browser): Implemented via CDP `Page.handleJavaScriptDialog`.
