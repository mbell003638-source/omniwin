# WS2 M1 fixes: step-by-step guide

Copy everything below the line and send it to the AI.

---

You're fixing the WS2 M1 problems the reviewer found. This guide is the order of work. The details of each problem, with file and line, are in `OmniWin_Handoff/08_WS2_M1_Review_Findings.md`. Keep both open.

## Rules for using this guide

1. **Do the steps in order.** Don't start a step until the previous step's checkpoint passes.
2. **Write the failing test first.** For every safety step, first add a test that shows the problem, run it, and confirm it fails. Then fix the code and confirm the test passes. Record both runs.
3. **Run the checkpoint after every step:**
   - Release build with `-warnaserror`;
   - the full test suite;
   - 0 failures, and no existing test removed, skipped or weakened.
4. **Log as you go.** Add one line per step to a "Fix log" section in `docs/status/WS2.md`: the step, what changed, the test name, pass or fail.
5. **Stop and ask the owner if:**
   - a fix needs changes outside `src/OmniWin.Browser`, `tests/OmniWin.Browser.Tests` or `testapps/web-fixtures`;
   - a step can't be done as written;
   - a test can only pass by weakening a check.
6. **Stay inside these limits the whole time:**
   - don't edit `src/OmniWin.Desktop`, `src/OmniWin.Safety` or the delete policy;
   - don't open real websites;
   - run browsers headless only;
   - no real mouse or keyboard input;
   - no system or security setting changes;
   - don't touch the owner's files or real browser profiles;
   - don't start M2.
7. **Owner decisions, 2026-11-07: the owner answered "yes to all"** for the 13 open questions: Q1, Q3, Q6, Q7, Q8, Q9, Q10, Q11, Q12, Q13, Q14, Q15 and Q16. Your recommendation in each proposal is approved. Q2, Q4 and Q5 were already settled in contract 2.1. **One limit:**
   - The owner's policy (`04_Status_Handoff.md` §5) has no confirmation prompts and blocks only deletion. So marking an action Risky (Q6) labels it in the descriptor and the audit log, but **doesn't block it**.
   - In particular, `close_tab` is allowed, because the owner's policy allows closing windows.
   - Whether non-web URL schemes are blocked is a separate owner decision. See Step 5.

---

## Step 0: record the baseline

1. Read `00_START_HERE_New_AI.md`, all of section 5, and `08_WS2_M1_Review_Findings.md`, all of it.
2. Run the Debug and Release builds and tests. Write the exact counts in the Fix log. Expected: 489 total, 484 passed, 5 skipped.
3. Record the SHA-256 of `OmniWin.Browser.dll`, `OmniWin.Core.dll`, `OmniWin.Safety.dll` and `OmniWin.Desktop.dll` (Release).

**Checkpoint:** the baseline is written in the Fix log.

## Step 1: S1, stop pages from faking what OmniWin sees

1. **Failing test first.** Add a fixture page, `testapps/web-fixtures/spoof.html`, that does all of these before OmniWin's script runs:
   - defines `window.__omniwin` with a fake `scan()` that labels a "Delete account" button as "Save";
   - overrides `Element.prototype.getBoundingClientRect` to return wrong coordinates;
   - overrides `document.elementFromPoint` to always return the button;
   - overrides `Element.prototype.getAttribute`.

   Add a test that observes the page and acts on the button. Confirm it currently fails: OmniWin sees "Save", and the act isn't denied.
2. **Fix:**
   - Run the indexer and every in-page action (`scan`, `focus`, `set_value`, `clear`, `check`, `uncheck`, `select`, `scroll_into_view`) in an **isolated world**: CDP `Page.createIsolatedWorld` per frame, and pass that context's id to `Runtime.evaluate` or `Runtime.callFunctionOn`.
   - Recreate the world after every navigation and new document. Contexts are destroyed on navigation, so track them with `Runtime.executionContextCreated` and `Runtime.executionContextDestroyed`.
   - Remove every dependency on `window.__omniwin` and on DOM expandos in the page world.
   - If the isolated context for a frame is missing, refuse to act: `blocked`, `input_sent=false`. Never fall back to the page world.
3. **Prove it:**
   - The spoof test passes: the real name "Delete account" is seen, and the act returns `denied` with `input_sent=false` and an empty page event log.
   - The existing delete test still passes.
   - Add a test that navigates to a second page and acts there, to show the world is recreated.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 2: S2, re-check the target live before input

