# OmniWin: builder report for handoff 23 (closing round)

Date: 2026-11-11. Times below are local (EST). The Remote Desktop session was `Disc` at the very start and `Active` for every live part (`query user` was run before each one). Edge count was 0 before and after every run. Nothing of the owner's was closed or changed by the live parts; B07 was `not_run` because the owner's Settings window is open (see the table).

Everything that sends real input ran through a command or test in the repo. No scratch script sent input. `smoke23.py` (scratch) only starts the server, lists tools and closes stdin; it sends no input and keeps its data folder in the scratch folder, not `%TEMP%`.

## 1. Items A8–A12

| Item | Done | Proving tests (all in `tests/OmniWin.Desktop.Tests`, `.trx` below) |
|---|---|---|
| **A8** settled readback | **Yes** | `TypingEngineTests`: `A_character_whose_original_lands_late_after_the_resend_and_the_first_matching_readback_is_contradicted` (through `DesktopEngine`: result `contradicted`, `readback_mismatch`); `A_character_whose_original_never_lands_is_confirmed_only_after_the_text_stayed_unchanged_for_500_ms` (`confirmed`, at least 6 readbacks, at least 480 ms); `A_resent_character_whose_text_cannot_be_read_back_is_unverified_never_confirmed`; `Text_typed_without_any_resend_needs_no_settle_wait`; and four tests of `StableReadbackAsync` (stable, changes restart the wait, never stable, unreadable) |
| **A9** refused twice | **Yes** | `A_character_the_editor_refuses_twice_is_sent_exactly_twice_and_the_act_ends_in_error_readback_mismatch` (editor saw `b` exactly twice, `c` never; status `error`, reason `readback_mismatch`, `input_sent=true`); pump-level: `A_stuck_caret_sends_the_character_once_more_and_the_second_miss_ends_the_act_with_readback_mismatch` |
| **A10** long `type` | **Yes** (engine level; not exercised live while another call ran) | `A_two_thousand_character_type_through_the_engine_is_confirmed_and_booked_as_dispatch_time`; `An_observe_sent_during_a_long_type_is_answered_between_characters` (answered in under 1 s while the type was still running); `An_act_sent_during_a_long_type_gets_an_immediate_blocked_result_that_says_a_type_is_running` (a click and a second type, both `blocked`, `input_sent=false`, under 1 s); `The_lane_is_free_again_after_the_type_ends`; `When_the_typing_limit_runs_out_the_act_reports_engine_timeout_not_cancelled` |
| **A11** F24 | **Yes**, with the limits below | `ForegroundAndPumpTests`: tap decided again immediately before the tap (`TapAllowedNow`: ordinary window yes; forwarding client no; hung window no; a front window that changes between two looks is judged on the second); `A_boundary_that_throws_after_the_F24_tap_does_not_hide_the_tap`; `A_focus_that_tapped_F24_is_reported_by_BringToForeground_with_the_key_and_input`; `A_desktop_act_focus_through_the_engine_returns_the_key_f24_channel_and_input_sent`; `A_stop_after_the_F24_tap_still_counts_the_tap_as_sent_input`. In `tests/OmniWin.SystemTools.Tests/WindowManageTests.cs`: focus reports the service's real `input_sent` (4 new tests) |
| **A12** D1, per-character wait | **Yes** | D1 through the engine: `Setting_or_clearing_a_file_dialogs_file_name_box_is_unverified_even_when_the_readback_matches` (`set_value` and `clear`), plus the two controls (an ordinary edit stays `confirmed`; `type` on the File name box stays `confirmed`). Per-character: `When_the_caret_moves_each_step_sends_the_next_character...`, `A_step_never_sends_more_than_one_character`, `A_native_edit_waits_for_the_scalar_after_each_character...`, the two stuck-caret tests, and three `AwaitTypedScalar_*` tests through the new seam |

### What changed (behaviour you should know about)
- **A10:** the typing pump (`TypingPump` in `TypingPump.cs`) now does at most one native action per worker call, so the single UI Automation worker is free between characters. Typing speed is unchanged live (7.7 ms per character on F01).
  - Reads (observe, wait, windows_list, describe) are served between characters.
  - A physical act or a second `type` arriving while a `type` holds the lane gets `blocked` at once, with detail "A type action is running ...". **Reason code: I used `rate_limit`**, because `ReasonCodes.cs` says new codes need a proposal. A dedicated code (for example `type_in_progress`) needs your decision.
  - `window_manage focus` (via `BringToForegroundDetailedAsync`) during a type returns not verified, nothing sent, at once.
  - The time spent sending characters is now booked under `input` (dispatch). `typing-v3.json`: precheck 11.5 ms, input 7,651.7 ms.
  - If the typing limit runs out, the act ends `error engine_timeout`, `input_sent=true`, detail "the typing time limit of N s ran out".
