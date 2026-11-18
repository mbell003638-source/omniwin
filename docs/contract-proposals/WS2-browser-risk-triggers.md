# Proposal: WS2-browser-risk-triggers (Q6)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Subject to Rule 7 limit: Marking an action Risky labels it in the descriptor and audit log, but does not block it. Policy has no confirmation prompts and blocks only deletion. In particular, close_tab is allowed.

## Problem
Contract §13.1 identifies clicks submitting password or payment forms, file inputs, and non-http(s) navigation as Risky actions. However:
1. Pressing Enter inside a password or credit card form field also submits the form.
2. Clicking a link whose `href` is a `javascript:`, `file:`, `data:`, or browser-internal scheme can bypass URL scheme checks.
3. `browser_nav close_tab` is currently listed as Normal in §8, whereas `ctrl+w` keyboard shortcut is classified as Risky in §13.1.

## Proposed Change
1. Classify `press enter` when focused in a password or payment form as `Risky`.
2. Classify `click` on links pointing to non-http(s) schemes (`javascript:`, `file:`, `data:`, `chrome:`, `edge:`) as `Risky`.
3. Classify `browser_nav close_tab` as `Risky` (matching `ctrl+w`).

## Impact on Other Streams
- WS0 (Safety): Include in `SafetyGate` classification.
- WS2 (Browser): Enforce during `ActionDescriptor` generation before `AuthorizeAsync`.
