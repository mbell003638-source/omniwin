# WS2 browser engine status

Updated 2026-11-08. Shared contract 2.3. M1 fix round 2 (reviewer findings N1–N6, low items, test fixes). M2 not started.

## Current status (M1)

- [x] **S1: Isolated world indexer.** The indexer and every in-page action run in a CDP isolated world (`CdpSession.EnsureIsolatedWorldAsync`). If the world cannot be found or created, `ActAsync` returns `blocked` / `browser_not_connected` with `input_sent=false` and never uses the page world (N4, test `Act_when_isolated_world_cannot_be_created_returns_blocked_and_sends_no_input`).
- [x] **S2: Live re-check before input.** Click, double click and hover re-read the element and hit-test it live. `press` and `type` now focus the target ref in the isolated world and confirm it is the focused element right before any key or text is sent (N1).
- [x] **S3: Generation check.** The live element is compared with the observed one by role, name and bounds. An act whose generation or ref was never observed returns `stale` with `input_sent=false` (N3). Only the last 8 generations per tab are kept, so an older generation is `stale`.
- [x] **S4: Guarded navigation actions.** All 7 navigation actions check the stop key, the user-activity pause and the safety gate.
- [x] **R1: Non-web schemes: allowed, only labelled.** Owner decision 2026-11-08: nothing is blocked by policy except deleting. No navigation, `new_tab`, link click or key press is refused for its URL scheme. The optional `BlockNonWebSchemes` setting and both of its refusals were removed (P0). `IsNonWebScheme`, `ClassifyNavRisk` and `ClassifyActRisk` stay; they only feed the Risky label (Q6), which records and never blocks. The earlier docs said `unsupported_scheme`; that code never existed.
- [x] **R2: Honest results.** Click and hover now set `input_sent` before the first command is sent. Every in-page action result is checked; a failure returns `error` with an accurate reason and `input_sent=true`.
- [x] **R3: Real process name.**
- [x] **R4: Profile allowlist.** Also refuses a data directory that is, contains or sits inside a known real-browser user-data folder (`BrowserLauncher.KnownBrowserUserDataDirs`), and a profile folder that already holds browser data but has no `.omniwin-profile` marker (`BrowserLauncher.PrepareProfileDirectory`) (N6).
- [x] **L1, L2.**
- [~] **L3: Event waits, partly.** Navigation waits end on page events (`Page.loadEventFired` and similar). The test now checks which event ended the wait (`CdpSession.LastNavigationSignal`). **Still fixed or polled:** `WaitForAsync` polls every 25 ms (contract §11 allows a bounded fallback), and the launcher polls for `DevToolsActivePort` every 50 ms. Removed in this round: the 15 ms click press/release delay, the 15 ms key down/up delay, the 50 ms double-click delay, and the 100 ms sleep after connecting (now waits for the attach event).
- [x] **Benchmark opt-in.**

## Not implemented (approved, but not in the code)

- ~~Q6: Risky label~~ is **done** (moved out of this list on 2026-11-08, see "Q6 Risky label" at the end).
- **Q7:** `window_opened` and `window_closed` on tabs: `EvaluateCondition` has no case for them (falls to `_ => false`).
- **Q9:** `frames=1` is hard-coded in `BuildObservationText`.
- **Q3:** audit records from the browser tools; **Q8** (dialog interception) and **Q11** (rich tabs list) as before. MCP browser tools are M2.

## Known limits

- Non-web schemes are never blocked, so there is no scheme limit to list. (The earlier "script navigation is not caught" limit went away with the optional block.)
- When a navigation wait times out and the old page reports `readyState` interactive or complete, the wait accepts it (`LastNavigationSignal = readystate_fallback`). The result is still `unverified`.
- A profile folder from an earlier build that already holds browser data has no marker and is now refused until the marker file is added or the folder is replaced. (No OmniWin data folder exists on this machine, so nothing is affected here.)

## Performance (ms)

Raw artifact: `artifacts/benchmarks/ws2-browser-m1-benchmarks-v3.json`, key `summaries.<row>` (`total.p50`, `total.p95`, `total.p99`, per-phase blocks, `status_counts`, `input_sent_true`). `failure` is absent (null). Microsoft Edge version and loaded-assembly hashes are in the same file (`browser_version`, `loaded_assemblies`). Headless, no real input. Every act sample reached the input step (`input_sent_true` = 500 of 500 in all 9 act rows; statuses `unverified` for click, `confirmed` for set_value and type).

Contract §15 budgets (`02_Architecture_and_Contracts.md`): `browser_observe` ≤ 10 ms for 150 elements or fewer; `browser_act` ≤ 10 ms excluding page reaction.