- **A9:** a character refused twice now ends the act as `error readback_mismatch` (before, the engine turned the blocked fault into `input_interrupted`). Docs that say `input_interrupted` for this case are now out of date (docs are yours).
- **A8:** "settled" = the readback matched continuously for 500 ms, read about every 100 ms, cap 2 s. The stable text decides: exact match `confirmed`; a different stable text `contradicted`; not stable or unreadable `unverified`. Only acts that re-sent a character use it.
- **A11:** `window_manage focus` now reports the engine's `input_sent`. This means a focus on a window that was already in front is `confirmed` with `input_sent=false` and no channel (before: always `true`/`api`). A service that can only answer yes/no, or a call cancelled during the attempt, keeps `input_sent=true`.
- **Seam:** `DesktopEngine` can now run against a fake backend (`IDesktopBackend`, an internal constructor). The real `UiaDesktop` implements it; its methods that the interface needs became `public` on an internal class. No production behaviour change from that.

### Limits (what is not proven)
- `TryForeground` itself (native) is not driven by any test: the tap re-check is covered through `TapAllowedNow` and the accounting through `RunForegroundAttempt`. The `try/finally` in `UiaDesktop.BringToForeground` is covered by reading, and by the engine test for a throw after a tap (fake backend).
- A10 was not run live with a second call during a real 1,000-character type.
- `AwaitTypedScalar` on a stuck caret returns quietly after its time (as before); the re-send then-error path belongs to the packet-editor path, which is what the stuck-caret pump tests cover.
- The optional switch of the three inline mutexes in `Desktop.Tests` to the shared `UiLockHolder` was **not done**: those tests keep the whole scenario on one thread, so they do not have the `ReleaseMutex` bug.
- Debug was built (0 warnings, 0 errors) but **no Debug test run was made** this round (you asked for Release only).
- OCR live and the Desktop diagnostics/benchmark tests were not run (code untouched; they need other switches and an idle desktop).

## 2. Counts and `.trx` files (Release, each with its build, no `--no-build`; `artifacts/test-results/2026-11-11-release/`)

| Run | total | passed | failed | skipped | `.trx` |
|---|---|---|---|---|---|
| Desktop.Tests (final) | 380 | 375 | 0 | 5 | `<user>_<pc>_2026-11-11_15_20_09.357158.trx` |
| SystemTools.Tests (final) | 168 | 165 | 0 | 3 | `<user>_<pc>_2026-11-11_15_21_45.722457.trx` |
| Integration live `LiveSessionTests` | 5 | 3 | 0 | 2 (the two placeholders) | `<user>_<pc>_2026-11-11_15_25_27.953554.trx` |
| Integration live `DesktopScenarioTests` | 27 | 27 | 0 | 0 | `<user>_<pc>_2026-11-11_15_25_32.810196.trx` |
| Desktop.Tests live `DesktopLiveTests` (`OMNIWIN_LIVE_SESSION=1`, `OMNIWIN_UI_TESTS=1`) | 1 | 1 | 0 | 0 | `<user>_<pc>_2026-11-11_15_26_34.558051.trx` |
| SystemTools.Tests with `OMNIWIN_LIVE_SESSION=1` | 168 | 167 | 0 | 1 (`Measurements`) | `<user>_<pc>_2026-11-11_15_26_41.209648.trx` |

Two `.trx` files in that folder are **superseded and were not deleted**: `..._15_19_00.700094.trx` (first Desktop run, before I shortened two slow tests; also 380/375/0/5) and `..._15_20_57.886076.trx` (first SystemTools run: 2 failed, see below).

