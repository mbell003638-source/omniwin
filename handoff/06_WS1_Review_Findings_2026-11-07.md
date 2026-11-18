# WS1 review findings: 2026-11-07

From the reviewer to the WS1 builder. The reviewer checked the working tree after the acceptance and benchmark run. Apply the items below in priority order. Owner decision D1 was subsequently approved as recorded below; D2 remains pending.

## What the reviewer verified (no action needed)

Builder follow-up: D1 was approved and implemented with native identity checks. F3 and F5 are fixed; F4 and the RDP portion of F2 were measured. Final build: 470 automated passes, zero failures, five opt-in skips in each configuration; all ten tasks and both deletion refusals passed in one server session. F1 Notepad and cold-read speed gates remain open, and F2 console testing/D2 remain pending. A newly reproduced Notepad packet bug required scalar acknowledgement and explicit pre-input refusal of unsupported supplementary Unicode/newline text. See `OmniWin/docs/status/WS1_Final_Review_Acceptance_2026-11-07.md` and `OmniWin/docs/benchmarks/WS1_Review_Followup_2026-11-07.md`; the reviewer findings below remain the historical review text.

- **All 10 owner tasks and both delete refusals pass.** The reviewer checked `results.json`, `transcript.json` and the files on disk:
  - The Notepad file contains the exact text.
  - The move source is gone from Downloads, and the Desktop copy is exact.
  - The refusals returned `denied`, `input_sent=false`, `policy_forbidden`.
- **The four fixes keep every guard.** Those are shell discovery, Save-dialog controls, the Notepad Unicode batch and window activation. The stop key, activity cooldown, the delete policy and the per-batch focus and boundary checks are unchanged. Hooks can be disabled only through the internal test constructor.
- **Release build:** with `-warnaserror` it has 0 warnings and 0 errors. Tests: 406 passed, 3 opt-in skipped.
- **The final benchmark JSON matches the report.** It has 200 samples per operation and `Failure=null`.

## Owner decisions pending

| ID | Question | Status |
|---|---|---|
| D1 | For a control with its own window handle (Win32 Edit, Notepad `RichEditD2DPT`), may the check *between* typing batches use only the native keyboard-focus handle? The full UIA focus check stays before the first batch and on every batch for windowless controls (WinUI or Chromium content). | **Approved by the owner: "Yes: faster checks only for verified native editors".** Exact native focus-HWND match required; full check before the first batch and for browser/windowless/unverified controls. |
| D2 | Keep the 1 ms p95 click-dispatch budget, or revisit it after F2's console-session result? | Waiting. Run F2 first. |

## Findings

### F1: typing focus checks dominate typing time (speed, high, needs D1)

- **Where:** `src/OmniWin.Desktop/DesktopActions.cs:703-714` (`RequireKeyboardFocus`), called once per batch from the typing loop in `Dispatch`.
- **Evidence:**
  - In `artifacts/benchmarks/ws1-input-final-apphost-release-100.json`, 100-character typing checks take 120.3 ms at p95. The 7 `RequireKeyboardFocus` calls account for 108.6 ms of that at p95.
  - Before a key press, the same check takes 1.4 ms at p95.
  - In the acceptance transcript, 74 characters into Notepad took 1504.8 ms in total, 1301.1 ms of it in prechecks. That's one check per character, because of the 1-scalar batch limit for `RichEditD2DPT`.
- **Likely cause:** `_uia.GetFocusedElement()` is a cross-process call. Right after a batch, it waits while the target's UI thread is still processing the characters it just received. Confirm this by timing the first call separately from the later ones.
- **Direction, only if D1 is yes:**
  - Between batches, when `live.NativeHandle != 0` and the native focus handle equals that handle, rely on the native checks already on lines 709-710 and skip the UIA call.
  - Keep the full check before the first batch.
  - Keep it on every batch whenever `live.NativeHandle == 0`.