| Row | p50 | p95 | p99 | Budget | Phases p50 / p95 |
|---|---|---|---|---|---|
| `controls_observe_live` (21 elements) | 1.91 | 2.56 | 3.48 | ≤ 10, **met** | read 1.89 / 2.50 |
| `elements150_observe_live` (150 elements) | 19.38 | 35.15 | 40.40 | ≤ 10, **over budget** | read 19.28 / 35.00 |
| `large_observe_live` (201 elements) | 38.14 | 65.83 | 78.18 | none (above 150) | read 36.72 / 64.42 |
| `controls_observe_cold` | 3.33 | 5.54 | 8.00 | — | |
| `elements150_observe_cold` | 34.47 | 38.83 | 41.71 | — | |
| `large_observe_cold` | 52.51 | 71.84 | 87.76 | — | |
| `controls_act_click` | 16.64 | 18.80 | 20.28 | ≤ 10, **over budget** | input 12.05 / 14.05, verify 3.73 / 5.28 |
| `controls_act_set_value` | 6.96 | 9.71 | 11.27 | ≤ 10 | verify 5.26 / 7.20 |
| `controls_act_type` | 7.12 | 11.95 | 14.31 | ≤ 10, **over budget at p95** | input 2.00 / 3.97, verify 4.39 / 7.06 |
| `elements150_act_click` | 34.00 | 49.78 | 52.82 | ≤ 10, **over budget** | input 7.32 / 15.31, verify 25.03 / 37.52 |
| `elements150_act_set_value` | 23.17 | 38.49 | 43.73 | ≤ 10, **over budget** | verify 21.35 / 36.65 |
| `elements150_act_type` | 24.47 | 40.68 | 44.84 | ≤ 10, **over budget** | verify 21.78 / 36.47 |
| `large_act_click` | 45.01 | 75.28 | 84.49 | ≤ 10, **over budget** | input 2.48 / 11.91, verify 39.20 / 69.35 |
| `large_act_set_value` | 47.07 | 76.65 | 85.46 | ≤ 10, **over budget** | verify 44.84 / 74.99 |
| `large_act_type` | 51.34 | 78.35 | 87.77 | ≤ 10, **over budget** | input 2.30 / 3.24, verify 47.92 / 74.11 |
| `launch_to_first_observe` | 586.80 | 832.27 | 832.27 | — | 5 samples |

Why acts are over: the verify phase re-scans up to 150 elements after the input (the observe cost above), and click input includes the CDP mouse round trips. The `D2` label used in the previous report was wrong: D2 is the WS1 desktop click budget. Not met: observe at 150 elements and every act row except `controls_act_set_value` (p95 9.71) — these need an owner decision or a faster scan.

Fixtures: `controls.html` has 21 interactive elements (the earlier "14" was before the link fixtures); `elements150.html` has 150; `large.html` has 201 (an input was added for the set_value and type rows).

## Contract Proposals Status
13 proposals approved by the owner 2026-11-07 ("yes to all"), in Architecture & Contracts v2.3. Q6 is implemented (2026-11-08) (see above), through `docs/contract-proposals/WS2-risk-label-wiring.md` (approved and implemented 2026-11-08). No WS2 proposal is waiting on the owner.

## Open items & owner decisions
1. **Performance: decided by the owner 2026-11-08.** Slow on bigger pages is acceptable; no speed work now. Observe at 150 elements (p95 35.15 ms) and most act rows are over the 10 ms budgets in the table above, and that is accepted. The contract §15 text has not been changed (contract changes go through WS0); the owner may want the 10 ms figures reworded to small pages only.
2. **Q6 wiring:** done 2026-11-08 (owner approved the main version of `WS2-risk-label-wiring.md`).
3. **Q7, Q9, Q3, Q8, Q11:** not implemented (above).
4. **Core edit made without asking in the previous round:** `OmniWinContract.cs:11` (contract version). Left as is.
5. **M2:** not started.