1. **Failing test first.** Add a fixture page, `testapps/web-fixtures/moving.html`, with a harmless button. Shortly after the observation, a Delete button appears exactly where the harmless button was, and the harmless button moves. Add a test that observes, waits for the swap, then clicks the old ref. Confirm it currently fails: the click reaches the Delete button.
2. **Fix.** In the act pipeline, immediately before input and after the gate:
   - Re-read the target by ref in the isolated world. Read its identity, name, role, enabled state, live bounds, and a hit test at the point you will click.
   - Compare those with what was observed. If identity, name or role changed, or the element is missing, return `stale`, `input_sent=false`.
   - Build the gate's descriptor from the fresh data, not the cache. If the gate can wait (for example, a confirmation), re-read again after it.
   - Click only at a point from the fresh bounds where the hit test returns exactly this element. Otherwise return `blocked` (`occluded_same_window`), `input_sent=false`.
   - Apply the same fresh check to `type`, `set_value`, `clear`, `check`, `uncheck`, `select`, `press` and `hover`.
3. **Prove it:**
   - The moving test passes: the Delete button is never clicked, and the result is `stale` or `blocked` with `input_sent=false`.
   - A test where nothing changed still clicks and confirms.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 3: S3, make the generation check real

1. **Failing test first.** Observe at generation N, change the element's name on the page, then act with generation N. Confirm the act currently proceeds.
2. **Fix:** remove the self-comparison at `BrowserEngine.cs:428`. When the request's generation is older than the latest, compare the observed element with the live one from Step 2. Changed returns `stale`, `input_sent=false`; unchanged proceeds.
3. **Prove it:** tests for both outcomes, changed and unchanged.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 4: S4, guard every navigation action

1. **Failing test first.** Trip the test kill switch, then call each `NavigateAsync` action: navigate, reload, back, forward, new tab, switch tab, close tab. Confirm they currently run.
2. **Fix.** Every navigation action must, in this order:
   1. check the stop key; tripped returns `denied` with `kill_switch` and `input_sent=false`;
   2. call `ISafetyGate.AuthorizeAsync` with a correct descriptor;
   3. send input;
   4. verify.

   Use the same user-activity rule as `ActAsync`.
3. **Prove it:** one test per action with the switch tripped. Each returns `denied` and `kill_switch` with `input_sent=false`, and the page or tab didn't change.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 5: R1, allow only http, https and about:blank

1. Replace the scheme denylist (`BrowserEngine.cs:228-233`) with an allowlist: `http`, `https`, and exactly `about:blank`. Every other scheme counts as a "non-web scheme" and is marked Risky in its descriptor (Q6, approved).
2. **The owner hasn't decided yet whether non-web schemes are blocked or allowed.** Q6 alone doesn't block them; see rule 7.
   - Until the owner tells you, refuse them: `denied`, `input_sent=false`.
   - Keep this one rule easy to switch: a single config value or one constant, with a test for each setting.
   - Report the setting you shipped.
3. Apply the same rule to navigation and `new_tab` URLs, and to clicks on links whose `href` uses a non-web scheme. Add a fixture link for each.
4. **Prove it:** tests for `file:`, `javascript:`, `data:`, `vbscript:`, `blob:`, `view-source:`, `ms-settings:` and `search-ms:` all return `denied` with `input_sent=false` and are marked Risky, while `http://127.0.0.1/...` works. Also test that `close_tab` is allowed and marked Risky.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 6: R2, report uncertainty honestly

1. A navigation with no expectation returns `unverified`, never `confirmed`.
2. Track "any input command sent" exactly. Set it as soon as the first CDP input or action command is sent: mouse moved, mouse pressed, focus, navigate. Never reset it.
3. If something fails or stops after any input:
   - return `error` with `input_interrupted` and `input_sent=true`;
   - or `error` with an accurate reason code and `input_sent=true`;
   - never `denied` or `blocked` with `input_sent=false`.
4. Map exceptions to accurate reason codes. Use `engine_timeout` only for real timeouts.
5. **Prove it:** tests with a fake session that fails between mouse pressed and mouse released, a navigation without an expectation, and a stop after focus in `type`.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 7: R3, use the real browser process name

