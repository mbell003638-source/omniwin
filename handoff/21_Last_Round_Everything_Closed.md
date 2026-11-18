# OmniWin: last round (close everything)

Copy everything below the line and send it to the AI.

---

**The owner wants nothing left open: no "known limits" and no "later" items, except the two downloads they chose to do later.** Do all of this in one go, then send one short report. Handoff 19's rules still apply: one test run at a time, count `msedge.exe` before and after every run, never close the owner's own Edge, never delete.

## 0. Before you start

1. **Wait for the reviewer's run to end.** Start only when no `dotnet test` process is running.
2. **Keep the reviewer's changes from 2026-11-10.** Don't redo them. They are listed in `20_Final_Report_Review.md`:
   - **Browser:** `BrowserProcesses.NamesProfile` (exact profile match); `BrowserLauncher` (`.omniwin-owner` and `ClearLeftoverBrowser`); `BrowserEngine.EnsureConnectedAsync` (closes the previous launch before reconnecting); the `frames_not_read` footer (`PageScript.cs`, `CdpSession.cs`, `BrowserEngine.cs`).
   - **Tests and fixtures:** `testapps/web-fixtures/frame-loading.html`; new tests in `BrowserLaunchCleanupTests` and `BrowserEngineTests`; a bounded wait in the frame tests in `BrowserEngineTests` and `tests/OmniWin.Integration/BrowserActTests.cs`.
   - **Docs:** the quoted `cmd` lines in `docs/RUNNING_TESTS.md`; contract lines in `02_Architecture_and_Contracts.md`.

## 0.5 Split the work: 2 to 6 agents in parallel

You are the **lead**. Start these agents at the same time. Each one edits only the files listed for it, and no two agents edit the same file. Shared files are the lead's only: `OmniWin.Core`, `Program.cs`, the contract, `README.md` and the status files.

| Agent | Owns | Does |
|---|---|---|
| **A: file dialogs** | `src/OmniWin.Desktop/ValueSettingPolicy.cs`, `tests/OmniWin.Desktop.Tests/ValueSettingPolicyTests.cs` | Section 1 (D1), with fakes only |
| **B: CJK typing** | `src/OmniWin.Desktop/DesktopText.cs`, the typing part of `DesktopActions.cs`, their tests | Section 2 (F16): reads the code, finds candidate causes, prepares the fix and tests with fakes. The lead proves it live in step 5. |
| **C: click speed** | `bench/` | Section 3: gets the click-speed measurement ready to run in step 5 |
| **D: docs** | `docs/` (except the status files) | Updates `docs/RUNNING_TESTS.md` and `docs/INSTALL.md` for anything the others change |
| **Lead** | the shared files | Merges, builds, runs every test run, republishes, does the whole live rerun alone, writes the report |

Rules for the agents:
- **No agent runs anything that starts a browser or sends real input.** Agents run only their own project's no-input unit tests, and only one agent at a time.
- **Full-suite runs, publishing and the live rerun are the lead's alone,** one after another.
- **Before merging, the lead checks that no file was changed by two agents.**

## 1. D1: no false `confirmed` in any file dialog (no title list)

The current check (`ValueSettingPolicy.IsIgnored`) recognises a file dialog only by English window titles. A dialog titled "Save Image As" or "Open File" can still report `confirmed`, and so can a file dialog's own address bar, because it runs in the app's process, not `explorer.exe`.

- Recognise the controls themselves, in any window and with any title: the File name box of the common file dialogs, and the address bar of any file dialog or Explorer window. Use their automation IDs or class names, read from a real dialog's UI tree. Keep the title rule only as an extra signal.
- `set_value` and `clear` on them must never return `confirmed`.
- Add tests with fakes for: an unusual English title, a non-English title, the dialog's own address bar, and ordinary edits that must stay unaffected.
- In the contract, replace the "Known limit" sentence under "No false `confirmed`" with what you built.

## 2. F16: fix it

Typing CJK text at the start of existing text still produces a repeated first character. The owner doesn't accept leaving it open.

- Find the real cause in a live run and fix it, so the text reads back correctly and the result is `confirmed`.
- If one control type truly can't take that text through typing, OmniWin must still produce the right text there, and never claim success it didn't check.
- Add tests, and prove the fix in the live run (step 5).

## 3. D2 and the console test (T3)

The owner uses Remote Desktop, not the physical screen.
- Measure click speed in the live run.
- Record those RDP numbers as the final budget, and close D2 with them.
- Mark T3 "not applicable: the owner uses Remote Desktop only".

*(Owner: delete this section before sending if you'd rather keep these two for the physical screen.)*

## 4. Build, test and publish

1. Build Debug and Release with `-warnaserror`.
2. Run the full suite in Release, then Debug, one after the other. Both must have 0 failed, with Edge 0 before and after.
3. Republish (`dotnet publish src/OmniWin.Server -c Release -o publish`). Check the server starts, lists 26 tools, reports contract 2.4, and exits when stdin closes.

## 5. Live rerun (the owner is connected and says go)

**Check first:**
- Run `query session`: the owner's session must be `Active`. If it shows `Disc`, ask the owner to open the Remote Desktop window, and wait.
- Read `SPI_GETFOREGROUNDLOCKTIMEOUT`. If it's `2147483647`, ask the owner to sign out and sign back in, then continue. Don't change the setting yourself. Signing out also ends your session, so save your progress first.
- Make sure Task Manager isn't in front and Explorer responds.

**Tell the owner** to keep the Remote Desktop window open and leave the mouse and keyboard alone until you say it's done.

**Run, with the commands from `docs/RUNNING_TESTS.md` §3 and §4:**
- task 10 (window switch);
- the D1 check: type a file name into a Save dialog and a path into the address bar, then check that the file and the folder really changed;
- the F16 check;
- the 9-task harness, 20 runs each;
- the click-speed measurement for section 3.

Every failure gets fixed and rerun, not listed.

## 6. Final report (short)

- Yes or No for D1, F16, D2 and T3, integration, publish and the live rerun, each with its proving test or artifact.
- Exact counts for both full runs, and the Edge count before and after each.
- The live results: task 10, D1, F16, the 9-task harness (success rate, wrong target, `input_sent` mismatches, false `confirmed`), click speed.
- Every changed file.
- Hashes of every Release DLL and the published exe.
- What's still open. The answer must be only "the two downloads the owner chose to do later".
