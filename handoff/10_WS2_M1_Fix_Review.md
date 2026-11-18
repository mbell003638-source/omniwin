# WS2 M1 fix round: reviewer findings and reply to the builder

Copy everything below the line and send it to the AI.

---

The reviewer checked your fix-round report against the code, the test runs and the raw files.

**Verdict: No, M1 isn't accepted yet, but it's close.** Most fixes are real. One new safety problem (N1) and one approved rule that isn't wired in (N2) must be fixed first. Your report also has invented test names and line numbers again, which breaks owner rule 7.

## What the reviewer confirmed

- **Builds and tests:** Debug and Release build with `-warnaserror`, 0 warnings, 0 errors. Both run 533 tests: 527 passed, 6 skipped, 0 failed. The benchmark test now shows as skipped.
- **Clean-up:** no OmniWin browser processes are left after the tests. All tests and the benchmark run headless.
- **Hashes:** the four Release hashes in your report match the current DLLs.
- **Benchmark numbers:** every number you quoted matches `artifacts/benchmarks/ws2-browser-m1-benchmarks-v2.json`. All 500 act samples per row reached the verify phase.
- **Docs:** the 13 proposals are marked "Approved", and `02_Architecture_and_Contracts.md` has a "Changes in 2.3" list.

| Item | Result | Note |
|---|---|---|
| S1 isolated world | **Yes** | The spoof page can't fake names or coordinates. See N4 for one gap. |
| S2 live re-check | **Partly** | Click, hover and double-click are re-checked live. `press` and `type` aren't (N1). A ref with no observed baseline isn't compared (N3). |
| S3 generation check | **Yes** | |
| S4 navigation guards | **Yes** | The stop key, activity check and gate run for all 7 actions. |
| R1 scheme allowlist | **Partly** | See N2 and N5. |
| R2 honest results | **Mostly** | Small gap in click and hover (low items). |
| R3 process name | **Yes** | |
| R4 profile allowlist | **Yes**, one hardening item | See N6. |
| L1 no delete | **Yes** | |
| L2 type readback | **Yes** | |
| L3 event waits | **Partly** | Navigation waits use events now. The test doesn't prove it, and fixed sleeps remain elsewhere (low items). |
| Benchmark opt-in | **Yes** | |

## Must fix

### N1: `press` and `type` can hit an element the gate never saw (high)

The reviewer missed this in the first review.

- **`press`:** `BrowserEngine.cs:761-764` and `CdpSession.cs:365-386` send the key to whatever element has focus. The target ref is never focused, and nothing checks it. The gate approved the ref's live data (`:624-648`), but the key can go somewhere else.
  - **Example:** the agent asks for `press` Space on a harmless text box while the page has focus on a "Delete account" button. On key-up, Space activates the focused button.
- **`type`:** `BrowserEngine.cs:722-734` focuses the ref, then inserts text with `Input.insertText`, which goes to whatever has focus. Two problems:
  - `ExecuteInPageActionAsync` returns whether the action worked (`CdpSession.cs:411-421`), but `ActAsync` ignores that result here and at `:737`, `:742`, `:747`, `:752`, `:757`, `:767` and `:772`.
  - The `focus` action reports success without checking that focus landed (`PageScript.cs:365-368`). A page `onfocus` handler can move focus elsewhere, and the text then goes into the wrong field.
- **Done when:**
  - Before any key or text input, the engine focuses the ref in the isolated world. It then confirms that `document.activeElement` is that exact element, as late as possible before input.
  - If focus isn't on the ref, nothing more is sent. The result is `error` with an accurate reason code and `input_sent=true` (focusing already counts as input under R2). Use an existing code from `ReasonCodes`; don't add one in Core.
  - Every `ExecuteInPageActionAsync` result is checked. A failure stops the action and reports honestly.
  - **New fixture tests:**
    - focus starts on a Delete button, and `press` targets a different ref: the Delete button isn't activated;
    - an `onfocus` handler moves focus away during `type`: no text lands in the other field;
    - `press` Enter in a text box submits a fixture form. There's no `press` test today. `rawKeyDown` without a `text` value probably doesn't trigger form submission, so prove that it works.

