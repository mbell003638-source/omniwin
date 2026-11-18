# Reviewer check of the final report (handoff 19)

Checked on 2026-11-10, with the owner disconnected. The builder AI had stopped (no `dotnet` or test processes running).

## Verdict

**Accepted. Only the live rerun is still open,** and it needs the owner connected. Four small follow-ups are listed at the end. None of them blocks use.

## What the reviewer checked

| Check | Result |
|---|---|
| `msedge.exe` count at the start | 0 |
| Release DLL and published exe hashes | all 9 match the report |
| Browser test project, Release, once | 138 total, 135 passed, 3 skipped, 0 failed; Edge 0 before, 0 after |
| Full suite, Release, first run | 1,170 total, 1,119 passed, **1 failed**, 50 skipped; Edge 0 before, 0 after |
| Integration project alone, 3 runs | 142 total, 110 passed, 32 skipped, 0 failed each time; Edge 0 before, 0 after |
| Full suite, Release, after the test fix below | 1,170 total, **1,120 passed, 0 failed**, 50 skipped; Edge 0 before, 0 after; build 0 warnings, 0 errors |
| Hashes after the reviewer's runs | all 9 unchanged (the test fix touches no product DLL) |
| Left running afterwards | no Edge; MSBuild's idle helper processes were stopped with `dotnet build-server shutdown` |

**The one failure was a timing race in a test, not a product bug.** The test was `BrowserActTests.Same_origin_frame_elements_have_a_frame_prefix_and_the_click_lands_inside_the_frame`. It slept a fixed 600 ms and then expected the inner frame's button to be listed. It failed only when the browser and integration projects ran at the same time. The reviewer replaced the sleep with a bounded wait of up to 10 seconds (test file only, see "Reviewer edits").

## Item by item

| Item | Verdict | Notes |
|---|---|---|
| B1 browser launch | Yes | `BrowserLauncher` keeps waiting after an exit code of 0, takes over the real browser by its unique profile folder, and stops it on dispose, failure and cancel. The engine also cleans up when a launch fails partway. 7 tests. Edge stayed at 0 through every run above. |
| D1 no false `confirmed` | Yes, for the two known cases | The fix in `ValueSettingPolicy.cs` recognises the dialog by English title words. Other titles slip through. See follow-up 1. |
| D2 task 10 | Environment (accepted) | Confirmed independently. The live value of the Windows foreground-lock setting is 2,147,483,647 ms, but the saved value is the Windows default of 200,000 ms. So something in this sign-in changed it while running. OmniWin's code never sets it: it only reads one mouse setting. A sign-out and sign-in, or a restart, brings back the saved value. Then run task 10 again in the live rerun. Nobody should change this setting by hand. |
| D3 CJK revert | Yes | The code is back to batches of 1 in Notepad's RichEdit and 16 elsewhere. The contract still described the reverted rule; the reviewer fixed that line. |
| D4 slow window | Yes | A window is marked slow after a read over 1.5 s or a UI Automation time-out, then skipped for 30 s. Known limit: the first slow read still costs up to the 1 s UI Automation time limit. The one call that lists all windows isn't covered (the builder said so). |
| T1 harness | Yes (built, not run) | Refuses to start without a connected desktop. |
| T2 Antigravity steps | Yes | `docs/INSTALL.md`; no settings file is edited. |
| T3 commands | Yes, after a fix | The two `cmd` lines stored the flag as `1 ` with a trailing space, so the live tests would have skipped silently. The reviewer fixed both lines. |
| F1–F10 | Yes | Checked in code: F2 (app launch now goes through the delete-word name check), F3 (windows past 60 are blacked out whole), F5, F6, F8 (interpreter list), F9 (no `Thread.Sleep` left), F10. F1, F4 and F7 were checked only through the passing suite. |
| Integration | Yes | See the table above. |
| Live rerun | Not run | Waits for the owner. Commands are in `docs/RUNNING_TESTS.md` §3 and §4. |

## Reviewer edits (small, no engine code)

1. **`tests/OmniWin.Integration/BrowserActTests.cs`:** the frame test now waits up to 10 seconds for the inner button, instead of a fixed 600 ms sleep.
2. **`docs/RUNNING_TESTS.md`:** both `cmd` lines now quote the flag, `set "OMNIWIN_LIVE_SESSION=1" && …`, so no trailing space ends up in the value.
3. **`02_Architecture_and_Contracts.md`:** the "Typing non-ASCII text" line now says the rule was reverted (D3).
4. **`README.md`** in this folder: lists 16–20 in order.

## Follow-ups: done by the reviewer on 2026-11-10 (nothing sent back to the builder)

The owner asked for no more back and forth, so the reviewer did items 2, 3 and 4 below directly, with tests. Item 1 is recorded as a known limit: it is desktop-engine code, which the reviewer does not write, and the tool description already tells the model to type in those controls.

