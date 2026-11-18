# WS2 task: wire in the Risky label (Q6)

Copy everything below the line and send it to the AI.

---

**Owner decision (2026-11-08): approved. Wire in the Risky label now, using the main version of your proposal** (`docs/contract-proposals/WS2-risk-label-wiring.md`). This replaces the earlier "not now". You may make the small Core and Server edits it lists, and nothing else in those projects. This is the only task: still don't start M2.

## The rule that doesn't change

The label **only records**. It never blocks, asks, delays or changes an action. Nothing is blocked by policy except deleting. If any test or code path would refuse, pause or alter an action because it's Risky, that's a bug.

## What to change: the four steps from your proposal

1. **Core, `Results.cs`:** add `RiskClass? Risk = null` as the last optional parameter of `ActResult`. No existing call may need to change.
2. **Core, `ResultFormatter`:** print `risk=risky` on the first result line only when `Risk` is `Risky`. Otherwise the text stays exactly as it is today, so every desktop result is unchanged.
3. **Core, `SafetyContracts.cs`, and Server, `ToolCallAudit`:**
   - add `string? Risk = null` as the last optional field of `AuditEntry`;
   - make `ToolCallAudit` fill it from the `risk=` token on the first line.
4. **Browser:** set `Risk` on every `ActResult` that `NavigateAsync` and `ActAsync` return once the action has been classified, using `ClassifyNavRisk` and `ClassifyActRisk`. Include denied, blocked, stale and error results.

## Limits

- **Files:** in Core and Server, edit only the files named above, plus their tests.
- **Untouched:** no edits to `OmniWin.Safety` or the gate, `OmniWin.Desktop` source, or the delete policy.
- **Version:** keep the contract version at 2.3. Q6 was part of the 2.3 approvals, and this implements it.
- **Hashes:** the Desktop and Safety DLL hashes will change, because they're rebuilt against the new Core. Report the new hashes and say that no Desktop or Safety source changed.
- **Everything else as before:**
  - headless only;
  - local fixture pages only;
  - no real input;
  - `NavigationGuard` stays in every browser test;
  - never open `file:`, `javascript:`, `ms-settings:` or `search-ms:`;
  - for a non-web case, use `data:` only.

## Tests (failing first where possible)

- **Browser:**
  - `close_tab` returns `Risk = Risky`;
  - navigating to a `data:` URL returns `Risk = Risky`;
  - clicking the `data:` link on `controls.html` returns `Risk = Risky`;
  - pressing Enter in the password field on `controls.html` returns `Risk = Risky`;
  - a normal button click and a normal http navigation return `Risk` as `Normal` or null;
  - none of these is refused or changed: assert the same status and `input_sent` you'd get without the label.
- **Core:**
  - the first line contains `risk=risky` only for a Risky result;
  - every existing `ResultFormatter` test still passes unchanged.
- **Server:** `ToolCallAudit` writes `Risk = "risky"` for a line with `risk=risky`, and null for a line without it.
- **Whole solution:** build both Debug and Release with `-warnaserror`, then run the full suite in both. There must be 0 failures and no existing test removed, skipped or weakened.

## Docs

- **The proposal:** change Status to "Approved by the owner 2026-11-08 (main version), implemented".
- **`WS2.md`:** move Q6 from "not implemented" to done, with the test names.
- **`02_Architecture_and_Contracts.md`:** change the Q6 bullet from "approved, not implemented" to implemented. Name the new `ActResult.Risk` and `AuditEntry.Risk` fields, and the `risk=risky` token.
- **Audit note:** the browser tools aren't registered until M2, so no browser audit line exists yet. Say so in `WS2.md`. The Server test proves the plumbing.

## Reply format

Same headings as last time: Result, Changes, Tests, Live runs, Measurements, Rules check, Open items, Hashes. Also:

- In **Changes**, list every file you edited outside `OmniWin.Browser`, with its lines.
- Copy names, lines and numbers from the files after your last edit.
- List every failed run.

Then stop.
