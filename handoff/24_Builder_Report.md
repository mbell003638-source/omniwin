# OmniWin: builder report for handoff 24 (focus reporting fix)

Date: 2026-11-11. The Remote Desktop session was `Active` when I checked before the live part (`query user`). Edge was 0 before and after every run. Neither fix changes what input is sent.

## Items

| Item | Done | Proving tests |
|---|---|---|
| **1. A stopped focus attempt reports what really happened** | **Yes** | Desktop.Tests (engine level, fake backend), `ForegroundAndPumpTests`: `A_boundary_stop_before_any_input_reports_the_stops_reason_and_input_sent_false` (blocked `user_active`, `InputSent=false`); `A_boundary_stop_after_a_window_input_reports_input_sent_true_without_the_key_and_with_the_stop`; `A_boundary_stop_after_the_F24_tap_keeps_the_key_and_the_stop` (`KeyTapped=true`, `InputSent=true`, stop `secure_desktop`); the earlier `A_boundary_that_throws_after_the_F24_tap_does_not_hide_the_tap` still passes. SystemTools.Tests (`WindowManageTests`): `A_focus_stopped_before_any_input_passes_the_stops_status_and_reason_through_with_input_sent_false` (`blocked user_active`, `input_sent=false`, no channel, the stop's detail); `A_focus_stopped_after_a_window_input_is_error_input_interrupted_with_input_sent_true_on_the_api_channel`; `A_focus_stopped_after_the_F24_tap_keeps_the_key_channel` (`error input_interrupted`, `input_sent=true`, `key:F24`); `A_focus_that_failed_with_no_stop_still_reports_foreground_failed` |
| **2. A focus refused because a `type` is running says so** | **Yes** | Desktop.Tests: `A_focus_while_a_type_holds_the_lane_is_blocked_rate_limit_with_nothing_sent` (a real 100-character fake type still running; `StopStatus=Blocked`, `StopReason=rate_limit`, `InputSent=false`, detail mentions the type; the type itself then ends `confirmed`). SystemTools.Tests: `A_focus_refused_because_a_type_is_running_is_blocked_rate_limit_with_input_sent_false` (`blocked rate_limit`, `input_sent=false`, detail says a type is running) |

### How it works
- `ForegroundAttempt` (`DesktopContracts.cs`) got three optional trailing members: `StopStatus`, `StopReason`, `StopDetail`. The three-argument form still compiles and means "no stop", so every existing caller works unchanged.
- `DesktopEngine.BringToForegroundDetailedAsync`:
  - The catch inside the worker call now returns `InputSent` and `KeyTapped` from what the attempt recorded (`LastForegroundAttempt`, written in `finally`), not `true`, plus the fault's own status, reason and message.
  - A type holding the lane returns `Blocked`, `rate_limit`, with a detail saying a type is running and to retry when it has finished.
  - The early refusal (kill switch or recent user input) now also carries its reason (`kill_switch` as `denied`, `user_active` as `blocked`) instead of a bare "not verified". This is a small addition beyond the file's two items; it follows the same "say the real reason" rule and sends nothing.
  - The outer catch (the worker call itself failed) is unchanged: it keeps `InputSent=true` because it cannot tell.
- `SystemToolsService` `window_manage focus`, when the attempt was not verified and carries a stop:
  - nothing sent: the stop's own status and reason, `input_sent=false`;
  - something sent: `error input_interrupted`, `input_sent=true`, channel `key:F24` if the key was tapped, otherwise `api`, with the stop's reason in the detail.
  - Without a stop (the plain failure case) the behaviour from round 23 is unchanged (`error foreground_failed`).

## Counts and `.trx` (Release, each with its build, no `--no-build`; `artifacts/test-results/2026-11-11-release/`)

| Run | total | passed | failed | skipped | `.trx` |
|---|---|---|---|---|---|
| Desktop.Tests | 384 | 379 | 0 | 5 | `<user>_<pc>_2026-11-11_16_01_13.037605.trx` |
| SystemTools.Tests | 173 | 170 | 0 | 3 | `<user>_<pc>_2026-11-11_16_01_28.900792.trx` |
| SystemTools.Tests live (`OMNIWIN_LIVE_SESSION=1`, session `Active`) | 173 | 172 | 0 | 1 (`Measurements`) | `<user>_<pc>_2026-11-11_16_02_35.135818.trx` |

Edge: 0 before and 0 after each of the three runs. Debug and Release builds of `OmniWin.sln` with `-warnaserror`: 0 warnings, 0 errors. No nine tasks, clicks or typing runs (as asked), no full suite.

Republished (`publish\`) after the final code. Smoke: 26 tools, `contract=2.4`, exit code 0 when stdin closed, no OmniWin process left.

## Changed files
- `src/OmniWin.Core/DesktopContracts.cs`
- `src/OmniWin.Desktop/DesktopEngine.cs`
- `src/OmniWin.SystemTools/SystemToolsService.cs`
- `tests/OmniWin.Desktop.Tests/ForegroundAndPumpTests.cs`
- `tests/OmniWin.SystemTools.Tests/Fakes.cs`
- `tests/OmniWin.SystemTools.Tests/WindowManageTests.cs`

I did not change `docs/*`, `bench/*`, `tests/Shared/*`, `tests/OmniWin.Integration/*`, `tests/OmniWin.Core.Tests/WorkflowTests.cs` or any handoff file except this report. (Your pinned assert in `TypingEngineTests` is in the test run above and passed.)

## Hashes (SHA-256 of `publish\`; each DLL equals its Release build output). All eight DLLs differ from round 23 because `OmniWin.Core.dll` changed and the others were rebuilt against it.

| File | SHA-256 |
|---|---|
| OmniWin.Browser.dll | C58AE6007C07DBEE4BD5FCCA1194594ABBEB88BB700C001251A7CB6050D3CD2E |
| OmniWin.Core.dll | 834FE91078810487C5171CE33CA93D8558E2D93DFBA46443DF24988D6C5A49C6 |
| OmniWin.Safety.dll | B05173BD3C6E084E82B60C42E92202188FAEDF1736D18CDFBD19DC6688CDE106 |
| OmniWin.Desktop.dll | 7DD42462673A3BF548160140049516C37A4D3098EF6C779A4333218D8864ADEF |
| OmniWin.Server.dll | A2514E248FC73CF93B55C82E6169865F5DA32B2B7A57DD280437EB2AF7866DB6 |
| OmniWin.SystemTools.dll | 433C36C1028884467C5BC6C0AFD499E7CE32ACC1C28E837A7E1F2C238511CF31 |
| OmniWin.Ocr.dll | 9BF7BA6C0B36C7778522859E879FD1E0E0B243AD805A36207BB9C3741E742E33 |
| OmniWin.Workflows.dll | B010B58720889043228527EA0B6D9F4232787C5F4BE5AA1AD96A4A8415CB9562 |
| publish\OmniWin.Server.exe | 30AED1E984758B6DAE89D3BCE95E3D732AD9BAD2B49DDEA092DD6493CF8BBA75 |

## Limits
- The stop paths are proven at engine level with a fake backend and at service level with a fake foreground service. A real boundary stop in `TryForeground` (a held modifier or secure desktop during a real focus attempt) was not provoked live.
- The kill-switch / user-active early refusal (the addition above) has no test of its own; the engine tests do not run with an activity fake.