| Item | Status | Where | Tests |
|---|---|---|---|
| 1. D1 wider match | **Known limit** (in contract 2.4) | none | none |
| 2. Say when a frame wasn't read | **Done** | `PageScript.cs` (frames report `loading`), `CdpSession.cs` (`FramesNotRead`, `FramesNotReadCount`), `BrowserEngine.cs` (footer `frames_not_read: N …`) | `A_same_origin_frame_that_is_still_loading_is_reported_so_the_model_observes_again` (new fixture `frame-loading.html`), `Frames_not_read_counts_loading_and_failed_same_origin_frames_and_never_more_than_exist` |
| 3. Exact profile match | **Done** | `BrowserProcesses.NamesProfile` | `NamesProfile_matches_only_the_exact_profile_folder` (8 cases) |
| 4. Leftover browser after a hard stop | **Done** | `BrowserLauncher` (`.omniwin-owner`, `ClearLeftoverBrowser`), `BrowserEngine.EnsureConnectedAsync` (closes the previous launch before reconnecting) | `A_browser_left_by_this_server_is_cleared_quickly_before_the_next_launch`, `A_browser_owned_by_another_running_server_is_never_touched` |

The frame test in the browser project got the same bounded wait as the Integration one.

**Runs after these changes (2026-11-10):**
- **Builds:** Debug and Release built with `-warnaserror`: 0 warnings, 0 errors.
- **Browser project, Release:** 150 total, 147 passed, 3 skipped, 0 failed. Edge was 0 before and 0 after.
- **Full suite, Release:** 1,182 total, 1,132 passed, 50 skipped, 0 failed. Edge was 0 before and 0 after.
- **Full suite, Debug:** 1,182 total, 1,132 passed, 50 skipped, 0 failed. Edge was 0 before and 0 after.
- **New Release hashes:**
  - `OmniWin.Browser.dll` `0730417e1e5a6c0a268606ed9b302f5df7eaaf20215821ff6c2c84e1046ac05d`
  - `OmniWin.Server.dll` `7694c8e9a8e0610bec8deedbfbb262f6141c3494a4774c12888d93672847b0dd`
  - The other DLLs are unchanged.
- **Not republished yet:** `publish\` still holds the earlier build. The builder republishes after the last round (file 21), because D1 changes the Desktop DLL anyway.

Item 1 is no longer a known limit: the owner sent it to the builder in file 21.

### The original follow-up text (for the record)

1. **D1, wider match.** `ValueSettingPolicy.IsIgnored` recognises a file dialog only by English titles (`Save as`, `Save`, `Open`, `Select…`, and so on). Titles like "Save Image As" or "Open File" don't match, so `set_value` there could still report `confirmed`. A file dialog's own address bar is also missed, because it runs in the app's process, not `explorer.exe`. Match the File name box by its AutomationId in any window instead, for example `FileNameControlHost`, `1001` or `1148`; confirm the IDs from a real dialog's UI tree. Match the dialog's address bar the same way. Add tests.
2. **Say when a frame wasn't read.** In `CdpSession.cs` (around lines 466–511), a same-origin frame that is still loading, or whose read fails, adds nothing, and nothing says so. The header still shows `frames=2`. Add a footer note, for example `frames_not_read: f2 (loading)`, so the model knows to look again.
3. **Exact profile match.** `BrowserProcesses.FindForProfile` matches the profile folder as a substring, so a folder `X\profile` also matches `X\profile2`. Match the whole `--user-data-dir="<path>"` value instead. The risk is low today, because test folders are GUID-named.
4. **A browser left over after a hard stop.** If the server is ended without shutting down (for example, force-closed by the MCP client), its dedicated browser keeps running. The next launch on the same profile then likely hands off to that old browser. The old `DevToolsActivePort` is older than the new start, so the launcher would wait 15 s and fail once. This is untested. Before launching, stop any browser already running on OmniWin's own dedicated profile (or reuse it), and add a test for it.

The tests after these must still pass in Release and Debug, with Edge at 0 before and after.

## Still open for the owner

- **Live rerun** when you're connected (keep the RDP window open and don't use the mouse or keyboard): task 10, the D1 typing check, the 9-task harness 20 times, and the F16 check. Sign out and back in first (or restart and sign in), so the foreground-lock setting goes back to its saved value.
- **Console click test (T3) and the click budget (D2):** need the physical console.
- **Downloads later:** detected boxes and the Windows-MCP comparison.
- **Antigravity:** you add OmniWin yourself in its "Manage MCP servers" screen (steps in `docs/INSTALL.md`).
- **Open bug:** F16 (CJK typing at the start of existing text), honestly reported as `contradicted`.