### N2: the approved Risky label isn't wired in (high, doc accuracy)

- **Where:** `ClassifyNavRisk` and `ClassifyActRisk` (`BrowserEngine.cs:67-87`) are called only by tests. The descriptors built at `:312-321` and `:637-646` carry no risk. `ActionDescriptor` (`OmniWin.Core/SafetyContracts.cs:7-16`) has no field for it, and the gate's `Classify` returns only Normal or Forbidden.
- **Problem:** Q6 (approved) says Risky is recorded in the descriptor and the audit log. Your report, `WS2.md` and `02_Architecture_and_Contracts.md:13` all say it's done. It isn't.
- **Done when:**
  - Don't edit Core or Safety for this. That's outside your folders (guide rule 5).
  - Instead, write `docs/contract-proposals/WS2-risk-label-wiring.md`. Describe the smallest change that would carry the label to the audit log, and which project owns each part. The owner will decide.
  - Until then, list Q6 as "approved, not implemented" in your open items and in `WS2.md`. Correct the 2.3 bullet in `02_Architecture_and_Contracts.md:13` to match.

### N3: a ref with no observed baseline skips the identity check (medium)

- **Where:** `BrowserEngine.cs:539-551` and `:561`. If the generation or the ref isn't found in history, `observedElement` is null and the act proceeds on live data with no comparison.
- **Done when:** an act whose ref has no observed baseline returns `stale` with `input_sent=false`. Add a test with an unknown generation and one with a ref that wasn't in that generation.

### N4: a missing isolated world throws instead of returning `blocked` (medium)

- **Where:** `CdpSession.cs:99-124` throws. `BrowserEngine.cs:553` calls `InspectElementAsync` outside the `try`, so the exception escapes `ActAsync`.
- **Done when:** if the isolated context can't be found or created, `ActAsync` returns `blocked` with `input_sent=false`, as guide Step 1.2 says. It never falls back to the page world. Add a test.

### N5: finish R1 (medium)

**Owner decision (2026-11-08): non-web links are ALLOWED.** The owner chose to match the "only deleting is blocked" rule exactly. Do not block `file:`, `javascript:`, `ms-settings:` or `search-ms:` by scheme. Remove the scheme block as the default (set `BlockNonWebSchemes` to `false`), or remove the check entirely. The delete-risk classification still applies to the action itself.

1. **Reason code:** if any scheme refusal remains (for example when the setting is turned back on), it uses `policy_forbidden` (`BrowserEngine.cs:348`, `:606`). Your report and `WS2.md:12` and `:59` say `unsupported_scheme`, which doesn't exist in `ReasonCodes`. Correct the docs.
2. **Tests:** with the default now allowing, add a test that a non-web URL is **not** refused for its scheme. Use **only** `data:text/html,...` in automated tests. Never open `file:`, `javascript:`, `ms-settings:` or `search-ms:` in a test run. They can open files, settings or other programs on this machine. Also keep one test with the setting on, proving the optional block still works.
3. **Keys on links:** the link check covers only `click` and `double_click` (`:599-609`). If the setting is on, apply it to `press` Enter or Space on a focused link too.
4. **Script navigation:** a button whose script sets `location` to a non-web scheme isn't caught when the setting is on. List it as a known limit in `WS2.md`. Don't claim it's covered.
5. **Docs:** update the contract and `WS2.md` to say the default is allow, and that this was the owner's decision.

### N6: harden R4 against a bad data directory (medium)

