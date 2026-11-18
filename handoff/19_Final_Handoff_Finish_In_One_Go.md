# OmniWin: final handoff (finish in one go, with parallel agents)

Copy everything below the line and send it to the AI.

---

**The owner wants OmniWin finished in one go.** Use several agents working in parallel, then send **one** final report. This file replaces `18_Final_Review_Round.md` and includes everything in it. The rules in `15_Full_Project_Completion_Handoff.md` §2 and §3 still apply.

## 0. What happened, and the first rule

The reviewer ran the full suite once on 2026-11-09:
- **Result:** 1,127 total, 918 passed, **161 failed**, 48 skipped.
- **Cause:** almost all failures were the browser launch (bug B1 below).
- **Damage:** every failed test left a hidden Edge running. Over 100 were left, and the PC became so slow that the owner couldn't sign in, even over RDP.

**First rule: the owner's PC must stay usable.**
- **Count `msedge.exe` processes** before and after every test run. If a browser your tests started is still running afterwards, stop everything and fix that first.
- **Only one test run at a time,** for the whole team.
  - The lead runs every full-suite run.
  - Agents run only their own project's unit tests, one agent at a time, and never anything that starts a browser until B1 is proven fixed.
- **Don't close the owner's own Edge windows,** or any process OmniWin didn't start.
- If the PC gets slow, stop all runs and report it.

## 1. How to split the work

You are the **lead**. Start these agents in parallel. Each owns only its listed files, and no two agents edit the same file. Shared files are edited by the lead only: `OmniWin.Core`, `Program.cs`, the contract, `README.md` and the status files.

| Agent | Owns | Does |
|---|---|---|
| **A: browser** | `src/OmniWin.Browser`, `tests/OmniWin.Browser.Tests`, `testapps/web-fixtures` | B1. Then checks the tab parallelism (on by default) is safe. |
| **B: desktop** | `src/OmniWin.Desktop`, `src/OmniWin.Server/Tools/DesktopTools.cs`, `tests/OmniWin.Desktop.Tests` | D1–D4 |
| **C: tests, bench, docs** | `tests/OmniWin.Integration`, `bench`, `docs` (except the status files) | T1–T3 |
| **Lead** | the shared files above | Merges, runs every full-suite run, does the live rerun, writes the final report |

Order:
1. Agent A starts B1 at once.
2. Agents B and C work at the same time, without starting a browser, until the lead confirms B1 passed.

## 2. The work

### B1 (Agent A, first): the browser launch, and no leftover browsers

- **The problem:** the `msedge.exe` OmniWin starts now hands off to another process and exits with code 0. The browser does start (its `DevToolsActivePort` file appears), but `BrowserLauncher.WaitForActivePortAsync` (`BrowserLauncher.cs:155`) treats the exit as a failure and loses track of the browser.
- **Done when:**
  1. The launcher reads the port from `DevToolsActivePort` even when the first process exits normally, and connects.
  2. The launcher tracks the real browser process, for example through the debugging port's owner or the process tree, and closes it when the engine is disposed, or when a launch or test fails partway.
  3. A test proves a failed launch leaves no browser running.
  4. The full browser test project, run by the lead, passes twice in a row, with the same Edge count before and after.

### D1–D4 (Agent B)

- **D1: no false `confirmed`.** `set_value` on the Save dialog file name and Explorer's address bar read back correctly but had no effect, and both counted as false `confirmed`. The target is zero. Make these cases unable to report `confirmed`, for example `unverified` for file dialogs and address bars. Tell the model in the tool description to type there instead. Add tests.
- **D2: task 10 regressed.** It passed on Nov 7 in the same kind of RDP session, and now it fails with `foreground_failed`. Find what changed since then: the lane gate, F1, the UI Automation worker shutdown, or the CJK change. Fix it, or prove with evidence that it's the environment.
- **D3: revert the CJK typing change.** One character at a time didn't fix F16, so undo it. Keep F16 listed as an open bug, with its honest `contradicted` result.
- **D4: one slow window must not block every read.** Give each window read its own time limit and skip a window that is too slow. Test it with a fake slow window.
- **Desktop lanes:** they stay off by default. Don't turn them on.

### T1–T3 (Agent C)

- **T1: the 9-task harness.** Fix what made it unreliable when the session dropped. It runs live in step 4, so keep it ready, but don't run it yourself.
- **T2: `docs/INSTALL.md`.** Explain how the owner adds OmniWin in Antigravity's own "Manage MCP servers" screen, with this entry. **Don't edit any Antigravity settings file.**

  ```json
  "omniwin": { "command": "C:\\Users\\<you>\\Desktop\\OmniWin\\publish\\OmniWin.Server.exe", "args": [] }
  ```
- **T3: one command each,** written down for: the no-input tests, the full suite, the live session and the benchmarks.

## 3. Integration (lead)

1. Merge every agent's work. Check that no two agents changed the same file.
2. Build Debug and Release with `-warnaserror`.
3. Run the full suite in Release, then in Debug, then both again: four runs, one after another. All must pass, with no Edge left running from the tests.
4. Republish to `publish\`. Check the server starts, lists 26 tools and exits.
5. Update the contract, the status files and `README.md`.

## 4. Live rerun (lead, only with the owner's yes)

**The owner won't be connected, not even over RDP, while you work.** So:
- don't wait for them, and don't ask for "go";
- skip this live step and list it in the final report as the one remaining step, with its command;
- everything else must work in a disconnected session;
- tests that need a connected desktop must skip cleanly with a clear message, not hang or fail;
- the server and test hosts must exit cleanly when no desktop is attached.

If the owner reconnects later and says "go", run the list below then. Otherwise this step waits.

Ask the owner once, with the list below, and wait for their yes. They're on RDP: they must keep the session connected and leave the mouse and keyboard alone.

The list:
- task 10 (window switch);
- the D1 recheck: type into the Save dialog and the address bar;
- the 9-task harness, 20 runs each;
- the F16 check.

If the owner isn't available, skip this step and list it as the one remaining step, with its command.

The console click test (T3 in `00_START_HERE`) stays "not run: needs the console".

## 5. Owner decisions already made (don't ask again)

- **No downloads now.** Detected boxes and the Windows-MCP comparison wait until the project is accepted. The owner approves them then. Keep their written plans ready.
- **Antigravity:** the owner adds OmniWin themselves through Antigravity's settings screen. You don't edit its files.
- **Never delete:** no deleting or recycling files. Test files stay for the owner. Tests may clean up only their own temp folders.

## 6. The final report (one, at the end)

Use these headings:

1. **Result:** Yes or No for B1, D1–D4, T1–T3, integration and the live rerun, each with its proving test.
2. **Agents:** what each agent changed, file by file. Confirm no file was edited by two agents.
3. **Tests:** exact counts for all four full runs (Debug, Release, then both again), and the `msedge.exe` count before and after each.
4. **Failed runs:** every one, with what you changed.
5. **Live rerun:** results, or "not run", with the command.
6. **Accuracy gates:** wrong target, `input_sent` mismatches, false `confirmed`, suite success, adversarial cases.
7. **Measurements:** every §15 row, from the artifacts.
8. **Rules check:** owner rules 1–8, B1–B7, and section 0 of this file.
9. **Owner decisions still open:** D2 (click budget), the two downloads for later, and anything new.
10. **Hashes:** every Release DLL and the published exe.

Then stop. The reviewer checks once, without running anything heavy on the owner's PC until your report shows no leftover browsers.
