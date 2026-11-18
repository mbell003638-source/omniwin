# Final review: what's left before OmniWin is accepted

Copy everything below the line and send it to the AI.

---

The reviewer checked your final report on 2026-11-09. It rebuilt the solution and ran the full test suite once in Release.

**Verdict: not accepted yet.** The builds are clean (0 warnings, 0 errors), but the reviewer's test run did not match your report.

## B1: the browser launch fails on this PC now (must fix first)

**Impact on the owner:** the browsers left over from the reviewer's run most likely made the PC so slow that the owner couldn't sign in, even over RDP. Until B1 is fixed and proven:
- **Don't run the browser tests,** or the whole suite, except the single test that proves the fix.
- **Count `msedge.exe` processes before and after every test run.** If any browser your tests started is still running afterwards, stop and fix that before running anything else.
- **Never run test projects in parallel** while this is open.

- **The reviewer's Release run:** 1,127 total, 918 passed, **161 failed**, 48 skipped. Your report said 0 failed.
- **Where the failures are:** almost all are browser tests, and they all fail the same way: `Browser process exited unexpectedly with code 0`, thrown by `BrowserLauncher.WaitForActivePortAsync` (`BrowserLauncher.cs:155`).
- **What's happening:** the `msedge.exe` process OmniWin starts now hands the work to another process and exits with code 0. The browser itself does start: in a manual check, its `DevToolsActivePort` file appeared. The launcher treats that first exit as a failure and loses track of the real browser.
- **Side effect:** each failed test left its headless browser running. After the reviewer's one run, more than 100 headless Edge browsers were still open.
- **Done when:**
  1. The launcher gets the browser from the `DevToolsActivePort` file even when the process it started exits normally, and connects to it.
  2. The launcher keeps track of the real browser process, for example through the debugging port's owner or the process tree, so it can always close it.
  3. Every browser OmniWin starts is closed when the engine is disposed, or when a launch fails partway, so nothing is ever left running.
  4. A test proves no browser is left after a failed launch.
  5. The whole solution passes in **both** Debug and Release, run twice in a row, with no Edge left running afterwards.
- **Tell the owner** that a restart is the simple way to clear the browsers left over from the reviewer's run. Don't close the owner's own Edge windows.

## Fix before acceptance

1. **The two false `confirmed` results** (your finding 2). `set_value` on the Save dialog file name and Explorer's address bar read back correctly but had no effect. The accuracy target for false `confirmed` is zero. Fix it so these cases can't be reported `confirmed`. For example, return `unverified` when the control is in a file dialog or address bar, and tell the model in the tool description to type there instead. Add tests.
2. **Task 10 (switching windows) regressed.** The Nov 7 acceptance passed it in the same kind of RDP session, and now `foreground_failed` blocks it. Find what changed since Nov 7: the desktop-lane gate, F1, the UI Automation worker shutdown, or the CJK typing change. Fix it, or prove it's the environment and show the evidence.
3. **F16 and the CJK typing change.** The one-character-at-a-time change didn't fix F16, so revert it, because it changed the desktop typing path for no gain. Keep F16 listed as an open bug, with the `contradicted` result as proof that it isn't hidden.
4. **One slow window blocks every desktop read** (your finding 3). A single slow app must not make `windows_list` and the other reads time out: give each window read its own time limit and skip a window that is too slow. Add a test with a fake slow window.
5. **The 9-task harness:** its 20-run results were invalid. Rerun it as part of the next live run.

## The reviewer will check after B1 passes

The reviewer couldn't verify F1–F10 or the parallel work through tests while 161 tests failed. Once the suite passes, it will check:
- each F item against the code and its test;
- the browser tab parallelism, which is on by default;
- the desktop lanes, which are off by default.

## Owner decisions (don't act without the owner's answer)

- **Antigravity:** which settings file to edit, or the owner adds OmniWin through Antigravity's own "Manage MCP servers" screen.
- **The console click test (T3):** stays "not run" while the owner uses RDP.
- **Downloads:** detected boxes and the Windows-MCP comparison stay off until the owner approves.

## Reply format

Same headings as before. Copy test counts from the run output, Debug and Release, and say how many Edge processes were running before and after the runs.
