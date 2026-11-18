# OmniWin: final fix round (from the reviewer's check of the last report)

**Reviewer's verdict on the last report (2026-11-10):** most of it is real.
- **Verified true:**
  - hashes and publish;
  - the full suite (1,198 total, 1,149 passed, 0 failed, with Edge at 0 before and after);
  - D1 recognises controls in any language;
  - the focus fallback order and its detach;
  - no system setting is changed in the code.
- **Not true, or not finished:**
  - **The new typing fix can still report a false `confirmed`.**
  - **The test harness can erase the owner's Notepad text and close the owner's windows.** The erase part is fixed by the reviewer; closing windows the sample didn't open is item C1.
  - The nine-task results were made with older harness code.
  - The click budget mostly measures a fixed wait.
  - Several statements in the report were inaccurate (listed in section 6).

Copy everything below the line and send it to the AI.

---

**Fix everything below in one go, with 4 agents plus you as lead, then send one short, exact report.** The rules from handoffs 19 and 21 still apply:
- one test run at a time;
- count `msedge.exe` before and after every run;
- never close or change anything of the owner's;
- never delete (tests may remove only the temp folders they created themselves);
- **no system or power setting changes.**

## 0. Already done by the reviewer (keep, don't redo)

- **`window_manage` window ids** (`SystemToolsService.cs`, `IsWindowId`): a `wN` that no longer exists returns `blocked not_found`. It never falls back to a process or title match, which could have closed "Draw50.png" for "w5". New tests are in `WindowManageTests`.
- **Harness, `Tasks.cs` `CleanupAsync`:** only the Notepad window the sample opened and checked empty (`NotepadWindow`) may get `ctrl+a backspace` or be closed. Any other new Notepad window, such as one where Notepad restored the owner's text, is left untouched.
- **`LiveSessionTests`:** the two deliberate `Assert.Fail` placeholders are now `Assert.Skip` with a pointer to where each check really happens.
- **The contract** (`02_Architecture_and_Contracts.md`) is updated for typing, F16, the focus fallback, `window_manage`, D2 and T3.
- Debug and Release build with `-warnaserror`. Tests were not run. `SystemTools.dll` changed, so **republish** at the end.

## 1. Split the work

| Agent | Owns (only these files) | Does |
|---|---|---|
| **A: desktop** | `src/OmniWin.Desktop/*`, `tests/OmniWin.Desktop.Tests/*` | A1–A7 |
| **B: workflows** | `src/OmniWin.Workflows/*`, `tests/OmniWin.Workflows.Tests/*` (or wherever the workflow tests are) | B1 |
| **C: harness** | `bench/*`, `tests/OmniWin.Integration/*` | C1–C7 |
| **D: docs** | `docs/*`, including the status files for this round | D1 |
| **Lead** | shared files, the contract, `README.md` | Merging, all test runs, publishing, the whole live run, the report |

**No agent starts a browser, sends real input, or runs anything other than its own project's no-input tests, and only one agent runs tests at a time.**

## 2. Desktop (Agent A)

- **A1. No false `confirmed` after a Notepad re-send.**
  - Today, if the original character arrives late and the re-send arrives too, the last check can pass before the second copy lands, and the act reports `confirmed` (`DesktopActions.cs` around lines 213–236, `DesktopEngine.cs` around lines 289–315 and 444).
  - Rule: when any re-send happened in an act, `confirmed` is allowed only if a readback taken **after the text has stayed unchanged for at least 500 ms** matches exactly. Otherwise return `contradicted` (wrong text) or `unverified`.
  - Test it with a fake that delivers the original late, after the re-send.
