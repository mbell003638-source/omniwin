# OmniWin: focus reporting fix (from the reviewer's check of round 23)

Round 23 is accepted (see `README.md`, entry 23). These are two small accuracy fixes in what `window_manage focus` reports. Neither one changes what input is sent.

## Rules
- Same rules as file 23: no scratch scripts for real input, no system or power setting changes, keep the Remote Desktop session `Active` for anything live (run `query user` first; if it is not `Active`, skip the live part and say so).
- **Don't change these files; the reviewer owns them:** `docs/*`, `bench/*`, `tests/Shared/*`, `tests/OmniWin.Integration/*`, `tests/OmniWin.Core.Tests/WorkflowTests.cs`, and the handoff files except your report. The reviewer updates the docs afterwards.

## 1. A stopped focus attempt reports what really happened
- **Today:** when a boundary check stops the attempt partway, `DesktopEngine.BringToForegroundDetailedAsync` returns `InputSent = true` whatever was sent. Examples of a boundary stop: a held modifier, a changed window, the secure desktop. The catch inside the worker call is at `DesktopEngine.cs` around 153–158. `window_manage focus` then reports `error foreground_failed`, "a focus attempt may have been made", and the real reason (for example `user_active`) is lost.
- **Wanted:**
  - Take `input_sent` from what the attempt recorded (`LastForegroundAttempt`, which is kept in `finally`), not `true`.
  - Carry the stop's status and reason through to `window_manage focus`:
    - nothing sent: the stop's own status and reason, for example `blocked user_active`, `input_sent=false`;
    - something sent before the stop: `error input_interrupted`, `input_sent=true`. This is the same rule as acts. A tapped F24 keeps channel `key:F24`.
  - A fault from the worker call itself (the outer catch, around 162–166) may keep `input_sent=true` when it cannot tell.
- You will need a reason or status on `ForegroundAttempt` (`src/OmniWin.Core/DesktopContracts.cs`). Keep the existing callers working.

## 2. A focus refused because a `type` is running says so
- **Today:** while a `type` holds the lane (`DesktopEngine.cs` around 141), focus returns `error foreground_failed`, "no input was sent", with no reason given.
- **Wanted:** `blocked rate_limit`, `input_sent=false`, with a detail saying a type action is running and to retry when it has finished. This is the reviewer's contract decision from round 23, and `desktop_act` already does the same.

## Tests (only these)
- **Desktop.Tests, engine level, with the fake backend:**
  - a boundary stop before any input gives the stop's reason with `input_sent=false`;
  - a boundary stop after a window input gives `error input_interrupted` with `input_sent=true`;
  - a stop after the F24 tap keeps `key:F24`;
  - a type holding the lane gives `blocked rate_limit` with `input_sent=false`.
- **SystemTools.Tests:** `window_manage focus` passes each of these through: status, reason, `input_sent` and channel.
- **Running them:**
  - Run Desktop.Tests and SystemTools.Tests in Release, each with its build, adding `--results-directory artifacts/test-results/2026-11-11-release --report-xunit-trx`. The criteria are 0 failed and Edge at 0 before and after.
  - Then republish, check 26 tools, contract 2.4 and a clean exit when stdin closes, and hash all 8 DLLs and the exe.
  - Live: only SystemTools.Tests with `OMNIWIN_LIVE_SESSION=1`, if the session is `Active`.
  - No nine tasks, clicks or typing runs. Nothing in those paths changes.

## Report
Save it to `OmniWin_Handoff/24_Builder_Report.md`. Include:
- Yes or No per item, each with its proving tests;
- exact counts with their `.trx` paths;
- Edge counts;
- every changed file;
- all 9 hashes in full.

Then send it to the reviewer session (`local_9f82a268-b253-4419-874f-fc9eca0f2932`) and stop.