- **Done when:**
  - The benchmark shows lower typing checks at p95 with all 200 values read back exactly.
  - Notepad at 100 characters is measured and reported.
  - Report whether the time moved into `verify` instead of disappearing. The total time is what users feel.
  - Tests cover the following cases: focus moved to a sibling HWND between batches; focus moved to another window; a windowless control still gets the UIA check.

### F2: click dispatch about 31 ms at p95 (speed, high, experiment first)

- **Where:** inside `SendInput` (`NativeInput.cs`, `Send`). The cause isn't OmniWin code. With the hooks off it's still about 31 ms, and with no focus change it's 0.5 ms.
- **Untested factor:** every run was in RDP session 2.
- **Experiments, in order:**
  1. Repeat the pointer benchmark with the owner signed in **at the console**, not over RDP. The owner must be at the PC and leave the mouse and keyboard alone.
  2. Still over RDP, compare the click that changes focus from a text control with one from a non-text control, such as a second button. This separates the cost of switching away from a text-input context from focus changes in general.
- **Done when:** both results are recorded in the validation report. Don't change click code for this until a cause is shown. D2 depends on experiment 1.

### F3: frame raise counted as input when nothing changed (correctness, low)

- **Where:** `src/OmniWin.Desktop/NativeDesktop.cs:218` and `:262` (`RaiseFrame` in `TryForeground`).
- **Problem:**
  - When the foreground window is already a child of the target, as with Notepad's WinUI editor, every activation calls `SetWindowPos` and sets `windowInput=true`.
  - `Activate` then records input sent.
  - A later refusal is then reported as `error input_interrupted` instead of `denied` or `blocked`, even though nothing visible changed.
- **Done when:** a frame raise counts as input only if the z-order actually changed, for example when the target wasn't already the top frame. Add a test showing a refusal after a no-op raise is reported as `denied` or `blocked` with `input_sent=false`.

### F4: observation walks child controls one call at a time (speed risk, medium, measure first)

- **Where:** `src/OmniWin.Desktop/UiaDesktop.cs:483` (`ReadTree`). The new `GetFirstChild`/`GetNextSibling` walk replaced one `FindAllBuildCache` per parent.
- **Risk:** the walk makes one cross-process call per child. A cold observe of a large window could exceed the 100 ms p95 budget. Not measured yet.
- **Measure:** cold and warm `desktop_observe` on:
  - an Explorer folder with 500 or more files in Details view;
  - a long Chrome page, such as YouTube search results;
  - the Save As dialog.

  This is read-only and sends no input.
- **Done when:** the numbers are in the validation report. If the cold observe is over budget, keep the Save-dialog fix but use the batched call wherever the raw children are already control elements. Fall back to the walker only through non-control intermediates. Then show that the Save-dialog test still passes.

### F5: full desktop UIA scan on every refresh when events are unavailable (speed, low)

- **Where:** `src/OmniWin.Desktop/UiaDesktop.cs:104`. `!_events.Available` forces `GetRootElement().FindAllBuildCache(...)` on every refresh. The cache TTL is 25 ms in that mode.
- **Done when:** the fallback cost is measured with events disabled. If `windows_list` misses 5 ms at p95, limit how often the UIA desktop rescan can run in that mode. Native enumeration stays every time.

### F6: re-run all 10 tasks on the final build (acceptance, medium)

- **Why:** the 10-task transcript spans several fixes and server restarts. Only window switching, focus and the File-menu click were rechecked on the final `OmniWin.Server.exe`.
- **Done when:** one uninterrupted run of all 10 tasks plus both delete refusals passes on the final build, after F1 and F3 if they land. Record it in `WS1_Acceptance_*.md`. This needs the owner's go-ahead and an idle desktop, because it sends real input.

## Re-review after the final run (reviewer)

**Verdict: WS1 functional acceptance is accepted. Speed acceptance stays open, as documented.**