1. Record the launched browser's actual process name (`msedge.exe` or `chrome.exe`) in the launcher, and use it in every descriptor. Remove the hard-coded `"msedge.exe"`.
2. **Prove it:** a test checks that the descriptor's process matches the launched executable.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 8: R4, allowlist the profile folder

1. Replace the denylist in `BrowserLauncher.ValidateProfileDirectory` with an allowlist:
   - Resolve the full path, following junctions and symlinks to the final target and expanding 8.3 short names.
   - Accept it only if it's inside OmniWin's own data directory (`%LOCALAPPDATA%\OmniWin` or `--data-dir`). Refuse everything else, with a clear message.
2. **Prove it:** tests for each of these:
   - a path inside the data dir is accepted;
   - each real browser "User Data" path, including Beta, Dev, Canary/SxS, Chromium, Vivaldi and Opera, is refused;
   - a junction inside the data dir that points to a fake "User Data" folder elsewhere is refused;
   - a path with `..` that escapes the data dir is refused.

   Create test folders in a temp area only. Never touch real browser folders.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 9: low items L1–L3

1. **L1:** stop deleting `DevToolsActivePort`. Read it only if its last-write time is after the browser process started; otherwise keep waiting. Prove it with a test: a stale port file left in place doesn't cause a connection to the wrong port.
2. **L2:** check the `type` readback against the expected value after insertion: existing text with the selection replaced. Prove it with a test that types into a field that already has text.
3. **L3:** use page events for navigation waits (load or lifecycle events, with a timeout) instead of 25 ms polling and fixed 100 ms sleeps. If you can't, remove "event-driven waits" from the M1 checklist and say why.

**Checkpoint:** build and full tests pass, and the Fix log is updated.

## Step 10: fix the benchmark test and re-measure

1. Make `BrowserBenchmarkTests` a real opt-in skip (`SkipUnless`, like WS1's benchmarks), so it shows as skipped, not passed, without `OMNIWIN_BROWSER_BENCHMARK=1`.
2. Run it headless with `OMNIWIN_BROWSER_BENCHMARK=1`. It sends no mouse or keyboard input and shows no window, so it doesn't need the owner's yes. Clear the variable afterwards.
3. Measure after Steps 1–9 on both `controls.html` and `large.html`:
   - live observe;
   - `click`;
   - `set_value`;
   - `type`;
   - cold observe;
   - launch to first observe.

   Report each phase separately: resolve, precheck, gate, input, verify.
4. Write the results to a new artifact with a new name. Don't overwrite `ws2-browser-m1-benchmarks.json`.

**Checkpoint:** the artifact exists, `Failure` is null or absent, and the sample counts are as requested.

## Step 11: final checks and documents

1. Debug and Release: build with `-warnaserror`, then the full tests. Record the exact counts as shown.
2. Confirm no Edge or Chrome processes are left running after the tests.
3. Update `docs/status/WS2.md`: the M1 checklist with honest Yes/No, the Fix log, the new measurements with artifact paths and keys, and the open questions.
4. Record new hashes. Don't call them "matching" an older build unless the values are identical, and say which build that was.
5. **Record the owner's approvals.**
   - In each `docs/contract-proposals/WS2-*.md`, change **Status** to "Approved by the owner 2026-11-07 ("yes to all")". For Q6, add the rule 7 limit.
   - Add the approved answers to `02_Architecture_and_Contracts.md` as version 2.3, with a "Changes in 2.3" list at the top. Change only what the approved proposals say.
   - Make sure the code matches each approved answer. List any proposal the code doesn't implement yet as an open item.

## Step 12: report and stop

Reply using the format at the end of `08_WS2_M1_Review_Findings.md`:
- Result: Yes or No for each of S1–S4, R1–R4, L1–L3 and the benchmark fix.
- Changes: file and line, and the proving test's name.
- Tests: exact counts.
- Live runs: headless runs only, and what each one did.
- Measurements: copied from the raw artifact, with path and key.
- Rules check: quote the real text of rules 1–8 from `00_START_HERE_New_AI.md` and B1–B7 from `07_Reply_to_New_AI_Start_WS2.md`.
- Open items: the non-web scheme decision (Step 5), and any approved proposal the code doesn't implement yet.
- Hashes.

**Then stop.** Don't start M2 until the owner says so.
