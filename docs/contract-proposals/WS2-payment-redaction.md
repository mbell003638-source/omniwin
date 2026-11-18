# Proposal: WS2-payment-redaction (Q12)

## Status
Approved by the owner 2026-11-07 ("yes to all"). Redact `value=<redacted>` for all credit card and payment fields.

## Problem
Contract §9.1 explicitly redacts password fields (`value=<redacted>`), but payment information such as credit card numbers, CVVs, and expiration dates (`autocomplete="cc-*"` or `name`/`id` matching credit card patterns) should also never be exposed in model observations, logs, or results.

## Proposed Change
1. Detect inputs where `autocomplete` starts with `cc-` (e.g. `cc-number`, `cc-csc`, `cc-exp`), or input types/names specifically designated for payment cards.
2. In observations and diffs, always emit `value=<redacted>` for these elements.
3. In `ActionDescriptor.ValuePreview` and any audit log entries, never record the actual text typed into these fields.

## Impact on Other Streams
- WS0 (Safety / Formatter): Preserves privacy and PCI-DSS compliance in model-facing text.
- WS2 (Browser): Implemented in `PageScript.js` and `BrowserEngine`.