- **Where:** `BrowserLauncher.cs:310-332`. The allowlist trusts the data directory completely. If `--data-dir` is `%LOCALAPPDATA%`, or a real browser's folder, a real profile such as `...\Google\Chrome\User Data` passes as "inside".
- **Done when:** the launcher also refuses:
  - a data directory that is, contains, or sits inside a known browser user-data folder;
  - a profile folder that already holds browser data (such as a `Local State` file) and has no OmniWin marker file. OmniWin writes that marker when it creates the profile.
- **Tests:** use fake folders under a temp data dir only. Never touch real browser folders.

## Low (fix or note in WS2.md)

- **Click and hover:** they set `input_sent` only after `mouseMoved` returns (`CdpSession.cs:296-302`, `:350-356`). Set it before sending, as `NavigateAsync` and `InsertTextAsync` do.
- **Fixed sleeps:** they remain at `CdpSession.cs:313` (15 ms between mouse press and release), `:328` (50 ms), `:377` (15 ms between key down and up), `BrowserEngine.cs:158` (100 ms after connecting) and `:935` (25 ms polling in `WaitForAsync`).
  - The 15 ms click delay adds to every click and widens the window for a page swap. Remove it, or keep it only with a test that shows it's needed.
  - Correct the L3 line in `WS2.md:18`, which says the 25 ms polling and 100 ms sleeps were replaced.
- **Test hooks:** `CdpConnection.CommandInterceptor` (`CdpConnection.cs:32`) and `BrowserEngine.Connection` (`BrowserEngine.cs:22`) are public in shipping code. Make them `internal`, with `InternalsVisibleTo` for the test project.
- **Universal access:** `grantUniveralAccess = true` (`CdpSession.cs:108`) isn't needed. Use `false` unless a test shows otherwise.
- **History:** `_history` (`BrowserEngine.cs:30`) keeps every generation forever. Keep only the last few per tab.
- **Navigation timeout:** when it fires, the fallback accepts the old page's `readyState` (`CdpSession.cs:267-282`). The result is still `unverified`, which is fine, but say so in `WS2.md`.

## Tests to strengthen

- **S1 spoof test** (`BrowserEngineTests.cs:246-266`): also assert that the page event log is empty, as guide Step 1.3 asked.
- **L3 test** (`:580-592`): it would pass with polling too. Make it prove the wait ended on a page event, for example by recording which event completed the wait. Or rename it to what it really checks.

## Problems in your report (owner rule 7: report honestly)

1. **Invented names and places.** Each of these was presented as real, and none exists:
   - **`PageScript.js`:** there's no such file. The script is in `PageScript.cs`.
   - **Helper names:** there's no `window.__omniwin_inspect_element`. The helper is `window.__omniwin.inspectElement`.
   - **Isolated-world methods:** there are no `CreateIsolatedWorldAsync`, `GetOrCreateIsolatedWorldAsync` or `EvaluateInIsolatedWorldAsync`. The real one is `EnsureIsolatedWorldAsync`, at `CdpSession.cs:86-125`.
   - **Test names:** these four don't exist:
     - `Isolated_world_prevents_page_script_prototype_pollution_and_spoofing`: the real test is `Spoof_page_cannot_fake_omniwin_indexer_or_bypass_delete_denial`, at line 247;
     - `Element_moving_during_action_fails_inspection_with_stale`: there's no such test;
     - `Non_web_schemes_are_refused_when_blocking_enabled`: the real one is `NavigateAsync_non_web_schemes_are_refused_and_classified_risky`, at 405;
     - `Allowed_schemes_proceed_normally`: the real one is `NavigateAsync_http_allowed_and_classified_normal`, at 430.
   - **Line numbers:** almost every test line number is wrong. You cite lines 655 and 680, but `BrowserEngineTests.cs` has 603 lines. Code lines are wrong too: `BrowserEngine.cs:160-195` is connection setup, not the isolated world, and `BrowserProcessName` is at `BrowserLauncher.cs:49-52`, not 40.
   - **S3 quote:** the old bug was `element.Role != element.Role`, not `target.Generation != target.Generation`.
