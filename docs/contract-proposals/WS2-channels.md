# Proposal: WS2-channels (Q13)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Standard channel definitions: `cdp_mouse`, `cdp_insert_text`, `keys`, `select`, `set_value`.

## Problem
In contract §6 (`Channels` class), several channel constants exist (`PhysicalClick`, `Invoke`, `Toggle`, `Select`, `SetValue`, `UnicodeTyping`, `Keys`, `CdpMouse`, `CdpInsertText`, `Api`). The mapping from `BrowserActRequest.Action` to the reported `Channel` needs to be formalized.

## Proposed Change
Formalize the following mapping for browser actions:
- `click`, `double_click`, `hover`: `Channels.CdpMouse`
- `type`: `Channels.CdpInsertText` (or `Channels.Keys` for key-event dispatch)
- `set_value`, `clear`: `Channels.SetValue`
- `press`: `Channels.Keys`
- `check`, `uncheck`: `Channels.Toggle`
- `select`: `Channels.Select`
- `focus`, `scroll_into_view`: `Channels.Api`

## Impact on Other Streams
- WS0 / WS4: Standardized expectation on `ActResult.Channel`.
- WS2: Implemented in `BrowserEngine.ActAsync`.
