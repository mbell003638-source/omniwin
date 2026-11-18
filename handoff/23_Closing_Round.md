# OmniWin: closing round (from the reviewer's check of round 22)

**Reviewer's verdict on the round-22 report (2026-11-11):** most of it is real, but it is not finished, and a few claims are wrong. The check was done by reading code, artifacts, file times and hashes.
- **Verified true:**
  - All 8 published DLLs match the Release build, and no server source changed after the publish at 07:29.
  - The nine-task report: 160 of 160 passed, B07 not applicable 20 of 20.
  - The re-send limit is now one per character (A2).
  - The typing time limit scales with length, up to 120 s (A3).
  - `TryForeground(long)` is removed (A4).
  - The `WM_NULL` check before attaching (A5).
  - The remote-desktop skip list for F24 and the `key:F24` channel (A6).
  - Workflow `confirmed` only when every step is (B1).
  - Temp folders are removed per sample (C4).
  - The minimize test checks the real state (C6).
  - The OCR fixture window and the UI-lock thread fixes.
- **Not true, or not finished:**
  - **The click benchmark's "no change" case changed something** (focus moved), so "the 300 ms wait no longer shows" is wrong. The 300 ms ceiling is unchanged in the code.
  - **The Debug run used test binaries older than the last test edits,** and no test-result file exists for any run.
  - **Desktop engine items:**
    - A1 and A7 are tested only through small helper functions.
    - A long `type` blocks every other desktop call for up to 120 s.
    - The F24 tap is decided on one look at the front window.
  - **Harness:**
    - The metrics could almost never fire.
    - The Explorer check accepted any Explorer window.
    - B10 overwrote the owner's clipboard.
    - Long typing was checked with a scratch script that could pick the owner's Notepad.

**What the reviewer fixed itself (2026-11-11), so it is not in the builder's list:**
- **Harness, `bench/OmniWin.Bench`:**
  - **Click benchmark:** it now measures a real no-change click and records `no_visible_change`.
  - **Report fields:** real `started` and `finished` times, and `remote_session`.
  - **B07:** `not_applicable` only when Windows reports Remote Desktop.
  - **`wrong_target`:** catches any change to the owner's existing Notepad, Explorer and Settings windows.
  - **`input_sent_mismatch`:** checked on every call.
  - **`false_success`:** works on early failures too.
  - **The sample's own windows:** a window is adopted only as the single new one.
  - **Explorer checks:** they use the sample's own window only.
  - **Explorer path failures:** a failure after OmniWin typed the path counts as `failed`.
  - **Leftovers and counts:**
    - The leftover-window list is always written.
    - Each sample's data folder is removed.
    - Harness calls are not counted as task calls.
  - **B10:** saves and puts back the owner's clipboard.
  - **New `typing` command:** replaces the scratch script.
- **Tests:**
  - **Clipboard live test:** puts the clipboard back, also when it was empty.
  - **UI lock:** one shared `tests/Shared/UiLockHolder.cs` (the two duplicate copies were removed), with its timeout path fixed.
  - **New `workflow_run` replay test (B2).**
- **Docs:**
  - Contract §15 corrected.
  - `WS1.md`, `WS4.md`, `TOOLS.md` and `RUNNING_TESTS.md` brought up to date, including the `typing` command and how to save `.trx` results.
- **Checked:**
  - Debug and Release build with `-warnaserror`, with 0 warnings and 0 errors.
  - The affected no-input tests pass: the new replay test, the 32 workflow service tests and the 16 harness safety tests, with Edge at 0 before and after.
- **Reviewer's live check of the new harness (2026-11-11, 08:40–08:42, Remote Desktop):**
  - **`clicks-v2`, 10 clicks per case:**
    - "no_change" was 10 of 10 "No visible change.", with a whole call p50 of 323 ms and p95 of 338 ms. The 300 ms wait is real.
    - "expect_met" was 10 of 10 `confirmed`, with a p50 of 54 ms.
    - The fixture's own log saw every click.
  - **`typing-v1`:** F01, 300 characters, `confirmed`, read back exactly.
  - **`typing-v2`:** Notepad, 200 characters, `confirmed`, read back exactly. Only its own window was used, then emptied and closed.
  - **`2026-11-11-omniwin-tasks-v2.json`:**
    - B01, B04, B05 and B06 passed 2 of 2, with every metric 0.
    - B07 was `not_run`, because the owner's Settings window was already open, so no new window appeared. It was correctly not adopted, and nothing changed.
    - The Remote Desktop session then disconnected, and the run stopped as designed, with B07 #2 and B10 not run.
  - **Afterwards:** Edge 0, Notepad 0, no fixture or server left, and no new temp or data folders.
  - **Found and fixed during the check:** the click benchmark tried to remove its log while the fixture was still releasing it. It now retries.
  - **Not yet run live on the new code:** B10 (clipboard save and restore) and the clipboard live test.