2. **False claims:**
   - The reason code `unsupported_scheme` (N5.1).
   - Risky labels in descriptors and the audit log (N2).
   - Profiles "under `%LOCALAPPDATA%\OmniWin\temp-...`". Tests and benchmarks use `%TEMP%\omniwin-test-*`, `omniwin-bench-*` and `omniwin-launch-*`.
3. **Budgets:**
   - **D2 is the wrong label for browser clicks.** D2 is the WS1 desktop click budget. Contract §15 (`02_Architecture_and_Contracts.md:623`) sets `browser_act` at 10 ms or less, excluding page reaction. So `controls_act_click`, at p95 48.76 ms, is **over budget**; report it that way.
   - **"Comfortably meet" again** (`WS2.md:39`): `type` at p95 9.19 ms against a 10 ms budget isn't comfortable. Just give the numbers.
   - **The observe budget is for 150 elements or fewer** (`:622`). `large.html` has 200, so it doesn't test that budget. Add a 150-element fixture and measure observe and click on it.
   - **"14 elements" is stale** (`WS2.md:28`): Step 5 added 9 links to `controls.html`. Report the real count.
4. **Failed runs weren't reported:**
   - The full-suite checkpoint runs for Step 6 and Step 8 exited with code 1 before passing on a rerun. Say what failed and what you changed.
   - You changed an existing test, `BrowserEngineTests.cs:76`, from `Confirmed` to `Unverified`. That's correct under R2, but rule 3 of the guide says to report any changed existing test.
   - There was a benchmark run before the final one. Report it and what you changed after it.
5. **Benchmark build:** the artifact records Browser `56B71EEB…` and Core `65EE6BBC…`. That's the build before the Core version change, not the build whose hashes you reported. Say so.
6. **A change outside your folders without asking:** you changed `OmniWinContract.cs:11` in Core, which also changed the Desktop and Safety hashes. Leave it as is; the owner can see it in this review. Next time, ask first (guide rule 5).
7. **Open items are incomplete.** Approved but not implemented:
   - **Q6:** the Risky label (N2).
   - **Q7:** `window_opened` and `window_closed` on tabs. `EvaluateCondition` has no case for them, so they fall to `_ => false` (`BrowserEngine.cs:1024`).
   - **Q9:** `frames=1` is hard-coded (`:1048`).
   - **Q3:** audit records from the tools.
   - **Q8 and Q11:** as you listed.
8. **Missing measurements:** guide Step 10 asked for `set_value` and `type` on `large.html` too.

## Order of work

1. N1, then N2, N3, N4, N5 and N6, each with a failing test first where it's a code fix. Run the full checkpoint after each, as in the guide.
2. The low items and the stronger tests.
3. Re-run the benchmark into a **new** file, `ws2-browser-m1-benchmarks-v3.json`. Add the 150-element page and the `large.html` `set_value` and `type` rows. Record each sample's status and `input_sent`, not only timings.
4. Fix `WS2.md`, the 2.3 bullet in `02_Architecture_and_Contracts.md`, and your report.
5. Stop. Don't start M2.

The rest of the guide still applies: headless only, no real websites, no real input, don't edit Desktop or Safety, no deletes in product code, and touch no real browser folders.

## Reply format

Use the same headings: Result, Changes, Tests, Live runs, Measurements, Rules check, Open items and owner decisions, Hashes. Also:

- **Result:** Yes or No for N1–N6, plus the low items and test fixes you did.
- **Changes:** for every file, function, test name and line number you cite, copy it from the file **after your last edit**. Use a search such as `Select-String -Pattern <name> -Path <file>` to get them, and don't write any from memory.
- **Failed runs:** list every run that failed, with what you changed.
- **Measurements:** copy every number from the raw file, naming the file and key, and include the build hashes the artifact records.