- **A2. Re-send at most once per character.** The limit is `resends < 3` per command and never resets (`DesktopActions.cs:217`), so one character can be sent 4 times. Make it one re-send per character, and an error if the character still isn't taken. Test it.
- **A3. Long text must finish.**
  - Typing now waits after every character, but the whole act still has a fixed 4 s dispatch limit (`DesktopEngine.cs:46`), so long text can stop partway with `input_interrupted`.
  - Scale the limit with the text length, keeping a hard upper bound so nothing waits forever.
  - Test with a fake for 2,000 characters. Live (step 5): type 1,000 characters into Notepad and into the F01 edit box, both reading back exactly.
- **A4. Remove the unused `TryForeground(long)` overload** (`NativeDesktop.cs:273`). It runs the F24 path with no checks.
- **A5. Bound the thread attach.** Before attaching to the foreground window's input queue, check that the window responds (a short `SendMessageTimeout` with `WM_NULL`). If it doesn't respond, skip the attach and the F24 step. A hung app must never block the UI Automation worker. Test with a fake hung window.
- **A6. F24 tap, safer and honest.**
  - Skip the tap when the window in front belongs to a remote-desktop or virtual-machine client (`mstsc.exe`, `msrdc.exe`, `vmconnect.exe`, `VirtualBoxVM.exe`, `vmware.exe`, `vmware-vmx.exe`), because those forward the key to another computer.
  - Record the channel as a key press (for example `key:F24`), not `api`.
  - `BringToForeground` must keep the input-sent flag, not drop it (`DesktopActions.cs:332`).
  - Add tests.
- **A7. Missing tests:**
  - the D1 downgrade inside the engine (a matching readback on a file-dialog box returns `unverified`);
  - a non-English Explorer address bar recognised by its id or class;
  - the per-character wait (caret moves, caret stuck), with fakes.

## 3. Workflows (Agent B)

- **B1.** Replaying a recorded workflow counts a step that ended `unverified` with a visible change as passed, and the whole run can then report `confirmed` (`StepRecorder.cs` around lines 29–30, `WorkflowService.cs` around lines 222 and 319–322). So a D1 `set_value` step can still end in a "confirmed" workflow.
  - Rule: a run is `confirmed` only if every step was `confirmed`. Otherwise the run is `unverified`, and the result names the steps.
  - Add tests.

## 4. Harness and live tests (Agent C)

- **C1. Close only the sample's own windows.**
  - Today cleanup closes any new window from Notepad, Settings, Calculator or Explorer (`Tasks.cs` around lines 62–74), even one the owner opened during the sample.
  - Track the windows each sample opens: its own launches, plus dialogs owned by those windows. Close only those. Any other new window is left alone and listed in the report.
- **C2. B05 and B06 work only in their own test folder.**
  - If Explorer did not reach the sample's test folder, the sample is `not_run` and sends nothing. Today the result of that check is ignored (`Tasks.cs` around line 545).
  - Never fall back to the owner's existing File Explorer window (`EnsureExplorer`, around lines 383–384): open a new one, or `not_run`.
- **C3. Honest metrics.** Add per-sample fields:
  - `leftover_windows`;
  - `wrong_target`;
  - `input_sent_mismatch`;
  - `false_success`, computed independently (today B04, B07 and B09 copy or loosen it: lines 536, 667, 687).
  Cleanup errors are recorded, not swallowed (around lines 92–95).
- **C4. Temp folders.**
  - Each sample removes only the `%TEMP%\OmniWin-test-*` folder it created, recorded by path, after the sample's checks are written.
  - B06 must not leave thousands of files.
  - The 1.4 GB left from earlier runs stays for the owner; don't remove it.
- **C5. Click-speed benchmark in `bench/`.**
  - The script that measured D2 lives only in a scratch folder. Move it into `bench/OmniWin.Bench` as a command.
  - Save the result in `artifacts/benchmarks/` with per-phase times: resolve, precheck, dispatch, verify.
  - Measure two cases: a click whose `expect` is met (no fixed wait), and a click that changes nothing (the 300 ms wait).