- **The 2 failures in that first SystemTools run:** one was my test design (it cancelled the token before the call, so nothing had been sent, which the service reported correctly). The other was real: `window_manage focus` returned `Done(...)`, which always says `input_sent=true`, even when the engine said nothing was sent. Fixed in `SystemToolsService.cs`; the final run is clean.
- The Integration live results include the clipboard test **`Clipboard_set_then_get_round_trips_and_the_original_text_is_put_back`: Passed (live, first run on the new code)** and `Windows_list_shows_real_windows_and_a_window_manage_minimize_restore_round_trip_works`: Passed. F16 (`F16_first_typing...` and `F16_cjk_emoji...`) passed. D1 has no live test in the Integration project; it is exercised by the nine tasks (B02 Save As, B04/B05 Explorer paths), which passed.
- Build: Debug and Release of `OmniWin.sln` with `-warnaserror`: 0 warnings, 0 errors.
- Publish: `dotnet publish src/OmniWin.Server -c Release -o publish` at 10:22. Smoke (`smoke23.py`): 26 tools, `contract=2.4`, exit code 0 when stdin closed, no process left.

## 3. Nine tasks (`bench/reports/2026-11-11-omniwin-tasks-v3.json`, 20 runs each, 10:27 to 10:43, `remote_session: true`, `test_folders_left_for_owner: []`)

| Task | passed | failed | not run | not applicable | wrong_target | input_sent_mismatch | false_success | leftover_windows | wall p50 / p95 (s) |
|---|---|---|---|---|---|---|---|---|---|
| B01 Notepad type | 20 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 3.05 / 3.18 |
| B02 Save As | 20 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 6.07 / 6.43 |
| B03 select all, clear | 20 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 2.79 / 3.76 |
| B04 Explorer list | 20 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 5.60 / 6.06 |
| B05 create and rename folder | 20 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 6.41 / 7.10 |
| B06 select in 2,000 files | 20 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 7.48 / 7.54 |
| **B07 Settings display scale** | 0 | 0 | **20** | 0 | 0 | 0 | 0 | 0 | 11.7 / 11.8 |
| B09 Edge form | 20 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1.88 / 2.20 |
| **B10 Notepad to Edge (clipboard)** | 20 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 3.98 / 4.97 |

- **B07 is `not_run` 20 of 20 ("Settings opened no new window")**, as you predicted: the owner's Settings window (ApplicationFrameHost "Settings") is open, so no new window appeared. It was correctly not adopted and not closed. **Tell the owner:** B07 cannot run until they close their own Settings window (and it is `not_applicable` anyway when Windows reports Remote Desktop, but the reviewer's rule put `not_run` first here). I did not touch it.
- **B10, first live result on the new clipboard save/restore: 20 of 20 passed.** I did not independently read the owner's clipboard before and after; the harness does the save and restore (reviewer's code), and the clipboard live test passed.
- 160 of 160 on the eight tasks that ran. Edge 0 and Notepad 0 afterwards; no fixture or server process left.

## 4. Click speed (`artifacts/benchmarks/clicks-v3.json`, 40 clicks per case; `clicks-v2` is yours)

`no_change`: 40 x `unverified`, **`no_visible_change` 40 of 40**, `input_sent` 40, fixture log 41 events (one warm-up). `expect_met`: 40 x `confirmed`, fixture log 40. p50 / p95 / max in ms:

| Phase | expectation met | changes nothing |
|---|---|---|
| resolve | 0 / 0 / 1.2 | 0 / 0 / 0 |
| precheck | 13.3 / 17.9 / 28.3 | 13.1 / 14.6 / 15.7 |
| gate | 0 / 0 / 5.8 | 0 / 0 / 0 |
| dispatch | 0.6 / 14.0 / 32.6 | 0.6 / 17.8 / 30.2 |
| verify | 32.6 / 48.1 / 53.7 | 307.6 / 314.1 / 314.5 |
| whole server call | 47.1 / 63.9 / 122.0 | 321.3 / 338.7 / 353.1 |
| client round trip | 49.7 / 66.7 / 146.4 | 324.5 / 342.4 / 356.1 |

Contract §15 (`OmniWin_Handoff/02_Architecture_and_Contracts.md`) now holds these numbers and the budgets (p95): pre-checks <= 20 ms, dispatch <= 40 ms; a click whose effect is seen: verify <= 60 ms, whole call <= 120 ms; a click with no expectation and no visible change: verify <= 320 ms, **whole call <= 360 ms**. One run of 40 per case.

## 5. Long typing (`typing-v3`, `-v4`, `-v5`)

