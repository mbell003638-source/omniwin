# Running the tests

Run from the repository root (`C:\Users\<you>\Desktop\OmniWin`). **One test run at a time on the whole machine.** The browser tests start OmniWin's own Edge; a hidden Edge left behind by a failed run slows the PC (this happened on 2026-11-09). The harness now stops its own browsers and fails the test if any is left, but still check the count.

## 1. No-input tests (safe anywhere, even in a disconnected session)

Pure unit tests: no browser, no server, no desktop, no real input.

```
dotnet test --project tests/OmniWin.Desktop.Tests -c Release
dotnet test --project tests/OmniWin.Core.Tests -c Release
dotnet test --project tests/OmniWin.Safety.Tests -c Release
dotnet test --project tests/OmniWin.Ocr.Tests -c Release
dotnet test --project tests/OmniWin.SystemTools.Tests -c Release
dotnet test --project tests/OmniWin.Integration -c Release --filter-class OmniWin.Integration.HarnessSafetyTests
```

(`--filter-class` takes the full class name; the Integration project's other classes start the server or a browser.)

## 2. Full suite (starts the server and the browser)

Needs no connected desktop: tests that need one **skip** with a message. Run it once, Release or Debug, and nothing else at the same time.

```
dotnet test --solution OmniWin.sln -c Release
```

Count Edge before and after. Count only processes started by the tests: their command line holds `omniwin` in the profile folder (`%TEMP%\omniwin-e2e\...` or `%TEMP%\omniwin-bench\...`), so your own Edge windows are not counted.

```
powershell -NoProfile -Command "(Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Where-Object { $_.CommandLine -match 'omniwin-(e2e|bench|test)' }).Count"
```

It must print `0` before the run and `0` after. If it does not, list and stop only the leftovers, by profile path:

```
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Where-Object { $_.CommandLine -match 'omniwin-(e2e|bench|test)' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"
```

This never touches an Edge whose command line lacks that profile path. Do not stop Edge by name (`taskkill /IM msedge.exe`): that closes your own windows.

If the PC becomes slow, stop the run (Ctrl+C) and run the cleanup above before anything else.

## 3. Live session (real mouse and keyboard; owner present)

Only with a **connected** desktop (an active Remote Desktop window or the console, unlocked). A disconnected session has no input desktop and a 640x480 placeholder screen, so these tests skip.

Before you start: Task Manager must not be the foreground window (it blocks focus changes), Explorer must respond (a slow Explorer makes every UI read time out), and leave the mouse and keyboard alone while it runs.

```
set "OMNIWIN_LIVE_SESSION=1" && dotnet test --project tests/OmniWin.Integration -c Release --filter-class OmniWin.Integration.LiveSessionTests
```

(PowerShell: `$env:OMNIWIN_LIVE_SESSION='1'; dotnet test --project tests/OmniWin.Integration -c Release --filter-class OmniWin.Integration.LiveSessionTests`. Use `--filter-class OmniWin.Integration.DesktopScenarioTests` for the fixture-app scenarios.)

Contract 2.6 (learning and selected text, F01 fixture and Notepad the tests open themselves; also writes `artifacts/benchmarks/learning-v1.json`):

```
set "OMNIWIN_LIVE_SESSION=1" && dotnet test --project tests/OmniWin.Integration -c Release --filter-class OmniWin.Integration.Contract26LiveTests
```

The same switch runs `Contract25LiveTests` (2.5). Count `msedge.exe` before and after; run `dotnet build-server shutdown` when done.

Stop key at any time: Ctrl+Alt+Shift+K (stops OmniWin input).

Do not touch the mouse or keyboard inside the Remote Desktop window during a live run. If the session was locked or disconnected, the harness refuses to start.

## 4. Benchmarks

Latency and accuracy gates, no real input (headless browser):

```
dotnet run --project bench/OmniWin.Bench -c Release -- e2e
```

The nine-task live comparison (real input, live session, owner present, same conditions as section 3). It refuses to start without `OMNIWIN_LIVE_SESSION=1` or without a connected desktop, writes a "not run: no connected desktop" report instead, and exits with code 3:

```
set "OMNIWIN_LIVE_SESSION=1" && dotnet run --project bench/OmniWin.Bench -c Release -- tasks --runs 20
```

To run only some tasks: `set "OMNIWIN_TASKS=B01,B02"` before the command. B07 is recorded as `not_applicable` under Remote Desktop (Windows disables the Scale box there).

Each sample is `passed`, `failed`, `not_run` (environment problem, such as `foreground_failed`; not counted in the success rate) or `not_applicable`, with `input_sent`, `false_success`, `wrong_target`, `input_sent_mismatch`, `leftover_windows` and `cleanup_errors` recorded. Reports go to `bench/reports\` under new names. Each sample removes only the `%TEMP%\OmniWin-test-*` folder it created itself. Two kinds of temp folders can be left from older runs and are yours to remove: `%TEMP%\OmniWin-test-*` (the nine-task harness) and `%TEMP%\omniwin-test-*` (live tests and scratch scripts, lower case).

Click speed, per phase (real input, same conditions; writes `artifacts\benchmarks\clicks-vN.json`):

```
set "OMNIWIN_LIVE_SESSION=1" && dotnet run --project bench/OmniWin.Bench -c Release -- clicks --runs 40
```

The "no_change" case runs in its own loop after one unmeasured warm-up click. Its `no_visible_change` count must equal its click count, or it did not measure a click that changes nothing.

Long typing, read back exactly (real input, same conditions; writes `artifacts\benchmarks\typing-vN.json`). `--target f01` types into the F01 fixture's Name box and reads the fixture's own log. `--target notepad` opens a new Notepad window (found by comparing the window lists before and after the launch), reads it back through Win32, then empties and closes only that window:

```
set "OMNIWIN_LIVE_SESSION=1" && dotnet run --project bench/OmniWin.Bench -c Release -- typing --target f01 --chars 1000
```

```
set "OMNIWIN_LIVE_SESSION=1" && dotnet run --project bench/OmniWin.Bench -c Release -- typing --target notepad --chars 1000
```

OmniWin's own tools against the two bridged engines (contract 2.5) on the same F01 jobs: click Agree, click Apply, and type 200 characters into Name. The fixture's own log is the judge. A Windows-MCP click or type is sent only if its screen point is on the fixture window. Writes `artifacts/benchmarks/compare-vN.json`. `--only omniwin|windows-mcp|agent-desktop` runs one way:

```
set "OMNIWIN_LIVE_SESSION=1" && dotnet run --project bench/OmniWin.Bench -c Release -- compare --runs 10
```

The benchmarks remove their own `%TEMP%\omniwin-bench\<id>` data folders and their own `%TEMP%\omniwin-test-clicks-*` / `omniwin-test-typing-*` logs by exact path. Never use a scratch script for anything that sends real input.

## 5. Saving test results

Every run that a report quotes saves its results. Never use `--no-build` for those runs, so the binaries are the final code:

```
dotnet test --solution OmniWin.sln -c Release --results-directory artifacts/test-results/2026-11-12-release --report-xunit-trx
```

Use the real date, and `debug` for the Debug run. Add the same `--results-directory ... --report-xunit-trx` to each live command in section 3. (`--report-xunit-trx` is the xunit v3 option on Microsoft.Testing.Platform; `dotnet test --project <one project> --help` lists it.)
