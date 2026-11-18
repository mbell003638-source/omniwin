# Proposal: WS2-tab-opened (Q7)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Evaluate `window_opened` and `window_closed` against browser tabs when executing in browser context.

## Problem
In contract §6 and §11, `WindowOpened` and `WindowClosed` expectation types are defined, but there are no explicit expectation types for tabs (`tab_opened`, `tab_closed`). Actions that open popups or new tabs (e.g. clicking target="_blank" links) need a way to be verified via expectations.

## Proposed Change
1. In the browser engine, when `WindowOpened(TitleContains, Process)` is evaluated, match against browser tabs (matching tab title containing `TitleContains`).
2. When `WindowClosed(Window)` is evaluated, if the identifier starts with `t` (e.g. `t2`), check if that tab has closed.
3. Add alias support or clarify that in browser context, `window_opened` checks for a newly created tab matching the title criteria.

## Impact on Other Streams
- WS0 (Core / Expectations): No immediate type changes needed if semantic aliasing is permitted; or add `TabOpened` / `TabClosed` in contract v2.3.
- WS2 (Browser): Implemented in `BrowserEngine.WaitForAsync`.
