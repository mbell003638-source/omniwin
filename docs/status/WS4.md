# WS4 tests and benchmarks: status

Updated 2026-11-13. Contract 2.5. Sections written on 2026-11-08 sent no real input; the live sections (nine tasks, desktop scenarios, click speed) do, and say so.

**Current state (2026-11-13, contract 2.5, mixed engines; builder report `OmniWin_Handoff/25_Mixed_Engines_Builder_Report.md`).** Full suites on the final code, each with its build: Release and Debug each 1,437 total, 1,386 passed, 0 failed, 51 skipped (the usual 49 plus two `RealCaptureTests` that skip while Remote Desktop is disconnected); `.trx` in `artifacts/test-results/2026-11-13-final-release` and `-final-debug`; Edge 0 before and after. New test project `tests/OmniWin.Router.Tests` (80 tests: router, guide, engine catalog and filters, bridged results, power tools). The reviewer checked the summed `.trx` counts, that the runs started after the last source edit (22:17:18; tests from 22:17:55), and that all 9 published files equal their build and the report. **Live on 2026-11-14** (Remote Desktop active; `.trx` in `artifacts/test-results/2026-11-14-live` and `-live-drag`): the new `tests/OmniWin.Integration/Contract25LiveTests.cs`, 11 of 11. The first run was 10 of 11: the test wrongly expected a selection event for Item 1, which F01 has already selected. After the fix the drag tests passed 2 of 2. Note: the drag check (the list ends on Item 4, button up) would also pass for a plain click on Item 4; the press, move and release sequence itself is covered by unit tests. Each engine's acting click hit Apply exactly once by the fixture's own log. Nine tasks (`bench/reports/2026-11-13-omniwin-tasks-v1.json`): B01-B06, B09, B10 20 of 20, B07 not applicable over Remote Desktop, every bad-result count 0. `clicks-v4`: no-change 40 of 40 "No visible change.", whole call p95 345.3 ms; expectation met p95 64.8 ms. `typing-v6/7/8`: F01 1,000 and 2,000, Notepad 1,000, confirmed, `retry=done`, read back exactly. Not exercised live: `omniwin_do` pause, play and next on a real media session; engine stop-key and time-limit paths. Leftovers: each full run leaves two `omniwin-test-launch-*` browser-profile folders and fixture logs in `OmniWin-test-fixture` in `%TEMP%` (small; the reviewer moved them to the Recycle Bin on 2026-11-14). Harness files changed by the builder for the contract bump: `Harness/Contract.cs` (three new always-on tools; `engine_call` and the power tools optional) and `SurfaceTests.cs` (still fails if a power tool is present by default), `FormatTests.cs` (`retry=` in the status-line pattern).

**Native against the engines (reviewer, 2026-11-14, `bench compare`, live over Remote Desktop):** the same F01 jobs through the published server, judged by the fixture's own log. Times are look plus act, p50:

| Job | OmniWin's own tools | Windows-MCP | agent-desktop |
|---|---|---|---|
| Click Agree (visible change), 10 | 10 exact, 53 ms | 10 exact, 687 ms | 10 exact, 309 ms |
| Click Apply (nothing changes), 10 | 10 exact, 330 ms (waits 300 ms to check) | 10 exact, 683 ms | 10 exact, 304 ms |
| Type 200 characters, 3 | 3 exact, 1.61 s, `confirmed` | 3 exact, 1.58 s, `unverified` | 0 of 3: refused (`POLICY_DENIED`: headless mode denies the focus it needs) |
| First call (engine start) | 0.16 s | 21.4 s | 0.27 s |

