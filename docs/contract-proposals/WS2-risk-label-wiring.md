# Proposal: WS2-risk-label-wiring (finishes Q6)

## Status
**Approved by the owner 2026-11-08 (main version), implemented.** The label only records; it never blocks, asks, delays or changes an action. Q6 (`WS2-browser-risk-triggers.md`) was approved on 2026-11-07. Implementation and tests are listed in `docs/status/WS2.md`, section "Q6 Risky label".

(Original wording, kept for the record: this proposal describes the smallest change that finishes Q6. Every part outside `OmniWin.Browser` is a Core or Server change.)

## What exists today
- `BrowserEngine.ClassifyNavRisk` and `BrowserEngine.ClassifyActRisk` (in `src/OmniWin.Browser/BrowserEngine.cs`) return `RiskClass.Risky` for: `close_tab`, navigation to a non-web scheme, `click`, `double_click` or an Enter/Space `press` on a link whose `href` is a non-web scheme, and Enter on a password or payment field.
- Only tests call them. The `ActionDescriptor` the engine builds has no risk field. `SafetyGate.Classify` returns only `Normal` or `Forbidden`. `GateResult.Risk` is always `Normal` or `Forbidden`.
- The audit log line is built by `ToolCallAudit` from the first line of the tool's text result. It has no risk field.

## Smallest change that would carry the label to the audit log
Four small edits, each owned by a different project. They are listed in the order data flows.

| # | Project | Edit | Owner of the file |
|---|---|---|---|
| 1 | `OmniWin.Core` (`Results.cs`) | Add `RiskClass? Risk = null` as a last optional parameter of `ActResult`, so no existing call changes. | Core / WS0 |
| 2 | `OmniWin.Core` (`ResultFormatter`) and `OmniWin.Server` (`ToolCallAudit`) | Print `risk=risky` on the first result line when `Risk` is `Risky`. `ToolCallAudit` already reads `status=`, `input_sent=`, `reason=`, `step=` from that line, so it reads one more token. | Core, Server |
| 3 | `OmniWin.Core` (`SafetyContracts.cs`) | Add `string? Risk = null` as a last optional field of `AuditEntry`, and fill it from the `risk=` token. | Core / WS0 |
| 4 | `OmniWin.Browser` | Set `Risk` on every `ActResult` it returns, using `ClassifyNavRisk` and `ClassifyActRisk`. | WS2 (this stream) |

Notes:
- No change to `SafetyGate` is needed for logging. The gate keeps returning `Normal` or `Forbidden`. The label is an engine-side annotation. It never blocks an action, which matches the owner's rule that only deleting is blocked.
- If the owner would rather have the gate own the label, the alternative is a `RiskClass? Hint { get; init; }` on `ActionDescriptor` (the same style as `ContainsDeleteCommand`), with `SafetyGate` returning `max(hint, own classification)` in `GateResult.Risk`. That touches `OmniWin.Safety`, and the audit log would still need steps 2 and 3.
- WS1 (desktop) would get the same field for free, because the change is in shared types.

## Tests that would prove it
- Browser: a `close_tab` result has `Risk = Risky`. A normal click has `Risk = Normal` or null.
- Server: `ToolCallAudit` writes `Risk` for a result line that has `risk=risky`.

## Decision
Approved 2026-11-08 and implemented as the four steps above.