The reviewer independently confirmed the following:
- Debug and Release build with `-warnaserror` with 0 warnings and 0 errors, and each runs 470 passed with 5 skipped.
- The Release Desktop DLL hash matches `80C9E2C7…4D80`.
- The final run used one server PID, 31596, and covered all 10 tasks plus both refusals with `input_sent=false`.
- The Desktop files are exact and the Downloads source is gone.
- The benchmark p95 values match the JSON.
- The D1 shortcut is limited to verified native editors. Command targets and browsers are excluded. The first batch keeps the full check, and the RichEdit continuation rechecks the stop and boundary on every resume and is discarded, never resumed, on cleanup.

Two follow-ups:

- **R1: an unreported contradicted step in the final run (reporting, medium).**
  - **What happened:** at `transcript.json` 01:37:53, typing `https://www.youtube.com` into Chrome's address bar returned `contradicted`. Chrome had autocompleted the value to a `/watch?v=…` URL from history. Enter was pressed anyway at 01:37:54, which opened an unrequested video page.
  - **The engine behaved correctly.** The client didn't stop.
  - **What to change:**
    - Record this in `WS1_Final_Review_Acceptance_2026-11-07.md`. Task 4's row currently mentions only page readiness.
    - Add a client rule to the tool docs or RetryRules: after `contradicted` typing into an address or search field, observe and correct before submitting with Enter.
- **R2: remaining speed gaps, no action until the owner decides.** These are p95 values against their budgets.

  | Measurement | p95 | Budget |
  |---|---:|---:|
  | Click checks | 12.3 ms | 10 ms |
  | Click dispatch | 31.2 ms | 1 ms |
  | 100-character typing checks | 14.2 ms | 10 ms |
  | 100-character typing dispatch | 60.4 ms | 20 ms |
  | Cold observe | 168–247 ms | 100 ms |

  Notepad took 1.4 s for 100 characters. Typing dispatch is about 0.3 ms per key event inside `SendInput`, the same pattern as clicks. D2 and the console comparison need the owner at the PC.

## Rules (unchanged)

- Don't disable or weaken any guard to meet a budget: stop key, activity cooldown, focus and identity checks, occlusion checks, delete policy. D1 is the only proposed relaxation, and it needs the owner's yes.
- Don't change the delete policy or terminal submission guard as part of this work.
- Budgets don't change without the owner's decision.
- After changes, report:
  - the Release build with `-warnaserror`;
  - the full test count;
  - the 200-cycle benchmark, using the command in `docs/benchmarks/WS1_Final_Validation_2026-11-07.md`;
  - updated assembly hashes.

## Follow-up resolution: 2026-11-07 (T1 & T2)

- **R1 resolved:** Task 4's acceptance record in `OmniWin/docs/status/WS1_Final_Review_Acceptance_2026-11-07.md` was updated to truthfully state that the initial address typing of `https://www.youtube.com` into Chrome returned `contradicted` (readback mismatch due to history autocomplete) and Enter was sent regardless before recovering. `ToolDocs.RetryRules` was updated with the client rule: when typing into an address, search or submit-on-Enter field returns contradicted, observe and correct the value before sending Enter; never submit a value that did not read back.
- **R2 status:** Strict speed items remain open (click dispatch ~31 ms p95, typing dispatch ~60 ms p95, cold observation ~168–247 ms p95). D2 remains pending physical console testing (T3).
- **Notepad limitation:** RichEdit scalar caret progress acknowledgement enforced; supplementary Unicode and newline packets rejected before input.
- **Test suite:** 470 passed, 0 failed, 5 opt-in skipped in both Debug and Release builds (0 warnings, 0 errors with `-warnaserror`).
- **Updated assembly hashes:**
  - `OmniWin.Desktop.dll`: `EB52842A0B9035BFF5FA10F70E7B0800E440C25F28A5D8D350B1AE9D87EC889A` (historical acceptance hash: `80C9E2C7FEEFCBD8B1CB3E507596CB1DB03A0588B370AF3D4029959322304D80`)
  - `OmniWin.Core.dll`: `65EE6BBCA0A8776B18BC48454E5DAD2E80986A28C960B22DDC7E0A2D821DC746`