Copy everything below the line and send it to the AI.

---

**Only the desktop engine items are left for you, plus the live run and the report. Use 1 agent for the desktop work plus you as lead.** The rules from handoffs 19, 21 and 22 still apply:
- one test run at a time;
- count `msedge.exe` before and after every run;
- never close or change anything of the owner's;
- never delete (tests may remove only the temp folders they created themselves);
- **no system or power setting changes;**
- **no scratch scripts for real input.** Everything that sends input runs through a command or test in the repo, and nothing under your scratch folder (`close_leftovers.py`, `long_typing.py`, `click_speed.py`) is run again.

**Don't change these files; the reviewer finished them:** `bench/*`, `tests/Shared/*`, `tests/OmniWin.Integration/Harness/*`, `tests/OmniWin.Integration/LiveSessionTests.cs`, `tests/OmniWin.Core.Tests/WorkflowTests.cs`, `docs/*`. Report anything wrong in them instead of changing them.

**Test only what your change can affect:**
- `tests/OmniWin.Desktop.Tests` after the desktop work;
- `tests/OmniWin.SystemTools.Tests` if `SystemToolsService.cs` changes (A11);
- then the live checks in section 3.

Don't rerun the whole suite.

## 1. Desktop (Agent A: `src/OmniWin.Desktop/*`, `tests/OmniWin.Desktop.Tests/*`)

- **A8. A1, finished.**
  - "Settled" must mean the text stayed unchanged for 500 ms: read back every 100 ms or so until two readbacks 500 ms apart are identical, with a hard cap of 2 s.
  - `confirmed` is allowed only if that stable text matches exactly. A changed text returns `contradicted`. No stable text within the cap returns `unverified`.
  - **Engine-level tests through `DesktopEngine`, not only `ResendOutcome`:**
    - a fake whose original character lands late, after the re-send and after the first matching readback, returns `contradicted`;
    - a fake whose original never lands returns `confirmed` only after the stable readback.
- **A9. A2 engine test.** A fake that refuses one character twice:
  - that character is sent exactly twice;
  - the act ends `error readback_mismatch` with `input_sent=true`.
- **A10. A long `type` must not block other calls.**
  - Today the whole text goes through one call on the desktop worker (`DesktopActions.cs` `Pump`), for up to 120 s. Every other observe, wait or act then fails with `engine_timeout` after 4 s.
  - Send the text in per-character worker calls, as the RichEdit path already does. While a `type` runs, another call must either be served between characters (reads) or get an immediate, clear `blocked` result saying a `type` is running (acts). Never a 4 s `engine_timeout`.
  - **Tests:**
    - a fake 2,000-character `type` through the engine ends `confirmed`, not just budget arithmetic;
    - an observe sent during it is answered.
  - Book the time spent sending characters under `dispatch`, not `precheck` (`EndPhase`, `DesktopEngine.cs` around 164–175).
  - When the typing limit runs out during the poll wait, report `engine_timeout`, not "(cancelled)" (`DesktopEngine.cs` around 304 and 316).
- **A11. F24, checked right before the tap.**
  - Check the front window again immediately before the tap: it must not be a forwarding client, and it must respond to `WM_NULL`. Today it is checked once before the attach and not again (`NativeDesktop.cs` around 334, 342, 363). If the check fails, no tap.
  - Put `BringToForeground` in `try/finally` like `Activate`, so a tap is never lost from the result when something throws after it.
  - Then let `window_manage focus` report the engine's real `InputSent` instead of always `true` (`SystemToolsService.cs` around 381).
  - Engine-level tests: the `key:F24` channel for `desktop_act` focus and for `BringToForeground`.
- **A12. A7, the rest.**
  - An engine-level D1 test: `set_value` on a fake file-dialog File name box whose readback matches returns `unverified`.
  - Per-character wait tests through a seam in `AwaitTypedScalar`: the caret moves, so the next character is sent; the caret is stuck, so the re-send happens and then the error.