| Target | chars | status | `readback_exact` | readback chars | wall | per char | phases |
|---|---|---|---|---|---|---|---|
| F01 | 1,000 | confirmed | **true** | 1,000 | 7.73 s | 7.73 ms | precheck 11.5, input 7,651.7, verify 38.4 |
| F01 | 2,000 | confirmed | **true** | 2,000 | 15.50 s | 7.75 ms | precheck 11.9, input 15,378.2, verify 80.8 |
| Notepad (its own new window) | 1,000 | confirmed | **true** | 1,000 | 10.82 s | 10.82 ms | precheck 9.3, input 10,123.1, verify 671.0; `wrong_target_evidence`, `input_sent_mismatch_calls`, `leftover_windows`, `cleanup_errors` all empty |

## 6. Every changed file (this round, by me)

Source:
- `src/OmniWin.Desktop/DesktopEngine.cs` (backend seam, A8 `StableReadbackAsync`, A10 phases, lane check, typing-limit `engine_timeout`, A11 throw handling)
- `src/OmniWin.Desktop/DesktopActions.cs` (pump moved out, `RunForegroundAttempt`, `BringToForeground` try/finally, public members for the interface)
- `src/OmniWin.Desktop/TypingPump.cs` (new: `TypingSeams`, `TypingPump`, `TypingSession`)
- `src/OmniWin.Desktop/IDesktopBackend.cs` (new)
- `src/OmniWin.Desktop/ActLanes.cs` (holder label, `TryEnterPhysical`)
- `src/OmniWin.Desktop/NativeDesktop.cs` (`FrontWindow`, `TapAllowedNow`, tap re-check, `AwaitTypedScalar` overload)
- `src/OmniWin.Desktop/UiaDesktop.cs`, `DesktopVerification.cs` (members made `public` for the interface; `UiaDesktop : IDesktopBackend`)
- `src/OmniWin.Desktop/UiaWorker.cs`, `UiaEvents.cs` (a `createAutomation` switch so a test engine creates no UI Automation object)
- `src/OmniWin.SystemTools/SystemToolsService.cs` (focus reports the real `input_sent`)

Tests: `tests/OmniWin.Desktop.Tests/EngineFakes.cs` (new), `TypingEngineTests.cs` (new), `ForegroundAndPumpTests.cs` (new); `tests/OmniWin.SystemTools.Tests/Fakes.cs`, `WindowManageTests.cs`.

Docs: `OmniWin_Handoff/02_Architecture_and_Contracts.md` §15 (click budgets). I did not change `docs/*`, `bench/*`, `tests/Shared/*`, `tests/OmniWin.Integration/*` or `tests/OmniWin.Core.Tests/WorkflowTests.cs`.

Found, not patched (docs are yours): the contract and status docs still say a refused character ends as `input_interrupted`; and `docs/status/WS4.md` / `WS1.md` still describe the `precheck` accounting of a long `type` as an open finding.

## 7. Hashes (SHA-256 of `publish\`, 2026-11-11 10:22; each DLL equals its Release build output)

| File | SHA-256 |
|---|---|
| OmniWin.Browser.dll | 70CECD46684EE8027ED687DE44E99DCCEF3041997C04C795B3BF4ED2347EF083 |
| OmniWin.Core.dll | F160560233508AA31A673617A61DBEDA82F74544910813F05C7873547C984F7C |
| OmniWin.Safety.dll | FB32B1B9F865F568652A1C446A156F934E51F28792452F26B09B5A7CBC5996A0 |
| OmniWin.Desktop.dll | 185E8A608CB3F514B63AFA99C315FE736932C8C423E5D67E6D46CCCD77B91988 |
| OmniWin.Server.dll | 6EE33EEBB2D0C790F42F31AFC575C53070BFEFBFCDB3859231F160AC8A66425F |
| OmniWin.SystemTools.dll | A58648F028461A64F5A3BBD43799CF0B3AA8A5C2BF4758DF0373C29744A49F2F |
| OmniWin.Ocr.dll | 168048E75F7E4D4B00F897F4981B35CDFFDEA91C5F30906D2593E79EBDE64508 |
| OmniWin.Workflows.dll | 0D781C2BB4C0E54361B1E9CDAF8F9D1F029E21EE7412C808BF0CB00D6135A8D4 |
| publish\OmniWin.Server.exe | 30AED1E984758B6DAE89D3BCE95E3D732AD9BAD2B49DDEA092DD6493CF8BBA75 |

## 8. Section 2 answers (my transcript was read for these; its timestamps are UTC, converted here to EST = UTC-5)