## Fix log
Earlier steps (Steps 0–11 of the first guide) are summarised in the checklist above. Round 2 (this file's date):
- **N1:** failing tests first (4 failed), then fixed. `type` and `press` focus the ref, confirm focus, then send. `press` Enter/Space send `keyDown` with text so Enter submits a form. Tests: `Press_goes_to_the_target_ref_not_to_the_element_that_had_focus`, `Press_enter_in_text_box_submits_the_form`, `Type_when_page_moves_focus_away_sends_no_text_and_reports_focus_failed`, `Press_when_page_moves_focus_away_sends_no_key_and_reports_focus_failed`. Needed `Emulation.setFocusEmulationEnabled`; without it a headless page fires `onfocus` late and the first fix passed the check too early.
- **N2:** not implemented; proposal written; docs corrected.
- **N3:** `Act_with_unknown_generation_returns_stale_and_sends_no_input`, `Act_with_ref_that_was_not_in_that_generation_returns_stale_and_sends_no_input`.
- **N4:** `Act_when_isolated_world_cannot_be_created_returns_blocked_and_sends_no_input`.
- **N5:** default changed to allow; tests use `data:` only with blocking off.
- **N6:** 5 new `BrowserLauncherTests` (fake folders under temp only).
- **Bug found by the v3 benchmark:** history trimming kept the highest generation numbers, but generations restart on a new page, so after a long-lived page every act on the next page was `stale`. Fixed with `RecordHistory` (clears the tab history when the generation does not go up); tests `Observation_history_resets_on_a_new_page_so_acts_after_a_long_lived_page_are_not_stale` and `Click_set_value_and_type_work_on_big_pages`. The first v3 run is kept as `artifacts/benchmarks/ws2-browser-m1-benchmarks-v3-discarded-history-bug.json` (not valid data: 6 of 9 act rows were all `stale`).
- **Changed existing tests (round 2):** the L3 test was renamed and strengthened; the spoof test now also checks the page event log. **Removed with the feature (owner decision, P0):** `NavigateAsync_non_web_schemes_are_refused_when_blocking_is_turned_on_and_classified_risky` (the 8-URL theory; its 8 `InlineData` cases were kept as the pure-function test `ClassifyNavRisk_marks_non_web_urls_risky_and_never_navigates`, which opens nothing), `ActAsync_clicking_non_web_link_is_refused_when_blocking_is_turned_on`, `ActAsync_press_enter_on_a_non_web_link_is_refused_when_blocking_is_turned_on` and `BlockNonWebSchemes_defaults_to_off_by_owner_decision`. Kept: `NavigateAsync_data_url_is_not_refused_for_its_scheme`, `ActAsync_clicking_a_data_link_is_not_refused_for_its_scheme` (renamed, no longer mention a default) and `ClassifyActRisk_non_web_link_is_risky_for_click_and_activation_key`.

## Final checks (2026-11-08)
- Debug and Release: `dotnet build OmniWin.sln -warnaserror` 0 warnings, 0 errors. `dotnet test` on the whole solution: 558 total, 552 passed, 6 skipped, 0 failed (both configurations). Browser project alone: 83 total, 82 passed, 1 skipped (the opt-in benchmark).
- No `msedge.exe` or OmniWin processes were left running after the runs.
- Release SHA-256: `OmniWin.Browser.dll` c77117993a40f06f876f22e442afd98839f9271f49f0298950b10fb1e4719be1; `OmniWin.Core.dll` 254441c25b34e61d9937ec390dab70e07df7a0033d622694885113b8b74fbc64; `OmniWin.Safety.dll` 4ff11ebe9d3301299ae6b35d51f9f33fefefcd877ff2efda89855534cdaade6c; `OmniWin.Desktop.dll` c773884d9815653460c6151066a2be996a2d368bbeff522abb31ee1869970c0b. The v3 benchmark's loaded Browser, Safety and Core hashes match these (same build). These are different from the v2 artifact's hashes (an older build).

## Round 3 (reviewer findings P1, P2 and low items), 2026-11-08
- **P1: failure after input returns a result.** The verify phase of `ActAsync` and the wait in `NavigateAsync` are now inside `try`/`catch`. A failure after input returns `unverified` with `input_sent=true` and a detail ("page changed or could not be read during verify"), or `error` / `input_interrupted` if the caller cancelled. Failing first: with the new catch switched off, `Failure_while_verifying_after_input_returns_unverified_with_input_sent` failed with the simulated exception, and `Clicking_a_link_that_navigates_always_returns_a_result_with_input_sent_in_a_loop` failed with `CDP error (19): Cannot find context with specified id` (the reviewer's scenario, reproduced). With the fix both pass (the loop is 50 link clicks that navigate). The act path only gained a try block, so no new benchmark was run.
- **P2: tests fail closed.** `NavigationGuard` (in `TestDoubles.cs`) is installed as the `CommandInterceptor` in every `BrowserEngineTests` run and in the benchmark. It throws before `Page.navigate` or `Target.createTarget` is sent for any URL that is not `http://127.0.0.1`, `http://localhost`, `about:blank` or `data:`. Tests that need their own interceptor use `NavigationGuard.With(...)` and restore `NavigationGuard.Check`. Tests: `NavigationGuard_allows_only_local_blank_and_data_urls` (10 cases) and `NavigationGuard_throws_before_a_non_local_navigate_is_sent`. The seven risky-scheme links were first moved to `nonweb-links.html`; after the owner's "no blocks except deleting" decision (P0) that fixture and its loader were deleted, so no fixture page links to `file:`, `javascript:`, `vbscript:`, `blob:`, `view-source:`, `ms-settings:` or `search-ms:`. `controls.html` keeps the `data:` link and the http link.
- **Low:** `CdpSession.EvaluatePageScriptAsync` is now `internal`. New tests for the last focus check (`IsFocusedAsync`, right before text or keys): `Type_when_focus_moves_just_before_the_last_check_sends_no_text` and `Press_when_focus_moves_just_before_the_last_check_sends_no_key`, on the new `focus-late.html`.
- **Failed or unusual runs, round 2 (not listed before):**
  - **Unsafe scheme run.** A full Browser test run was started after the default became "allow" but before the old scheme tests were changed. Those tests navigated the headless browser to `file:///C:/Windows/notepad.exe`, `javascript:alert(1)`, `ms-settings:windowsupdate` and `search-ms:query=test` (the ms-settings and vbscript cases took about 10 s each to time out). Afterwards I checked, read-only: no Settings, Notepad-from-test or browser processes, nothing new in Downloads. That cannot prove nothing ran. The tests were fixed before any further run, and P2 now makes this impossible in a test run.
  - **Run stopped after 580 s.** The first full Browser run after the N1 fix hit my 580 s command limit and was moved to the background, then stopped. I believe it was waiting in those same scheme tests (several 10 s timeouts plus the external-protocol navigations). I did not prove that.
  - **Stuck test process.** After that run, a test process (`OmniWin.Browser.Tests`, PID 17640) was still alive and locked the build output; the build failed with MSB3021/MSB3027. I stopped it with `taskkill` (its child browser processes ended with it). A `Notepad.exe` that appeared in the process list was the owner's own (started 2026-11-07 with their file) and was left alone.
- **Note for M2 (not changed):** `ResolveSelectorAsync` scans but does not record the scan in history or return its generation, so an act on a resolved ref is `stale` unless the ref was observed first. Workflow replay needs a resolved ref plus a usable generation. That is an interface change; write it up as a proposal when M2 starts.

### Round 3 final checks
- Debug and Release: `dotnet build OmniWin.sln -warnaserror` 0 warnings, 0 errors. Whole-solution `dotnet test`: 573 total, 567 passed, 6 skipped, 0 failed, in both. Browser project alone: 98 total, 97 passed, 1 skipped (the opt-in benchmark).
- No `msedge.exe` or OmniWin processes left after the runs.
- Release SHA-256: `OmniWin.Browser.dll` 2f196892c3a6b0b967dc188404fe6c0ef3f4a0193ebdb80f9b25d53e481fb3c8 (changed this round); `OmniWin.Core.dll` 254441c25b34e61d9937ec390dab70e07df7a0033d622694885113b8b74fbc64, `OmniWin.Safety.dll` 4ff11ebe9d3301299ae6b35d51f9f33fefefcd877ff2efda89855534cdaade6c and `OmniWin.Desktop.dll` c773884d9815653460c6151066a2be996a2d368bbeff522abb31ee1869970c0b (unchanged). The v3 benchmark was recorded on the earlier Browser build `c77117993a40…`, not this one; no v4 was run because the act path only gained a `try` block.

### P0 (owner decision 2026-11-08: no blocks apart from deleting)
- Removed `BrowserEngine.BlockNonWebSchemes` and its refusal in `NavigateAsync` and in the `ActAsync` precheck. Nothing is refused for a URL scheme any more; the label still records.
- The safety checks that are not policy blocks stay unchanged: stop key and user-activity pause, stale / focus / hit-test / disabled / dialog checks, and the dedicated-profile check.
- No failing-first test was possible for a removal that must not open a risky URL; the kept `data:` tests show a non-web scheme is not refused, and `NavigationGuard` would throw before a `file:`-style URL were sent.
- Docs updated: this file (R1 line, known limits, test list) and the Q6 bullet in `02_Architecture_and_Contracts.md`.
- Another leftover test process locked the build output once more (PID 30796, `MSB3021`); it had exited on its own by the time I looked, so I stopped nothing. The Browser test host takes a few seconds to exit after a run.
- **Q6 label wiring:** not done in that round; done afterwards (section "Q6 Risky label" below).

### P0 final checks
- Debug and Release: `dotnet build OmniWin.sln -warnaserror` 0 warnings, 0 errors. Whole-solution `dotnet test`: 570 total, 564 passed, 6 skipped, 0 failed, in both. Browser project alone: 95 total, 94 passed, 1 skipped (the opt-in benchmark). The drop from 573 is the three removed tests; the 8-URL theory became an 8-case pure-function test.
- After the runs the test host and an `msedge` process were still listed a few seconds later and were gone within 20 s; no process was left, none was stopped.
- Release SHA-256: `OmniWin.Browser.dll` 70d005d98b7bda02146e050551842054c4b2bfd3deab45a66d7b83af8024f31e (changed again, P0); `OmniWin.Core.dll` 254441c25b34e61d9937ec390dab70e07df7a0033d622694885113b8b74fbc64, `OmniWin.Safety.dll` 4ff11ebe9d3301299ae6b35d51f9f33fefefcd877ff2efda89855534cdaade6c, `OmniWin.Desktop.dll` c773884d9815653460c6151066a2be996a2d368bbeff522abb31ee1869970c0b (unchanged). The v3 benchmark was recorded on Browser build `c77117993a40…`; the act path since then gained only a `try` block and lost a skipped-by-default refusal, so no v4 was run.

## Q6 Risky label, 2026-11-08 (implemented; owner approved the main version of the proposal)
The label only records. It never blocks, asks, delays or changes an action; deleting is still the only thing blocked.
- **`ActResult.Risk`** (`RiskClass? Risk = null`, last optional parameter, `OmniWin.Core/Results.cs`). No existing call changed.
- **`risk=risky` token:** `ResultFormatter.Format(ActResult)` prints it on the first line only when `Risk` is `Risky`; every other result text is unchanged.
- **`AuditEntry.Risk`** (`string? Risk = null`, last optional field, `OmniWin.Core/SafetyContracts.cs`), written as `"risk":"risky"` and left out when null.
- **`ToolCallAudit.BuildEntry`** (`OmniWin.Server/Hosting/ToolCallAudit.cs`) fills it from the `risk=` token on the first result line.
- **Browser:** `NavigateAsync` and `ActAsync` set `Risk` on every result they return. Navigation is classified from the action and URL, so denied, blocked and error results carry it too. `ActAsync` is classified from the live element once it has been read, so a result returned before that (unknown generation, element gone, no isolated world) has no label.
- **Tests:** Browser `Risk_close_tab_is_labelled_risky_and_still_runs_unchanged`, `Risk_navigating_to_a_data_url_is_labelled_risky_and_still_runs_unchanged`, `Risk_clicking_the_data_link_is_labelled_risky_and_still_runs_unchanged`, `Risk_pressing_enter_in_the_password_field_is_labelled_risky_and_still_runs_unchanged`, `Risk_normal_click_and_normal_http_navigation_are_not_labelled_risky`, `Risk_label_is_also_set_on_a_denied_result_and_does_not_change_it`. Core `Risky_result_prints_risk_risky_on_the_first_line`, `Result_that_is_not_risky_prints_no_risk_token_and_is_unchanged`, `Risky_label_adds_only_the_token_and_changes_nothing_else`. Server (in `OmniWin.Integration`) `ToolCallAuditTests`. Audit writer `Risk_label_is_written_when_set_and_left_out_when_null`. With the Browser wiring switched off, the five Browser label tests failed; they pass with it on.
- **No browser audit line exists yet:** the browser tools are not registered in the Server until M2. The Server tests prove the plumbing from a result's first line to `AuditEntry.Risk`.
- **Two small extra edits made so the Server test can run** (no Server test project exists and the solution file is not to be edited): `InternalsVisibleTo Include="OmniWin.Integration"` in `OmniWin.Server.csproj`, and `ReferenceOutputAssembly="false"` removed from the Server reference in `OmniWin.Integration.csproj`.

### Q6 final checks
- Debug and Release: `dotnet build OmniWin.sln -warnaserror` 0 warnings, 0 errors. Whole-solution `dotnet test`: 589 total, 583 passed, 6 skipped, 0 failed, in both. No existing test was removed, skipped or weakened.
- Release SHA-256: `OmniWin.Browser.dll` d09496e390936bf1e951c9481b5471f60700dd83b4088e66baeb36052d04a658; `OmniWin.Core.dll` c2bff80ff5aed3b9f97246ba73241e0a244a18cddc97279d6e5fa70bc5ab7248; `OmniWin.Safety.dll` 9af2e85a5908beda4d4617e58ae9a0a3eb6866a7c37e19f95fe24d941b16953f; `OmniWin.Desktop.dll` 6a05dd17caf11f227bc6bd657c5d3514b1d20fb28f85a9876fa7edce77e0050d; `OmniWin.Server.dll` 4360f233b700a5b7e8e127d4ca9eeb9199da3343b82d702948f8de57f8ec7bbc. Core changed (new fields), so Safety, Desktop and Browser were rebuilt against it and their hashes changed; I edited no Desktop or Safety source.
- Open: the C# sample of `ActResult` and `AuditEntry` inside `02_Architecture_and_Contracts.md` (§6/§7) does not show the new `Risk` fields yet; only the Q6 bullet was updated. WS0 owns that file.

## Phase 0 of the full-project handoff, done by the reviewer (2026-11-08)
- `BrowserEngine.ConnectionCreated` (internal test hook) runs on every new CDP connection before its first command, including after a reconnect. `BrowserEngineTests.InitializeAsync` and `BrowserBenchmarkTests` now install `NavigationGuard` through it instead of after connecting.
- `NavigationGuard_throws_before_a_non_local_navigate_is_sent` uses `http://omniwin-guard-test.invalid/` instead of an `ms-settings:` URL. New test: `NavigationGuard_is_installed_on_every_new_connection_through_the_engine_hook`.
- This file's proposal line (Q6) corrected. Contract: `ActResult` and `AuditEntry` C# samples now show `ExpectOutcome` and `Risk`; leftover confirm-dialog wording in §4, §5 and §7 marked as not used under the owner's policy.
- Checks: Debug and Release `-warnaserror` 0 warnings, 0 errors; whole solution 590 total, 584 passed, 6 skipped, 0 failed in both. No Edge or test process left. Release Browser DLL `4F74D03CF5204EF8ACE98E299750CB27B4FCFBE0594E85C9851EF93A0874DAD7`, Server `B9480AB8268EFEAADE2265D615CEE2825A20BF5BB575F2E53EC37B87A071B74D` (Core, Safety, Desktop unchanged).

## `browser_view` (look-only), 2026-11-08
- **Code:** `BrowserEngine.ViewTabAsync` (`IBrowserViewer`), `CdpSession.CaptureViewAsync`, `PageScript` `sensitiveRects`, shared imaging in `OmniWin.Core/ViewImaging.cs`.
- **How:** CDP `Page.captureScreenshot` of the viewport (or the whole page with `full_page`). Every password and payment field, in the page and in same-origin child frames, and every iframe whose document can't be read (cross-origin) is found in the isolated world, read before and after the screenshot, and blacked out on the full-resolution pixels before scaling and encoding. Any failure returns `error` and no image. An open JavaScript dialog gives `blocked modal_blocking`. No input is sent, the stop key applies, and nothing is written to disk.
- **Tests (`BrowserViewTests`, 8 headless on local fixtures, `NavigationGuard` installed through `ConnectionCreated`):** image plus untrusted header and metadata line; `max_side` respected, default 1280, never above 1920; password and payment pixels uniform black while an ordinary field stays visible; cross-origin frame black and same-origin frame visible; full page covers 2400+ px and masks a password field below the fold (`tall.html`); sensitive-field read failure gives no image; open dialog blocks and a tripped stop key denies; a view sends no input. One opt-in measurement test.
- **Measurement** (`artifacts/benchmarks/view-browser-v1.json`, `rows.<name>`, 50 samples each, all `ok`): `controls_viewport` p50 52.30 / p95 71.08 / p99 74.44 ms, 1256x708 PNG 25.5 KB; `large_viewport` p50 73.73 / p95 94.89 / p99 101.75 ms, 68.7 KB; `tall_full_page` p50 151.52 / p95 222.42 / p99 1157.70 ms, 10.9 KB. Report-only row, target at most 300 ms: met at p95 for all three; the full-page p99 is over.
- **Two engine fixes found by the new tests' full-suite run:** `window_opened` / `window_closed` waits no longer scan the page (a command to a closing tab can go unanswered and hung `Window_opened_matches_a_new_tab_by_title_and_window_closed_matches_a_closed_tab` for 90 s only when the whole suite ran); and `BrowserViewTests` and `BrowserEngineTests` now share the xunit collection "Browser engine" so they don't start browsers side by side. Failed runs: that test failed twice in full runs before the fix; it passed alone every time.
- **Totals:** Browser project 119 tests, 117 passed, 2 skipped (the two opt-in benchmarks), 0 failed. Fallback-order wording added to `ToolDocs.FallbackOrder` and to `browser_observe`, `screen_ocr`, `screen_view` and `browser_view`.

## M2 to M5 integration checks, 2026-11-08
- Debug and Release, whole solution, `-warnaserror`: 0 warnings, 0 errors. `dotnet test`: 1044 total, 994 passed, 50 skipped, 0 failed, in both. Skipped tests are the opt-in benchmarks, the real-input / live-session tests (`OMNIWIN_LIVE_SESSION=1`), tests that need an interactive desktop, and the rate-limit test (nothing enforces `rate_limit`, WS4 finding 3).
- One Debug-only failure in the first run (`Instruction_like_page_text_stays_inside_quoted_fields_and_forges_nothing`) was a test bug: the fake ref `b99` in the page text collided with a real ref number. The fake is now `b99999`.
- Published to `publish\` (31 MB, 55 files). Smoke test over stdio on a temp data folder: clean JSON-RPC on stdout, contract 2.4, 26 tools (the same 26 as contract §8), `config.json` and `logs\` created.
- Release SHA-256: Browser b444c928…e012, Core 4bfb5448…0e72, Safety 357652c9…ef7a, Desktop f264aac8…d7d3, Server 3a9bfbfa…3cf0, SystemTools 1bf908f6…1e04, Ocr 3049eb09…0304, Workflows 45b1d821…707b; `publish\OmniWin.Server.exe` 30aed1e9…ba75.
- Browser fixes from WS4 findings: observation text now uses line feeds only; `omniwin_status` shows `milestone=M5-release`.
- Not done yet: the live session (needs the owner at the PC) and the Antigravity registration.

### Review fix F5 (browser_view), Debug
- **Shadow DOM:** the page script now walks open shadow roots; CDP (`DOM.getDocument` with `pierce`) finds closed shadow roots, and any host whose closed root holds an input or textarea is blacked out whole (also a child frame that holds one).
- **Deep frames:** inside a child frame, every nested frame is blacked out whole (`sensitiveRects(true)`), so a frame two levels deep is never searched and never shown.
- **Tests (new fixtures `shadow.html`, `frames-deep.html`, `frame-mid.html`, `frame-pw.html`):** `A_password_field_inside_an_open_shadow_root_is_black_and_a_closed_shadow_root_with_an_input_is_black_whole`, `A_frame_two_levels_deep_is_black_whole_even_though_it_holds_a_password_field`. Both fail with the fixes disabled and pass with them.
- **Cost:** every `browser_view` now makes one extra CDP call (`DOM.getDocument`). Timings in `view-browser-v1.json` were taken before F5 and were not re-measured.
- `OmniWin.Browser.Tests` (Debug): 122 total, 120 passed, 2 skipped (opt-in benchmarks), 0 failed.

## Observe on very large pages: cause, fix and timings (WS4 finding 6), 2026-11-08
- **Cause (found by timing each step):** navigation (80 to 235 ms), the element count (1 to 3 ms) and the C# side were not slow. The page script was. For every button it built a selector path that also computed the *name* of each ancestor, and the name of the list container is its whole `innerText` (thousands of rows of text, recomputed for each button), and it rebuilt the sibling index of each ancestor from scratch. On the 2,000-button page one scan took 2.4 s; on the 10,000-button page it did not finish in over 100 s.
- **Fix, in `PageScript.cs`:** (1) a container with more than 50 children uses a cheap name (aria-label, title or id) in the selector path; ordinary elements and small containers keep the full name exactly as before; (2) the sibling index is computed once per parent per scan; (3) a hard cap: one scan inspects at most 3000 candidate elements (`MAX_CONSIDERED_ELEMENTS`), counts the rest, and the result says so: `omitted: ..., N beyond the scan limit` and `truncated: N more not read (page scan limit; use viewport_only or narrow scope)`. `ElementScanResult.CappedAtLimit` carries the count.
- **Timings** (`artifacts/benchmarks/ws2-observe-scan-cap-v1.json`, key `rows.<name>`: `p50_ms`, `max_ms`, `capped_line_present`; 10 samples each, headless Edge, Debug build, `max_elements` 1000): 2,000 buttons p50 246.4 ms, max 355.2 ms, no scan-limit line (before: about 2,360 ms per scan); 10,000 buttons p50 1236.4 ms, max 1917 ms, scan-limit line present, 3,000 elements listed and 7,000 reported as not read (before: no answer within 100 s, `engine_timeout` at 30 s). Release numbers should be a little lower; not re-measured.
- **Tests:** `BrowserScanCapTests` (`A_10000_button_page_answers_quickly_and_says_how_many_buttons_were_not_read`, `A_2000_button_page_is_under_the_cap_and_shows_no_scan_limit_line`, `Measure_2000_and_10000_button_observe_and_write_the_artifact`). Browser project in Debug: 131 total, 128 passed, 3 skipped (opt-in), 0 failed.
- **Failed runs:** the first two scan-cap runs timed out at 2 to 3 minutes (the cause above); one assertion of mine looked for `Row 1 ` instead of `button "Row 1"`; a temporary timing test was added and removed again.

## Browser parallel lanes, 2026-11-09 (parallel-agent changes (browser))
Calls on different tabs run at the same time and each lands only on its own tab; calls on the same tab take turns.
- **Ref to tab:** every scan records which tab each `bN` ref came from (`_refTabs`, `BrowserEngine.ScanTabAsync`). `ActAsync` and `DescribeAsync` now use the tab of the ref (`GetSessionForRef`) and fall back to the active tab only for an unknown ref, which keeps its old meaning (`stale`, `input_sent=false`). `ResolveSelectorAsync` uses the active tab, or the tab whose URL matches the selector.
- **Refs are unique across tabs:** the shared counter `_nextElementId` is gone. Each tab takes ref numbers from its own lane (tab number minus 1, modulo 16; ids lane+1, lane+17, ...), so two tabs never produce the same ref. Page script `scan` takes a stride. Tabs on the same lane (more than 16 tabs) take turns scanning.
- **One input-bearing call at a time per tab:** `CdpSession.OpGate` serializes `ActAsync` and `NavigateAsync` on a tab. The wait honours the stop key (result `denied kill_switch`, `input_sent=false`) and the caller's cancellation. Other tabs never wait on it. Observe, view and wait are not gated.
- **Thread safety:** `RecordHistory` trims under a lock; the active tab id is `volatile`.
- **A dialog on one tab never holds up another tab:** per-tab state, tested.
- **Tab throttling found and fixed:** Edge runs a tab that is not in front of its window at about 1 frame a second, so every input to it took about 1000 ms (found by timing chained clicks: 15 ms for the first click, about 1000 ms input time for each next one). `new_tab` now opens the tab in its own window (`Target.createTarget` with `newWindow: true`), and the launcher adds `--disable-background-timer-throttling`, `--disable-renderer-backgrounding` and `--disable-backgrounding-occluded-windows`. Chained clicks are now 15 to 31 ms.
- **Measurement:** `artifacts/benchmarks/ws2-browser-parallel-v1.json`, keys `serial_total_ms`, `parallel_total_ms`, `speedup`: two tabs (150-element page and 201-element page), 100 click acts each with the 150-element verify scan, headless Edge. All 200 acts succeeded in both modes. Serial 3383.7 ms, parallel 1702.5 ms, speedup 1.99. Earlier runs of the same measurement (about 100 s and about 40 s, speedup 1.0) were invalid and were overwritten: first the test reused a generation that the tab had already trimmed (every act `stale`), then the throttling above made every act take about 500 ms.
- **Tests (`BrowserParallelTests`):** `Acts_on_two_tabs_at_once_each_land_only_on_their_own_tab_for_50_rounds`, `A_ref_names_its_tab_so_describe_and_act_use_that_tab_not_the_active_one`, `Concurrent_acts_on_the_same_tab_take_turns_so_their_mouse_events_never_interleave`, `A_dialog_open_on_one_tab_does_not_hold_up_another_tab`, `The_stop_key_ends_a_wait_for_a_busy_tab_with_denied_and_nothing_sent`, and the opt-in `Measure_parallel_against_serial_on_two_tabs` (`OMNIWIN_PARALLEL_BENCH=1`, `OMNIWIN_PARALLEL_ACTS=n`). With the ref-to-tab lookup switched off three tests failed; with the per-tab gate switched off two failed.
- **Browser project (Release, without the other agent's `BrowserScanCapTests`):** 128 total, 125 passed, 3 skipped (opt-in measurements), 0 failed. The parallel class was run four times in a row, all passing.

## Reviewer follow-ups, 2026-11-10 (after handoff 19)
- **Exact profile match:** `BrowserProcesses.NamesProfile` matches a process only when one of its `--user-data-dir` values is exactly the profile folder, so `X\profile` never matches `X\profile2`.
- **Leftover browser:** each launch writes `.omniwin-owner` (server process id and start time) in the profile folder. `BrowserLauncher.ClearLeftoverBrowser` stops a browser left on the profile by a server that is gone, or by this same server, before launching. It refuses, and touches nothing, when another running OmniWin server owns it. `BrowserEngine.EnsureConnectedAsync` closes the previous connection and launch before reconnecting.
- **Frames not read:** the page script reports same-origin frames that are still loading. `CdpSession` counts those, plus frames whose read failed (`FramesNotRead`). The observation footer then says `frames_not_read: N (still loading or changed while read; observe again)`.
- **Tests:**
  - `NamesProfile_matches_only_the_exact_profile_folder` (8 cases);
  - `A_browser_left_by_this_server_is_cleared_quickly_before_the_next_launch`;
  - `A_browser_owned_by_another_running_server_is_never_touched`;
  - `A_same_origin_frame_that_is_still_loading_is_reported_so_the_model_observes_again` (fixture `frame-loading.html`);
  - `Frames_not_read_counts_loading_and_failed_same_origin_frames_and_never_more_than_exist`;
  - the two same-origin frame tests (Browser and Integration) now wait, bounded, for the inner button instead of sleeping a fixed time.
- **Runs:** Browser project, Release: 150 total, 147 passed, 3 skipped, 0 failed. Full suite, Release and Debug: 1,182 total, 1,132 passed, 50 skipped, 0 failed each. `msedge.exe` count was 0 before and after every run.