- **Optional, same files:** the three inline mutexes in `Desktop.Tests` (`DesktopLiveTests.cs:43`, `DesktopBenchmarkTests.cs:41`, `DesktopPointerDiagnosticTests.cs:42`) can use the shared `tests/Shared/UiLockHolder.cs` (link it as the other test projects do).

## 2. Accuracy (lead): answer each one exactly

1. **`close_leftovers.py`** (created 2026-11-10 20:29, changed 20:35). It closes every Notepad, Settings and Calculator window, and empties unsaved Notepad tabs. Did you run it? When? Which windows did it close?
2. **`long_typing.py notepad`, run about 08:01 on 2026-11-11.** It typed into the first `windows_list` line containing "Notepad", then sent `ctrl+a`, `backspace`, `ctrl+w`. Which window was that? Was any other Notepad window open then? If it was the owner's, say so plainly.
3. **ForegroundLockTimeout.** What command or tool wrote 2,147,483,647 to the saved value at about 20:27 on 2026-11-10? `WS1.md` also shows `SPI_GETFOREGROUNDLOCKTIMEOUT` returning 2147483647 on 2026-11-09, a day earlier. Explain the live value against the saved value.
4. **The Debug run.** When did it run, and with `--no-build`? The Debug test binaries for OCR (02:55) and SystemTools (07:31) are older than the edits to those tests at 07:40 and 07:41. Also give the start time of the final Release run.
5. **Other files.** What wrote these on 2026-11-11?
   - `artifacts/benchmarks/ws2-observe-scan-cap-v1.json`, at 08:04;
   - four new `%TEMP%\omniwin-test-long-*` and `omniwin-test-smoke-*` folders.
   Don't remove the folders; the owner decides.
6. **Small corrections:**
   - `publish\OmniWin.Server.exe` ends `…BBA75`, not `…8BA75`;
   - there are 1,336 old test folders in `%TEMP%`, not about 1,118.

## 3. Build, targeted tests, live run and report (lead)

1. Build Debug and Release with `-warnaserror`.
2. **Run only the affected suites in Release, each with its build (no `--no-build`):**
   - `tests/OmniWin.Desktop.Tests`;
   - `tests/OmniWin.SystemTools.Tests`, if `SystemToolsService.cs` changed.
   Add `--results-directory artifacts/test-results/<date>-release --report-xunit-trx` (see `docs/RUNNING_TESTS.md` §5). The criteria are 0 failed, and Edge at 0 before and after.
3. Republish. Check: 26 tools, contract 2.4, exits when stdin closes. Hash all 8 DLLs and the exe.
4. **Live run.** The owner is on Remote Desktop: keep the session `Active`, and the owner leaves the mouse and keyboard alone. Save a `.trx` file for each test project.
   - `clicks --runs 40` gives a new `clicks-vN` (`clicks-v2` is the reviewer's 10-click check). The "no_change" case must show `no_visible_change` 40 of 40. Write its whole-call budget into contract §15.
   - `typing --target f01 --chars 1000`, then `--chars 2000`, then `typing --target notepad --chars 1000`. Each must show `readback_exact: true`.
   - Integration live: `LiveSessionTests` and `DesktopScenarioTests` (D1, F16, minimize/restore, clipboard). The clipboard test now restores the owner's clipboard through Win32. It has not run live yet, so report its result exactly.
   - Desktop.Tests live (with `OMNIWIN_LIVE_SESSION=1`), and task 10.
   - The nine tasks, 20 runs each, **last**. B10 has not run live with its new clipboard save and restore yet.
     - Report its first result exactly.
     - B07 is `not_run` while a Settings window is already open, which is correct: tell the owner and don't close it.
   Any failure gets fixed (in your files) and that part rerun. A harness failure is reported, not patched.
5. **Report.** Include:
   - Yes or No per item (A8–A12), each with its proving test;
   - exact counts with their `.trx` paths, and Edge counts;
   - the nine-task table (passed, failed, not run, not applicable, `wrong_target`, `input_sent_mismatch`, `false_success`, `leftover_windows`);
   - the `clicks-v2` per-phase numbers and the §15 budgets;
   - the three `typing` results;
   - every changed file;
   - all 9 hashes in full;
   - the answers to section 2, numbered 1–6.
   Then stop.