1. **`close_leftovers.py`: yes, I ran it twice.**
   - Run 1: 2026-11-10 20:29:37. It matched windows by process name (`Notepad.exe`, `SystemSettings.exe`, `ApplicationFrameHost.exe`, `CalculatorApp.exe`), **not by who started them**. It found five Notepad windows: w2 and w3 titled "*saved by B02 - Notepad", w4 "*Untitled - Notepad", and w7 and w8 "Save as" dialogs. It pressed Escape in the two "Save as" dialogs; for the three unsaved tabs it sent `ctrl+a backspace` (emptying their text) and then `window_manage close`. All three closes answered `confirmed`. Afterwards only Taskbar, Claude and Antigravity were left. The B02 titles are the harness's own files. I believed all five came from my B02/B07 runs, but **I did not prove that for "*Untitled - Notepad" (w4)**; if that was the owner's unsaved tab, its text was emptied.
   - Run 2: 20:35:39, a changed copy. It launched Notepad itself (`app_launch`); Windows restored a tab "b02.txt - Notepad", which it closed with `ctrl+w`; the follow-up `close` answered `not_found` (already gone).
   - It is not run again.
2. **`long_typing.py notepad`, 2026-11-11 08:01:20.** It launched Notepad through `app_launch` and typed into the first `windows_list` line containing "Notepad": **w4, `Notepad.exe pid=80216`**, titled "*the quick brown fox jumps over the lazy dog ... - Notepad" after typing, which is the window it had launched (the other windows were w1 explorer, w2 Claude, w3 Antigravity). It then sent `ctrl+a`, `backspace`, `ctrl+w` to it. I did **not** record a Notepad window count before the run (only after, which was 0), so I cannot prove no other Notepad window of the owner's was open; the script's first-match selection would have picked one if it had listed first. The evidence says it was my own launched window, not the owner's.
3. **`ForegroundLockTimeout`.** On 2026-11-10 08:12 the owner said "Yes set it please" and I ran `SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001)` with flags 0, which changes the live value for the session only and writes nothing to the registry (live 2147483647 to 200000). **At 20:27 on 2026-11-10 I ran `Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ForegroundLockTimeout -Value 2147483647 -Type DWord`** (description "Restore lock timeout registry value"). I meant to put back the original *live* value; the command instead wrote the **saved (registry) value**, which was 200000 (I read it at 20:26). That was my mistake, and the owner did not approve a registry write. Live and saved are both 2147483647 now (read-only check today at 10:29). The live value was 2147483647 at the start of each login (the owner's login items probably set it, as the WS1 note of 2026-11-09 shows); the saved value had been 200000 until my write. I have not touched it again. To put the saved value back, the owner can decide to run: `Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ForegroundLockTimeout -Value 200000 -Type DWord`.
4. **The Debug run.** I ran `dotnet build -c Debug` at about 07:31:42 on 2026-11-11 and then `dotnet test --solution -c Debug --no-build` straight after (07:31 to about 07:33). So yes, `--no-build`, and it ran **before** the 07:40 and 07:41 test edits; the OCR (02:55) and SystemTools (07:31) Debug test binaries are older than those edits, as you say. No `.trx` was saved for it. The final full Release run started at **08:03:23** (build, then `dotnet test --solution -c Release --no-build`); earlier full Release runs started 07:26:36 and 07:29:51. This round I did not run Debug tests.
5. **Other files.**
   - `artifacts/benchmarks/ws2-observe-scan-cap-v1.json` (08:04:02) is written by the test `tests/OmniWin.Browser.Tests/BrowserScanCapTests.cs:109` during the full Release suite that started 08:03:23. It is a by-product of the suite, not a separate action.
   - The `omniwin-test-long-*` folders (08:01:01, 08:01:32, 08:01:59) came from the three runs of my scratch `long_typing.py`; `omniwin-test-smoke-99awf_vz` (07:34:05) came from my scratch `smoke.py`. I removed none of them. (Two older `omniwin-test-smoke-*` folders, 2026-11-10 22:13 and 22:52, are also from `smoke.py`.)
6. **Corrections accepted.** `publish\OmniWin.Server.exe` ends `...BBA75` (confirmed above); the old test folders are **1,336** (count of `%TEMP%\omniwin-test-*` today, case-insensitive). I did not touch them.

## 9. Left for the owner
- Close their own Settings window if they want B07 to run (I will not).
- Decide about the saved `ForegroundLockTimeout` (item 3 above).
- The 1,336 old `%TEMP%\omniwin-test-*` folders and the two superseded `.trx` files.
- Power settings (unchanged by me).