- **C6. Minimize/restore live test.** It now uses its own window but was never run live after the change, and it checks only `input_sent`. Also check that the window really was minimized, then restored.
- **C7. B07 (display scale):** over Remote Desktop, Windows disables the Scale box. Mark B07 "not applicable over Remote Desktop" (the owner uses Remote Desktop only, the same decision as T3), separate from pass, fail and not run.

## 5. Docs (Agent D)

- **D1. Bring these up to date with the final state:** `docs/status/WS1.md`, `docs/status/WS4.md`, `docs/TOOLS.md` and `docs/RUNNING_TESTS.md`. Known stale lines:
  - **`WS1.md`**
    - Line 3: date and contract version.
    - Line 9: "physical-console comparison pending".
    - Lines 9 and 21: "strict budgets fail / open". Close them with the new per-phase budgets.
    - Old counts only (lines 7, 19, 35, 39, 53, 119–123, 185, 191): add the final counts.
    - Line 195: the old title-based D1 rule.
    - Line 203: "click budget stays with the owner".
    - Lines 206–208: the D3 text that contradicts the F16 fix.
    - Line 204: "no system setting change". The saved foreground-lock value did change; see section 6.
  - **`WS4.md`**
    - Line 3: date, contract 2.4, "no real input".
    - Line 8: old counts.
    - Line 16: 24 tools; it is 26.
    - Line 32: the no-input command.
    - Lines 33–34: "not over Remote Desktop".
    - Line 35: two different benchmark commands.
    - Line 36: the scratch script.
    - Line 37: the B06 text, plus the missing final nine-task results.
    - Line 56: phase text.
    - Line 63: "F01–F17 not built".
    - Line 75: finding 1, fixed.
    - Line 82: finding 8, superseded.
    - Line 131: 27 tests.
    - Line 132: "never run".
  - **`TOOLS.md` line 50:** add that `set_value`/`clear` on file-dialog boxes and address bars return `unverified` (use `type`), and that Notepad refuses emoji and newlines in `type`.
  - **`RUNNING_TESTS.md`:** add the click-speed command (C5), and mention both `OmniWin-test-*` and `omniwin-test-*` temp folders.

## 6. Accuracy (lead)

The last report said several things that the files don't support. Be exact this time:
- **"Re-sent once":** the code allows up to 3 re-sends per command.
- **"B07 reports not run":** in v29, B07 was 1 failed and 19 not run.
- **"Minimize test fixed, then passed 3 of 3":** the file changed after the recorded live run, and there is no record of that rerun.
- **"Foreground lock restored":** the *saved* value was the Windows default, 200,000 ms, and it is now 2,147,483,647 ms. Say what changed it, and when. Don't change it again; the owner decides.
- **`close_leftovers.py`** (in your scratch folder) closes every Notepad, Settings and Calculator window and empties unsaved Notepad tabs. Say whether you ran it, when, and which windows it closed.

## 7. Integration, live run and report (lead)

1. Merge, check that no file was changed by two agents, then build Debug and Release with `-warnaserror`.
2. Run the full suite in Release, then Debug: 0 failed, Edge 0 before and after each.
3. Republish. Check: 26 tools, contract 2.4, exits when stdin closes.
4. **Live run.** The owner is on Remote Desktop: keep the session `Active`, and the owner leaves the mouse and keyboard alone.
   - all **nine** tasks, 20 runs each, **on the final harness code**;
   - D1 (Save dialog and address bar);
   - F16;
   - long text (A3);
   - the minimize/restore test (C6);
   - task 10;
   - the click-speed benchmark (C5).
   Any failure gets fixed and rerun.
5. **Report.** Include:
   - Yes or No per item, with the proving test or artifact;
   - exact counts and Edge counts;
   - the nine-task table (passed, failed, not run, not applicable, `wrong_target`, `input_sent_mismatch`, `false_success`, `leftover_windows`);
   - the per-phase click times, and the budgets you wrote into contract §15;
   - every changed file, by agent;
   - hashes;
   - the answers to section 6.
   Then stop.