Files: `compare-v1.json` (OmniWin and Windows-MCP), `compare-v2.json` (agent-desktop, 10 clicks per job), `compare-v3.json` (one-click diagnostic that captured agent-desktop's typing refusal). No stray clicks and no false success in any way; engine results are always `unverified`, so a careful caller looks again after each one. agent-desktop's refusal comes back as `error input_sent=true retry=check_state`: cautious but not exact, because OmniWin cannot tell that the engine sent nothing. Only standard controls in one test app were measured; apps where OmniWin itself sees little were not. Follow-up the same day: the `engines` topic of `omniwin_guide` claimed that agent-desktop can type in headless mode. It now gives the measured order and speeds, says to use `desktop_set_value` for text with agent-desktop, and tells the model never to use an engine for something OmniWin refused (`src/OmniWin.Server/Tools/GuideTopics.cs`; the `start` topic gained step 4, "an engine only when none of these can do the job"). Server built in Debug and Release (0 warnings), Router.Tests 80 of 80 (`artifacts/test-results/2026-11-14-guide`), republished. Smoke on the published copy: 30 tools, no power tools, contract 2.5, the new text served, clean exit. Only `OmniWin.Server.dll` changed (`98AF91405F299CF550300B3D4783ACA0A497FED5693823477EE20E10205A68D7`); the other 7 DLLs and the exe are unchanged.

**Previous state (2026-11-11, handoff 23, closing round).** Only Desktop and SystemTools changed, so only they were rerun (Release, with build, `.trx` in `artifacts/test-results/2026-11-11-release/`): Desktop.Tests 380 total, 375 passed, 0 failed, 5 skipped; SystemTools.Tests 168, 165 passed, 0 failed, 3 skipped. Live over Remote Desktop: `LiveSessionTests` 3 passed, 2 placeholders skipped, including `Clipboard_set_then_get_round_trips_and_the_original_text_is_put_back` (first live run of the Win32 save and restore); `DesktopScenarioTests` 27 of 27; `DesktopLiveTests` 1 of 1; SystemTools live 167 passed, 1 skipped. Nine tasks (`bench/reports/2026-11-11-omniwin-tasks-v3.json`): B01-B06, B09 and B10 20 of 20 each (B10's first live run with its clipboard save and restore), every bad-result count 0; B07 `not_run` 20 of 20 because the owner's own Settings window was open (it was not adopted or closed). `clicks-v3` (40 per case): no-change 40 of 40 "No visible change.", whole server call p50 321 ms, p95 339 ms; expectation met p95 64 ms. `typing-v3`/`-v4`/`-v5`: F01 1,000 and 2,000 characters and Notepad 1,000, all `confirmed` and read back exactly. Edge 0 before and after; the reviewer found no new temp folders and no leftover processes afterwards.

**Previous state (2026-11-11, after the final fix round, handoff 22).** Last full suite run (2026-11-11, final code): Release and Debug each 1,242 total, 1,193 passed, 0 failed, 49 skipped; `msedge.exe` 0 before and after. Live (`OMNIWIN_LIVE_SESSION=1`, Remote Desktop, published build of 2026-11-11): the Integration project 143 tests, 141 passed, 0 failed, 2 skipped (the placeholders); SystemTools 162 passed, 0 failed (run twice); OCR 86 total, 84 passed, 0 failed. The nine-task harness on the final code (`bench/reports/2026-11-11-omniwin-tasks-v1.json`): B01-B06, B09 and B10 passed 20 of 20 each (160 of 160), B07 not applicable 20 of 20 (over Remote Desktop Windows disables the Scale box); false success 0, wrong target 0, input_sent mismatch 0, leftover windows 0, no Notepad/Calculator/Edge left afterwards. Long typing (published server, real input): 1,000 and 2,000 characters into the F01 editor were `confirmed` and the fixture's own log held the exact text (7.8 ms per character); 1,000 characters into Notepad `confirmed` (11 ms per character) and its window was closed. Click speed: `artifacts/benchmarks/clicks-v1.json` (40 + 40 clicks; whole server call p95 about 84 ms; per-phase numbers and the budgets set from them are in contract §15). Two test bugs were found and fixed on 2026-11-11: the OCR default-view test started `notepad.exe` (a stub on Windows 11) and could not close its window, so it now uses its own fixture window; the OCR and SystemTools live tests released the shared UI lock from another thread after an await (`ReleaseMutex` failed), so they now hold it on one dedicated thread.

## What exists

- **Stdio harness** (`tests/OmniWin.Integration/Harness/`): `McpServer` starts the Release or Debug `OmniWin.Server.exe` with `--test-mode`, a throwaway data dir and `OMNIWIN_BROWSER_HEADLESS=1`, speaks MCP over stdio, and enforces the **stdout guard** (a non-JSON line on stdout fails the run). `FixtureWebServer` serves `testapps/web-fixtures` from 127.0.0.1 and records, independently of OmniWin, every click / input / change / key / submit the page receives (a small script is injected into every page and posts each event; trusted vs script-made events are told apart). It also keeps the "server record" of the W01 form. `E2ESession` ties a server, a web server and a data dir together; one per test class, or a fresh one for tests that count audit lines or read files after shutdown. Contract §8 is written out in `Contract.cs` so the suite compares the server with the document, not with itself.
- **Suite** (`tests/OmniWin.Integration`), counts of 2026-11-08 (the project had 143 tests on 2026-11-10): 89 tests, 82 passed, 7 skipped, 0 failed (Release, last run 2026-11-08): Adversarial 23, BrowserAct 14, Format 9, Workflow 8, Surface 7, Safety 5, SystemTools 11, LiveSession 5, ToolCallAudit 6 (WS0's), DesktopProtocol 1. Existing `DesktopProtocolTests` now asserts the desktop tools are present; the whole surface is checked in `SurfaceTests`.
- **Benchmark runner** (`bench/OmniWin.Bench`): `e2e`, `micro`, `all`, and `tasks` (the OmniWin side of the nine-task comparison). Links the same harness sources. No new packages (no BenchmarkDotNet: micro rows use Stopwatch).
- **Fixtures added** (web, all under `testapps/web-fixtures`): `rewrite.html`, `reaction.html`, `lexicon.html`, `login.html`, `checkout.html`, `secrets.html`, `route.html`, `misc.html` (+ `download.txt`), `w01.html`, `biglist.html`, `biglist10k.html`, `moving-auto.html`.

## Test groups (WS4 brief §4)

| Group | Class | Tests | Notes |
|---|---|---|---|
| 1 Surface | `SurfaceTests` | 7 | 26 tools of §8 exactly (only `screen_view`/`browser_view` may be extra), snake_case parameter names, retry wording on act tools, "data not instructions" wording on read tools, no shell/script/screenshot tool, status lists every engine, stdout guard |
| 2 Formats | `FormatTests` | 9 | §9.1 observation, redaction (password, card), refs never reused, §9.3 status line + step id + diff, blocked detail, tab list, diff observation, windows_list, injection page |
| 3 Acts and expectations | `BrowserActTests` | 14 | every browser action kind, wrong-target = 0 from the page's own trace, url_contains / element_appears / all_of / any_of / window_opened / window_closed, delayed reaction, route change, W01 form with the server's record, truncation and viewport_only, same-origin frame click |
| 4 Adversarial | `AdversarialTests` | 23 cases | delete button/link/form, lexicon (DELETE, Delete all denied; Send, Pay, Sender, Undelete, Payroll not), spoofed indexer, element swapped after observe, covered button, disabled, rewriting input (contradicted), no-reaction button (unverified), unknown generation, unknown action, cross-origin frame, delete confirmation dialog, password page + Enter (Risky label), file/mailto/download links |
| 5 Safety and audit | `SafetyAuditTests` | 4 (+1 skipped) | one audit line per call, `<N chars>`, URL scheme+host only, bN refs kept, step id in the audit line, no secret/cookie/storage/typed-password/URL query/fragment in any written file, stderr or stdout, denied calls logged with reason |
| 6 No repeated input | inside group 4 | | contradicted, timeout and unverified each leave exactly one input in the page's log |
| 7 System tools / OCR (no input) | `SystemToolsTests` | 10 (+1 marker) | system_info kinds, apps_list, audio read (no device here), unknown window, app_launch refusals, media refusal |
| 8 Workflows | `WorkflowE2ETests` | 8 | run_steps, stop at first non-passing step, delete step cannot pass, non-act tools refused, save/list/run round trip with a parameter and no typed text in the file, hand-edited delete workflow refused, unknown step id, wait_for routing |
| Phase 5 (tagged) | `LiveSessionTests`, one marker in `SystemToolsTests` | 6 skipped | see below |

### Skipped tests and why
- `LiveSessionTests` (5): `Audio_set_reads_back_and_is_restored`, `Clipboard_set_then_get_round_trips_and_the_original_text_is_put_back`, `Windows_list_shows_real_windows_and_a_window_manage_minimize_restore_round_trip_works`, `Screen_ocr_of_a_fixture_window_an_oN_click_and_the_ten_second_stale_rule`, `Stop_key_cancels_in_flight_work_and_later_acts_return_denied_kill_switch`. They change the real volume, clipboard or windows, or need the user's physical keyboard. Trait `Category=RealInput`; skipped unless `OMNIWIN_LIVE_SESSION=1`. When forced, the OCR and stop-key ones skip with a pointer: OCR is covered by the two OCR tests in `DesktopScenarioTests`, and the stop key is on the manual checklist.
- `SystemToolsTests.Live_audio_set_and_restore_window_manage_clipboard_round_trip_screen_ocr_and_ocr_ref_click`: a marker so group 7's live part stays visible.
- `SafetyAuditTests.Default_rate_limit_denies_with_rate_limit`: finding 3.

## Commands
- **No-input tests (also run by `dotnet test OmniWin.sln`):** `dotnet test --project tests/OmniWin.Integration -c Release --nologo --no-build` (build first; Microsoft Testing Platform syntax)
- **Phase 5 tagged tests (owner connected over Remote Desktop with the session `Active`, hands off the mouse and keyboard; hold `Local\OmniWin.UiInputTestLock`):**
  `set OMNIWIN_LIVE_SESSION=1` then `dotnet test --project tests/OmniWin.Integration -c Release --filter-class OmniWin.Integration.LiveSessionTests` (the filter was checked: without the variable all 5 report skipped)
- **Benchmarks, no real input:** `dotnet run --project bench/OmniWin.Bench -c Release -- all` (or `e2e`, `micro`). This is the one command form; `docs/RUNNING_TESTS.md` uses the same. Writes new numbered files to `artifacts/benchmarks/` and `bench/reports/`, never over an old one.
- **Click speed, per phase (the D2 benchmark):** `set "OMNIWIN_LIVE_SESSION=1" && dotnet run --project bench/OmniWin.Bench -c Release -- clicks --runs 40` (real input). It clicks the F01 fixture through the published server in two cases, "expect_met" (Agree checkbox with a `toggle_is` expectation, no fixed wait) and "no_change" (repeated Apply clicks in their own loop after one unmeasured warm-up click, so nothing changes and each act waits the 300 ms settle ceiling), and writes `artifacts/benchmarks/clicks-vN.json` with p50/p95/max for resolve, precheck, dispatch and verify, the fixture's own count of clicks that arrived, and how many results said "No visible change.". **Run on 2026-11-11 (`clicks-v1.json`)**: whole server call p95 about 84 ms; the numbers and the budgets set from them are in contract §15. Correction (reviewer, 2026-11-11): in `clicks-v1` every Apply click came right after an Agree click, so focus moved and the wait ended at once; it did not measure a click that changes nothing, and the 300 ms ceiling is still in the code. The command was fixed the same day. `clicks-v2` (reviewer, 10 clicks per case, 08:40) shows 10 of 10 "No visible change." with a whole server call p50 of 323 ms and p95 of 338 ms; the click with an expectation met had a p50 of 54 ms. **Reviewer live check of the new harness, 2026-11-11 08:40–08:42, Remote Desktop:**
- `typing-v1`: F01, 300 characters, `confirmed`, read back exactly, 8.1 ms per character.
- `typing-v2`: Notepad, 200 characters, `confirmed`, read back exactly, 10.2 ms per character; the window was emptied and closed, and nothing was left.
- `2026-11-11-omniwin-tasks-v2.json`, two runs each:
  - B01, B04, B05 and B06 passed 2 of 2, every metric 0, and no data folder was left.
  - B07 #1 was `not_run`, because the owner's Settings window was already open, so no new window appeared. It was correctly not adopted, and no change was seen.
  - The Remote Desktop session then disconnected, so the run stopped as designed, with B07 #2 and B10 not run.
- B10 and the clipboard live test are not yet run on the new code.
- **D2, first measurement (2026-11-10, scratch script, superseded by the command above): measured live over Remote Desktop (Windows 11 build 10.0.26300, no physical display).** 40 real `desktop_act click` calls on the F01 fixture button "Apply" through the published server: all 40 reached the button (the fixture's own log shows 40 Apply events); server time p50 324 ms, p95 336 ms, max 354 ms; client round trip p50 329 ms, p95 340 ms, max 360 ms. A 400 ms p95 budget was written from this, but about 300 ms of each of these clicks is the fixed wait for a change that never comes (a click on Apply changes nothing), so it does not measure the click itself. Script: scratchpad `click_speed.py` (uses only the MCP server and the fixture).
- **The nine tasks (OmniWin side), real input:** `set OMNIWIN_LIVE_SESSION=1` then `dotnet run --project bench/OmniWin.Bench -c Release -- tasks --runs 20`. Each sample creates `%TEMP%\OmniWin-test-<task>-<id>` and removes only that folder itself when it ends; a folder it could not remove is named in the report. Folders left by runs before 2026-11-11 stay for the owner. Harness (final, 2026-11-11): only the windows a sample opened itself, and a dialog one of them owns, are closed; any other new window is left alone and listed (`other_new_windows_not_touched`); only the Notepad window the sample opened and checked empty may be emptied; File Explorer tasks use a new window only and send nothing unless it is in the sample's own folder; each sample records `false_success`, `wrong_target`, `input_sent_mismatch`, `leftover_windows` and `cleanup_errors`. Reviewer changes on 2026-11-11, applied before the next run:
- **`wrong_target`:** set by the task's own folder check, or by any change to an existing window of the driven apps during the sample: a changed title, an existing Explorer window in another folder, or an existing Notepad window whose text hash changed. The evidence is in `wrong_target_evidence` (ids only, no titles or text).
- **`input_sent_mismatch`:** also set by any call whose status is `denied`, `blocked` or `stale` while it reported `input_sent=true` (`input_sent_mismatch_calls`).
- **`false_success` on early failures:** an early failure uses the server's status of the last effect call instead of `false`.
- **Which windows are the sample's own:** a window is adopted only as the single new candidate (app_launch reports no process id), and Settings needs "Settings" in its title.
- **Explorer checks:** the folder and selection checks look only at the sample's own Explorer window, by its Shell handle.
- **Explorer path failures:** a failure after OmniWin typed the Explorer path is `failed`, not `not_run`.
- **B10 clipboard:** B10 saves the owner's clipboard text through Win32 and puts it back. When the clipboard holds non-text content, B10 is `not_run` and touches nothing. Other formats of the same copy, such as HTML, are not put back.
- **Leftovers and counts:**
  - Each sample removes its own `%TEMP%\omniwin-bench\<id>` data folder (`data_folder_left` otherwise).
  - `tool_calls_per_run` counts only task calls.

Rules that already applied: window ids are snapshotted at sample start; the run stops after 3 consecutive samples that cannot use the desktop; it refuses to start when the screen cannot be reached (`DesktopAvailability` also checks `GetCursorPos`). `OMNIWIN_TASKS=B01,B02` restricts which tasks run. B02 sends keys to the Save As dialog window id (not the Notepad window). B07 under Remote Desktop: Windows disables the Scale box and shows no percentage, so B07 is recorded as `not_applicable` (a fourth state, separate from passed, failed and not run; the owner uses Remote Desktop only). Task 10 passed 9/9 live. B08 (setting change) is dropped. B06 selects through the list item's own Select action and the selection is read independently through the Shell. **Results on record (v29, 2026-11-10, made with the older harness code):** B01, B02, B03, B04, B05, B06, B09 and B10 passed 20 of 20; B07 was 1 failed and 19 not run (the failure was a refused click on a covered item; a later B07-only run was 20 of 20 not run). **Results on the harness of 2026-11-11 morning (`2026-11-11-omniwin-tasks-v1.json`, run about 07:42–07:58):** B01–B06, B09 and B10 passed 20 of 20, B07 not applicable 20 of 20; every counted metric 0. Its `started` field is really the write time (fixed afterwards: the report now has `started`, `finished` and `remote_session`). The reviewer changed the harness again on 2026-11-11 (B07 is `not_applicable` only when Windows reports a Remote Desktop session; the leftover-window list is written even when closing fails), so the next run is the one on the final harness code.

## Measurements (Release, headless Edge 157.0.4341.42, this PC)
Valid run: `artifacts/benchmarks/ws4-e2e-v3.json`; report `bench/reports/2026-11-08-phase2-v3.md`. Earlier runs `ws4-e2e-v1.json` and `ws4-e2e-v2.json` are kept but not valid for four rows (the metrics block was not parsed: n/a in v1, empty in v2 after a first fix attempt). Micro: `ws4-micro-v1.json`. Warm-up 20, then 200 samples (launch 5, cold 50, workflow 60). Every act sample reached input (`input_sent` true 200/200 in each act row; statuses `confirmed` for set_value and type, `unverified` for click).

| §15 row | Compared number (artifact key) | p50 | p95 | Budget | Result |
|---|---|---|---|---|---|
| browser_observe live, 150 elements | server metric `browser_observe` (`rows[browser_observe_live_150].server_metrics`) | 33.8 | 45.4 | 10 | **over** |
| browser_act set_value | server total without gate (`server_without_gate`) | 29.069 | 40.247 | 10 | **over** |
| browser_act click | same | 31.968 | 45.844 | 10 | **over** |
| browser_act type | same | 23.755 | 39.844 | 10 | **over** |
| windows_list | server metric `windows_list` | 0.059 | 0.11 | 5 | pass |
| audio (no-device path) | server metric `audio` | 0.106 | 0.159 | 5 | pass |
| workflow_run per step | server metric `workflow_run_step` | 31.2 | 38.3 | 20 | **over** |
| tool-call overhead | client round trip of `omniwin_status` | 0.425 | 0.596 | 1 (server only) | report only: finding 7 |
| browser_observe cold / launch to first observe | client round trip | 35.639 / 1047.285 | 55.859 / 1202.016 | report | report only |

Browser misses are the known, owner-accepted slow path on bigger pages (verify re-scans up to 150 elements). Not measured here, all needing a visible window or real input: desktop observe/act/input rows, `screen_ocr`, `media`, `window_manage`.

**Accuracy gates over a 20-run browser set (B09, `accuracy` in the artifact):** wrong-target 0 of 120 steps; `input_sent` mismatches 0; false confirmed 0; 20 of 20 runs fully as expected (120 of 120 steps). Adversarial cases: the 23 tests in `AdversarialTests` all pass. The desktop halves of these gates are recorded per sample by the nine-task harness (see "How to run").

**Micro (µs per operation, `ws4-micro-v1.json`):** Quote x150 + lines p50 41.2 / p95 191.5; x500 p50 55.8 / p95 117.8; `Format(ActResult)` 1.4 / 2.6; `FormatTabs` x10 2.1 / 4.0; expectation JSON parse 3.5 / 9.1.

## Fixture mapping (WS4 brief F01-F17 / W01-W10)
| ID | Where | State |
|---|---|---|
| F01-F17 | `testapps/OmniWin.FixtureApp` | **Built** (`--scenario F01` … `F17`, `F05-COVER`; see `Scenarios/ScenarioRunner.cs`). The text that follows describes 2026-11-08, when only a basic window existed. Web analogues cover F09/W04 (`lexicon.html`), F10 (`controls.html`), F11 (`rewrite.html`), F12 (`reaction.html`), F13 (`moving-auto.html`), F15 (`login.html`), F17 (`untrusted.html`). |
| W01 | `w01.html` (+ `controls.html`) | built, with the server-side form record |
| W02 / W03 | `login.html` / `checkout.html` | built |
| W04 | `lexicon.html`, `delete.html` | built |
| W05 | `untrusted.html` | existing |
| W06 | `frames.html`, `frame-inner.html`, `frames-cross.html` | built (second origin is `localhost` vs `127.0.0.1`) |
| W07 | `route.html`, `dynamic.html` | built |
| W08 | `dialogs.html` | existing |
| W09 | `biglist.html` (2,000 rows), `large.html` (201), `biglist10k.html` (10,000, for finding 6) | built |
| W10 | `misc.html`, `download.txt` | built; the mailto and download links are listed and never followed |

## Findings (product behaviour the tests exposed; each one says whether it is fixed)
1. Fixed: `omniwin_status` prints `contract=2.4 milestone=M5-release`. (On 2026-11-08 it printed `contract=2.3 milestone=M1-WS1`.)
2. A saved workflow stores the full page URL and, for container elements, long page text as selector `name`s (for example a whole form's label text). No typed text is stored, but page text and the URL (with port, and query if any) are.
3. `rate_limit` is in the config but nothing enforces it; `denied rate_limit` is unreachable. The brief's rate-limit test is skipped. Since handoff 23 the code has one use only: `blocked rate_limit` with `input_sent=false` for an act that arrives while a desktop `type` is running (tested in `TypingEngineTests`).
4. `browser_observe` / `browser_tabs` text uses CRLF line endings; act results and other tools use LF. The harness normalizes it.
5. The `no visible change` detail promised for a click that changes nothing never appears: every scan advances the generation, so the engine's check is always false. The result is correctly `unverified`.
6. `browser_observe` on a page with 10,000 buttons returns `status=error reason=engine_timeout` after 30 s (seen in the first suite run, the test took 1 minute); 2,000 buttons work. The scan walks every element before the `max_elements` cut. It also left the engine slow for the next tests in that run.
7. The "tool-call overhead in server <= 1 ms" row cannot be measured separately; only the client round trip (0.4-0.6 ms) is visible. A server-side metric for it would be needed.
8. Superseded: the scenarios F01-F17 are built and the desktop scenario tests ran live on 2026-11-10. (On 2026-11-08: desktop fixture scenarios F01-F17 are not built, so the desktop halves of groups 3-7 and the OCR `oN` click test cannot run in Phase 5 yet. This is the main open work item.
9. `cross_origin_frame` is never returned as a result code: cross-origin frame contents are not indexed, so nothing inside can be named. The observation lists the frame as `cross_origin` instead (the approved behaviour).

## Reason codes
Covered by a test: `policy_forbidden`, `occluded_same_window`, `disabled`, `not_found`, `readback_mismatch`, `modal_blocking`, `unsupported_action`, `engine_timeout` (seen incidentally), `focus_failed` and others in `OmniWin.Browser.Tests`. Manual or Phase 5: `kill_switch`, `user_active`, `elevated_target`, `occluded_by_window`, `focus_changed`, `foreground_failed`. `rate_limit` only as `blocked` while a `type` runs (`TypingEngineTests`). Unreachable under the owner's policy or today's code: `denied rate_limit`, `user_denied`, `confirm_timeout` (no confirmation prompts), `cross_origin_frame` (finding 9), `secure_desktop`.

## Manual checklist (the owner runs it; record the date and result here)
- `blocked user_active` from real input (contract §13.3).
- Refusal to start elevated and `blocked elevated_target` (contract §5).
- Stop key (Ctrl+Alt+Shift+K) cancels a running act; the next act returns `denied kill_switch`; restart the server after.
- Dropped by the owner's decisions: the confirm dialog items (the dialog does not exist), 150 % display scale items (a setting change).

## Log
- 2026-11-08: harness, 89-test suite, benchmark runner, fixtures, report. Failed runs: the first full suite run had 11 failures, all test or fixture mistakes except findings 4, 5 and 6 (CRLF, the missing detail, the 10,000-row timeout); one build error from a backslash in a string; two benchmark runs with a metrics-parsing bug (v1, v2). No product code was edited. The first suite run also left the browser busy on the 10,000-row page, which made later tests in that class fail; the page was cut to 2,000 rows.

## Desktop fixture scenarios F01 to F17 (built 2026-11-08, finding 8)

`testapps/OmniWin.FixtureApp` now has all scenarios. With no `--scenario` it is the original window (the one the desktop benchmarks and live tests use), unchanged. Nothing was launched or clicked while building them: the only run was the unknown-scenario check, which exits at once and shows no window.

**Start one:** `OmniWin.FixtureApp.exe --scenario F09 --title "my title" --log C:\path\f09.jsonl --stdin-commands`. It prints `OMNIWIN_FIXTURE_READY`, then `OMNIWIN_FIXTURE_LOG <path>`. An unknown scenario exits with code 2 and lists the known ones on stderr.

**Ground truth:** every input a control really receives is one JSON line in the log (`t`, `scenario`, `control`, `event`, `value`, `detail`). Default path `%TEMP%\OmniWin-test-fixture\<scenario>-<pid>.jsonl` (a file the app creates; nothing else is ever deleted). Control ids are the controls' `Name` (the UIA AutomationId). Password scenarios log only `len=N sha=xxxxxxxx`, never the value.

**Stdin commands** (`--stdin-commands`, one per line, run on the UI thread, so no UI input is needed): F04 `open`; F08 `scroll N`; F10 `scroll_far`; F12 `open_later`; F13 `replace`, `move`, `scroll`; F14 `steal`, `stop` (or `--steal-after-ms N`). Anything else is logged as `unknown_command`.

| ID | Window / controls (AutomationId) | What it logs |
|---|---|---|
| F01 | WinForms: Name (edit), Apply, Agree, ChoiceA/B, Fruit (combo), Level (slider), Items (list) | click, text_changed, checked, selected, value |
| F02 | WPF, same controls and ids | same |
| F03 | WinForms, 500 controls: Field001..250 (edits), Go003.. (buttons) in a scrolling panel | click, text_changed |
| F04 | Main action, Open dialog; modal `FixtureModalDialog` (DialogEntry, CloseDialog) | click, dialog_opened, dialog_closed |
| F05 | Target, Free; `--scenario F05-COVER --rect L,T,W,H` is a second process: borderless topmost window over the Target (the first process prints `OMNIWIN_FIXTURE_RECT L,T,W,H`) | click per control; `CoverLabel` click if the cover is hit |
| F06 | Covered ("Covered target") under panel Shield; Open ("Uncovered control") | click on the button or on Shield |
| F07 | BigTree: 5,000 nodes, 30 levels deep (spine "Level 0..29" plus leaves) | expanded, collapsed, selected |
| F08 | WPF list Rows: 100,000 rows, recycling virtualization | click (the row the click really landed on), selected, scrolled_to |
| F09 | Send, Delete, Pay, Delete all, DELETE, Sender, Undelete, Payroll (ids B09_1..8, neutral, so only the name decides) | click with a running count |
| F10 | DisabledAction (disabled), Visible, FarBelow (scrolled out of view in panel Scroller) | click, scrolled |
| F11 | Rewriter: uppercases and cuts input at 5 characters; Other | typed (raw), rewritten |
| F12 | OpenLater (new window "Later" after 1.5 s), NeverReacts | click, timer_started, later_window_opened |
| F13 | Target (replaced / moved by commands), ScrolledTarget in ScrollHost, three trigger buttons | click, replaced, moved, scrolled |
| F14 | Editor, Apply; a second window "Thief" takes the foreground every 400 ms after `steal` | text_changed, foreground_taken |
| F15 | WinFormsSecret (password char) and WpfSecret (WPF PasswordBox in an ElementHost), Submit | text_changed (length and hash only), initial_secrets |
| F16 | Unicode: multi-line edit (typing-order live test added 2026-11-10; bug fixed, see WS1) | text_changed (exact text, code points) |
| F17 | Instruction-like labels; buttons Quoted, Multiline, Header whose names hold quotes, a backslash and line breaks; Plain | click |

W01 to W10 are the web fixtures mapped earlier in this file; nothing changed there.

### Desktop tests (groups 2 to 7), `tests/OmniWin.Integration/DesktopScenarioTests.cs` and `DesktopFixture.cs`
- `DesktopFixtureCatalogTests` (1 test, runs now, no input): the app knows F01 to F17 and rejects an unknown scenario with exit code 2 and no window.
- `DesktopScenarioTests` (27 tests including the 8 F09 cases and the F16 typing-order test), all `Category=RealInput`, all skipped unless `OMNIWIN_LIVE_SESSION=1`. Each starts its scenario (a real window) under `Local\OmniWin.UiInputTestLock`, drives it through the real MCP server, and compares every result with the scenario's own log: F01, F02, F03, F04, F05 (second process), F06, F07, F08 (recycled row is stale), F09 (denied: Delete, DELETE, Delete all; clicked once: Send, Pay, Sender, Undelete, Payroll), F10, F11 (contradicted, one input only), F12 (expectation met; unverified, one input), F13 (replaced, moved and scrolled), F14, F15 (redaction, no secret in any written file), F16 (exact text, `unicode_typing`), F17 (no forged line), OCR of F01 then a click on its `oN` ref, and the 10-second `oN` stale check.
- **Run live on 2026-11-10** (Release, over Remote Desktop): 27 of 27 passed; the whole Integration project with the live switch on was 143 tests, 140 passed, 3 failed (the minimize/restore test, since changed to use its own window and to check the minimized state, and two placeholders that are now skips). The builder reports a live rerun on 2026-11-11 (Integration 143 tests, 141 passed, 2 skipped, so the changed minimize/restore test passed), but no test-result file was saved for it; from the next round every run saves a `.trx` file under `artifacts/test-results/`. Before that date: they compile and are skipped. The expectations come from the contract and the briefs; the first live run may show that a status or reason differs from what is written here. Treat each failure as a finding to look at, not as proof either way.

**Run in Phase 5:** set `OMNIWIN_LIVE_SESSION=1`, then `dotnet test --project tests/OmniWin.Integration -c Release --filter-class OmniWin.Integration.DesktopScenarioTests`. The server is started by the tests themselves; the owner stays off the mouse and keyboard.

Not built: nothing from F01 to F17. The brief's "F05 and F09 repeated at 150 % display scale" stays dropped (it is a display setting change).
