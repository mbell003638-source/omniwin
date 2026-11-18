# Final WS1 review acceptance — 2026-11-07

All ten owner tasks and both deletion refusals passed in one uninterrupted Release server session, PID 31596, with `test_mode=false`, PerMonitorV2, armed stop/activity hooks, no-delete SafetyGate, and audit logging. The session was RDP, not the physical console. No source changed during this run.

Evidence: `artifacts/acceptance/final-guarded-2026-11-08T01-36-12-633Z/metadata.json`, `results.json`, `transcript.json`, `final-status.txt`, and the audit log. The UTC directory date is November 8; the local acceptance date is November 7. Metadata records `complete=true`, the single server PID, and assembly hashes. The server exited normally afterward.

| Task | Result | Actual verification |
|---|---|---|
| 1. Notepad: type and save to Desktop | Pass | 100 varied ASCII characters physically typed; GUI Save As path committed with Tab; file bytes exact. |
| 2. Launch installed app by Start-menu name | Pass | Observed and invoked the exact Notepad item in Windows Start; Notepad became foreground. |
| 3. Open Chrome | Pass | Invoked Google Chrome by name in Start; Chrome window became foreground. |
| 4. YouTube video search | Pass* | Initial address typing returned contradicted (readback mismatch due to Chrome history autocomplete); Enter was sent regardless, opening an unrequested video before recovering. Search field and button were then submitted and verified; results URL, title, and video links read back. See Task 4 note below. |
| 5. Maps route | Pass | Browser address field navigated to a directions request for Toronto City Hall and Royal Ontario Museum; resolved addresses and driving route read back. |
| 6. Explorer file move | Pass | Dedicated Downloads fixture selected, Ctrl+X, Desktop navigation, Ctrl+V; source absent and destination bytes exact. |
| 7. Type and confirm Unicode | Pass | Physical typing into real Chrome's address field; exact ASCII, accented, Greek, and emoji readback. This was not a Notepad emoji test. |
| 8. Physical menu click | Pass | Notepad File menu clicked through `physical_click`; fresh observation verified expansion. |
| 9. Select and replace a word | Pass | Selected `acceptance` with navigation/selection keys, typed `verified `, saved, and verified the complete file. |
| 10. Switch windows | Pass | Notepad → Explorer → Notepad; focus confirmed and foreground IDs checked after each switch. |

Fixture Delete invocation and Explorer Delete key both returned `denied`, `input_sent=false`, `policy_forbidden`. A separate Notepad supplementary-Unicode request returned `blocked`, `input_sent=false`, `unsupported_action`, demonstrating the explicit provider limitation before any typing.

## Task 4 reporting correction (R1)

During initial navigation at `transcript.json` 01:37:53 UTC, typing `https://www.youtube.com` into Chrome's address bar returned `status=contradicted` (`reason=readback_mismatch`, `channel=unicode_typing`) because Chrome autocompleted the value to a `/watch?v=…` video URL from history (`https://www.youtube.com/watch?v=xCHSyht5eac`). The test client sent Enter anyway at 01:37:54 without observing or correcting the value, opening the unrequested history video before recovering. The engine reported accurately; the client behavior was flawed. The acceptance record has been corrected to reflect this step truthfully, and `ToolDocs.RetryRules` was updated with the client rule: when typing into an address or search field returns contradicted, observe and correct the value before sending Enter. Never submit a value that did not read back.

## Notepad limitation

The installed RichEdit provider corrupted supplementary Unicode during physical packet delivery, including after native caret progress was observed. Earlier full-focus checking also corrupted a fresh-window 100-character sample. The corrected path acknowledges each supported scalar's native caret progress before another is sent. It rejects supplementary Unicode and newline packet text before input; callers can choose explicit `set_value`, or separate Enter key actions. No clipboard fallback or automatic replay was added.

The final 100-character Notepad ASCII action was confirmed in 1398.7 ms: prechecks 1090.9 ms, native dispatch 112.3 ms, verification 195.5 ms. This is one functional sample, not a p95 estimate or speed acceptance. Earlier failed attempts are preserved under their own incomplete metadata; they are not this acceptance run.

## Automated and live validation

- Debug and Release solution builds with warnings as errors: zero warnings/errors.
- Each normal suite: 470 passed, zero failed, five opt-in scenarios skipped; 475 discovered.
- Final Release functional fixture: passed separately (`artifacts/benchmarks/review-final-live-fixture.txt`).
- Final 200-cycle input benchmark: passed with exact text/counters (`review-final-guarded-input-100.json`).
- RDP pointer controls and read-only observation/fallback diagnostics: recorded in the [review follow-up](../benchmarks/WS1_Review_Followup_2026-11-07.md).

Final Release Desktop SHA-256: `80C9E2C7FEEFCBD8B1CB3E507596CB1DB03A0588B370AF3D4029959322304D80`. It matches the final benchmark and acceptance metadata. Final harness SHA-256: `89577F39212483B037562A9A959FC9FB0BFE70DB607BBFFB1419830B9E8B85BC`.

## Files retained

- `C:\Users\<you>\Desktop\OmniWin_Final_Acceptance_final-guarded-2026-11-08T01-36-12-633Z.txt`
- `C:\Users\<you>\Desktop\OmniWin_Final_Move_final-guarded-2026-11-08T01-36-12-633Z.txt`

The 500-file observation fixture and diagnostic scratch data remain inside ignored `artifacts/`. Only dedicated fixtures were created or moved. Prior acceptance files and scratch tabs were preserved.

Strict speed acceptance remains open: input checks/dispatch and cold observations still exceed several budgets. The physical-console comparison is unavailable in the owner's current RDP setup. No budget was changed.
