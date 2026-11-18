# Reviewer findings before the final report

Copy everything below the line and send it to the AI.

---

The reviewer read the new code while your live session was running. It ran no builds and no tests, and edited nothing in `OmniWin\`. Most of the work matches the plan. These items need fixing before the final report.

## When to do this

- **Don't interrupt a live test that is running.** Finish the current live step first.
- **If the owner is still at the PC:**
  1. apply the fixes;
  2. run all non-live tests in Debug and Release;
  3. re-run only the live tests these fixes affect (the `screen_view` live tests, and any live test of `app_launch`, the clipboard or the terminal);
  4. then write the final report.
- **If the owner has left:** apply the fixes and run all non-live tests. In the final report, list the live tests that need a re-run.
- **In the final report,** put each item under its F number, with the test that proves it.

## What the reviewer confirmed as matching the plan

- **No downloads:** the projects use only the five packages they already had.
- **No Safety edits:** `OmniWin.Safety` has no changes.
- **All 26 tools are registered.**
- **The page-dialog rule:** `dialog_accept` goes through the gate with the dialog text, and accepting "Delete everything?" is denied. `dialog_dismiss` is always allowed.
- **The activity log:** URLs keep only the scheme and host, `bN` and `tN` ids are kept, and no image or clipboard text is logged.
- **`app_launch`:**
  - only Start-menu apps or exact allow-listed paths;
  - a path, URL or command line is refused;
  - command processors never get `args`.
- **Workflows:**
  - names can't become paths, and existing files are never overwritten;
  - files are validated on load, and every step is re-checked against the gate before a run;
  - typed text is saved only as a placeholder;
  - workflows can't call themselves.
- **`screen_view` of one window** uses `PrintWindow`, so windows on top can't leak into the picture.
- **`browser_view`** reads the sensitive fields before and after the screenshot, and masks cross-origin frames whole.

## Must fix

### F1: pasting gets around the typed delete-command check (high)

- **The gap:** `clipboard_set` can put a command such as a delete command on the clipboard. A paste into a command window then sends that text without the typed-command check ever seeing it. A paste is `desktop_keys ctrl+v` or `shift+insert`, a right-click in a terminal (Windows Terminal and the console paste on right-click), or a "Paste" menu item. A command window is a terminal, the Run box, or Explorer's address bar. Nothing in `OmniWin.Safety` or `OmniWin.Desktop` handles a paste today.
- **The owner's approval:** this needs a change on the delete-protection side. **The owner approved it when sending this file.**
  - **Allowed:** the smallest change that adds this one refusal.
  - **Not allowed:** removing, loosening or reordering any existing check.
- **Done when:**
  - a paste into a command window is checked against the clipboard's text, using the same delete-command check that typed text gets;
  - a match returns `denied policy_forbidden` with `input_sent=false`;
  - a paste into an ordinary text field is unchanged.
- **Tests,** using a fixture terminal-like window or fakes only, never a real delete command in a real terminal:
  - each paste route into a command window is denied;
  - a harmless paste into a command window is allowed;
  - a paste into a normal text box is allowed.

### F2: `app_launch` skips the delete-word check (medium-high)

- **Where:** `SystemToolsService.cs:248-250` builds the descriptor with action `launch` and no target. `DeletePolicy.WhyForbidden` checks names only when `ActivatesTarget(a)` is true and `a.Target` is set (`DeletePolicy.cs:70`), and `launch` isn't one of those actions.
- **The gap:** a Start-menu entry such as "Uninstall …" launches, although "uninstall" is one of the owner's delete words.
- **Fix in `OmniWin.SystemTools` only, without changing Safety:** describe the launch so the existing name check applies. For example, give it a target whose name is the app's Start-menu name, with an action the policy already checks.
- **Test:** a fake catalog entry "Uninstall OmniWin Test" returns `denied policy_forbidden` with `input_sent=false`, and the launcher fake is never called. Never launch a real uninstaller in a test.

### F3: whole-screen and region pictures can leave windows unmasked past 60 (medium)

- **Where:** `ScreenViewService.cs:276`, `if (++count > _options.MaxWindowsToMask) yield break;`. Windows after the 60th are neither checked for password fields nor blacked out.
- **Fix:** black out every window past the limit, or return `error` and no image. Never return those windows unmasked.
- **Test:** fakes with more than 60 overlapping windows. The extra windows are black, or there is no image.

### F4: masks can miss if a window moves during capture (medium)

- **Where:** password rectangles are found first, then the window or region is captured (`ScreenViewService.cs:165-166` and `:183-186`). A move or scroll in between puts the black boxes in the wrong place.
- **Fix:** read the window rectangles before and after the capture, as `browser_view` does with its fields. If any changed, capture once more. If they still don't match, return `error` and no image.
- **Test:** a fake window whose rectangle changes between the two reads.

### F5: `browser_view` misses two kinds of password field (medium-low)

- **Shadow DOM:** `sensitiveRects` uses `document.querySelectorAll` (`PageScript.cs:271`), which doesn't look inside shadow roots. Walk open shadow roots. Black out any closed shadow root that has an input.
- **Nested frames:** only direct child frames are searched (`CdpSession.cs:596`). Black out any frame deeper than one level whole, as cross-origin frames are.
- **Tests:** fixture pages with a password field inside a shadow root and inside a frame two levels deep. Both are masked.

## Should fix

- **F6: honest `input_sent` when a browser tool fails.** `BrowserTools.Failure` (`BrowserTools.cs:101-122`) always reports `input_sent=false`. For `browser_act` and `browser_nav`, report `input_sent=true` when it can't be ruled out, as contract §13.3 says ("conservatively reports `input_sent=true`"). Check the other tool files for the same pattern.
- **F7: an activity log the owner can read.** `ToolCallAudit.SafeMetadata` (`ToolCallAudit.cs:92-104`) drops or hides harmless details:
  - the `app` of `app_launch`;
  - `kind`;
  - `volume` and `mute`;
  - `media` actions;
  - `window_manage` actions such as `close`, `minimize` and `move`, which are logged as `<5 chars>`.

  Log these plainly. Keep `args`, `text`, `value` and `keys` redacted.
- **F8: interpreters in the allow list.** `AppLaunchRules.CommandProcessors` (`AppLaunchRules.cs:18-22`) doesn't list other interpreters an owner might allow-list: python, node, bash, wsl, rundll32, regsvr32, msbuild and java. Either add them, or state in `docs/INSTALL.md` that interpreters must never be allow-listed. Adding them is preferred.
- **F9:** `ClipboardBackend.cs:91` uses `Thread.Sleep(10)`. Use a bounded async wait to match §16, or say in the final report why it must stay.
- **F10:** `02_Architecture_and_Contracts.md` line 8 says the version "is raised to 2.4 when the work order is accepted", but `ContractVersion` is already `"2.4"`. Make them agree.

## Final report additions

- Under Result: F1–F10, each Yes or No, with the proving test names.
- Under Rules check: say that F1 changed the delete-protection side with the owner's approval, and that it only adds a refusal.
- **The owner's clipboard:** the live clipboard test puts back only text. If the owner had a picture or files on the clipboard, those are lost. Mention it.
