# 27. Reviewer check of contract 2.6 (handoff 26, builder report 26)

Date: 2026-11-15. Reviewed by reading the code, the `.trx` files and the published hashes (no suite rerun).

## What checks out

- **Test counts match the report.** Release 1,532 total, 1,469 passed, 0 failed, 63 skipped (`artifacts/test-results/2026-11-15-final-release`, 8 `.trx`). Debug 1,532 / 1,468 / 1 failed / 63 (`...-final-debug`). Router.Tests 157 of 157 in both.
- **The Debug failure is a test-timing flake, not a product bug.** `ForegroundAndPumpTests.cs:421` asserts `sleeps > 3` inside a 60 ms window; under full-suite load the thread was preempted (the test took 222 ms) and only 3 pauses fitted. It passes alone.
- **All 9 published hashes match the report**, and each DLL equals its Release build output.
- **Accepted as built:** the new reason words (contract §19), the 35-tool surface and the `Harness/Contract.cs` edit (minimal), the lock file instead of a named semaphore (Windows frees it when the holder dies; a semaphore would stay taken after a crash), window-title generalising (the resolver also matches the process, and with two matching windows it takes only the foreground one, else nothing), "newest recipe wins", `recipe_not_ready` not counted, no recipe for a request with a line break or brace, slot values limited to 5 words with no joining word.
- **Privacy:** the audit log uses an allowlist of argument keys, so `goal`, `template`, `params`, `sel` and `text` are never logged. Selected text is not logged.
- **`selection_replace`:** the gate is asked as for a `type` of the same text before anything is sent; it refuses a changed selection, a password field, protected programs and a non-text clipboard; the paste goes through the normal `desktop_keys` path (lane, stop key); the earlier clipboard text is put back on every path.
- **Decision slot:** with `provider` "none" nothing is constructed; any other name reports "no adapter in this build". There is no network code.

## Fix before the live run (3 items)

**L1. A wording alias is learned at replay time, without `task_done`, and acts after the replay are ignored.** `LearningService.RunAsync` (`src/OmniWin.Server/Learning/LearningService.cs:550-555`) calls `TryLearnWording` the moment a `workflow_run` inside an episode ends cleanly. Two problems:
1. **Owner decision 2** (handoff 26 §4) says nothing is learned without `task_done(success=true)`. Today the alias is written even if the AI never calls `task_done`, or calls it with `success=false`.
2. If the AI does more after the replay (the recipe was only part of the task), the whole request still becomes an alias, and `TaskDone` (line 161) answers "nothing new to learn". Example: recipe "fill the invoice form for {name}"; request "fill the invoice form for bob then press print"; the AI runs the recipe, then clicks Print. Saved alias: "fill the invoice form for {name} then press print". Once the recipe is trusted, that sentence runs only the form fill by itself and answers `confirmed`. Print never happens.

Fix: keep the replay (name, values) on the episode instead of learning at once. In `TaskDone(success=true)`, add the alias only if no act (recorded or not) happened in the episode after that replay. If acts followed, add no alias and answer `learned=false` with a detail saying the extra steps were not linked to the recipe (composing recipes is parked). Tests: replay then `task_done` gives `alias_added`; replay then another act then `task_done` gives none; replay and no `task_done` (or `success=false`) gives none.

**L2. A noisy recipe can still run by itself.** Handoff 26 A1 and A3 say a noisy run is saved with `auto_run_eligible=false`. `WriteCard` (line 335) ignores `Noisy`, so after 3 clean replays a noisy recipe runs from `omniwin_do` with no AI (builder decision 7). A clean replay only proves each saved step still passes, not that the task is complete. A step that was missing from the first run stays missing, and the AI may have quietly done that step by hand on each replay. Fix: `eligible` must also require `episode?.Noisy != true`. Update the noisy note in `Learned()`, the noisy test, and the §19 line about `auto_run_eligible`. The AI can still run a noisy recipe with `workflow_run`.

**L3. Some acts inside an episode do not mark it noisy.** `RunLogFilter` (`src/OmniWin.Server/Hosting/RunLogFilter.cs:42-45`) passes every tool outside `StepTools` straight through, with no `NoteAct`. So `engine_call` (the guide's fallback for what OmniWin cannot do, which is exactly the step most likely to be missing), `selection_replace`, a phrase-table act through `omniwin_do`, and power tools send input inside an episode without the episode becoming noisy. Handoff 26 A2 asks to count every acting tool call. Fix: for a tool outside `StepTools` (other than `run_steps` and `workflow_run`), call `NoteAct(false)` when the result's first line says `input_sent=true`. Test: an `engine_call` with `input_sent=true` inside an episode gives a noisy card with `auto_run=no` (after L2).

After L1 to L3: run Router.Tests (and Desktop.Tests only if `RunLogFilter` tests live there), republish, record the new hashes, then run the live checks (`Contract26LiveTests`, `docs/RUNNING_TESTS.md` §3) with the owner connected.

## Minor (no need to block)

- **M1:** ease the flaky assertion at `ForegroundAndPumpTests.cs:421`, for example `sleeps >= 2`.
- **M2:** `RecipeWordingTests` allows 10% of must-run phrases to be missed but does not print how many were. Print the count so the report can quote it.
- **M3:** for the first real decision adapter, decide "remote" from the endpoint host (loopback or not), as handoff 26 §7 says, not from the adapter's own `IsRemote`.
- **M4:** `TryLearnWording` checks `Templates.Count > MaxAliases`, which allows 11 wordings when a recipe has no main template.
- **M5:** `selection_replace` puts back plain clipboard text only (rich text or HTML copied with it is lost; now in `docs/TOOLS.md`), and it does not check that the window is unchanged after the paste before reading back.

## Docs updated by the reviewer

Contract header v2.6, §8 table (35 tools), §14 config line, and a review note at the top of §19; `docs/TOOLS.md` (2.6 tools, learning, selection, config); `docs/RUNNING_TESTS.md` (`Contract26LiveTests` command); handoff `README.md` entry 26.

## Follow-up, 2026-11-16 (builder addendum in report 26)

- **Debug rerun:** 1,532 total, 1,467 passed, **0 failed**, 65 skipped (`artifacts/test-results/2026-11-16-full-debug`). The two extra skips are in `OmniWin.Ocr.Tests` (5 to 7 skipped). M1 is done: the test now times its own pause.
- **Who fixes L1 to L3:** the builder reports the owner told it to continue, so it is making L1, L2, L3, M2 and M4. The reviewer does not edit `src/OmniWin.Server/Learning/*`, `RunLogFilter.cs` or `tests/OmniWin.Router.Tests/*` until the builder reports back. To review then: the fixes, the new `text_insert` tool (36 tools; `SelectionTools.cs`, one more line in `Harness/Contract.cs`), and the new hashes after republishing.
- **Interim app (`src/OmniWin.App`, `publish\OmniWin.App.exe`), read by the reviewer.** It is not the Handy-based app of owner decision 7, and the owner was not asked before it was built (handoff 26 §9 said to ask first and stop if the build tools are declined). It is harmless where it sits: it starts only when opened, adds nothing to Windows start-up, and adding it to `publish\` changed no existing file (only the five `OmniWin.App.*` files are new; the server hashes are unchanged). By code:
  - It starts its own `OmniWin.Server.exe` and only calls tools, so every check applies. The server lists `OmniWin.App.exe` as protected.
  - Both hotkeys are off until ticked, and Ctrl+Alt+Shift+K is refused.
  - Settings: `config.json` is replaced only after `--validate-config` accepts a candidate file, other keys are kept, and `power_tools` is never written (handoff 26 D5 allows this temp-file-then-rename of OmniWin's own config).
  - Polish sends the selected text only to the address the owner sets. An address off this PC needs the owner's tick. The key comes from an environment variable and is never saved.
  - 40 of 40 app tests pass (`artifacts/test-results/2026-11-16-app-release`).
  - **Never seen running:** no window, hotkey or polish call has been tried. Check it in the live session.
- **M6 (minor):** `RecipeStore.Append` (`RecipeStore.cs:125-147`) drops an event silently after 20 failed tries (about 200 ms of contention, now possible with two servers sharing the folder). `workflow_manage` then still answers "Recorded". A dropped `disabled` or `wrong_pick` would leave a recipe trusted. Suggest: return whether the line was written, and answer `workflow_manage status=error` when it was not.

## Follow-up 2, 2026-11-16 (builder addendum 2): L1 to L3 fixed

- **Results match:** full suites on one build, Release and Debug each 1,592 total, 1,529 passed, **0 failed**, 63 skipped (`2026-11-16-final-release`, `-final-debug`); after M6, Router.Tests 165 of 165 in Release and Debug (`2026-11-16-router-Release`, `-Debug`); App.Tests 53 of 53. The full suites were not rerun after M6 (only Router.Tests, which covers it).
- **Hashes match:** `OmniWin.Server.dll` `B12D4BAD…3596` and `OmniWin.App.dll` `943CBF03…85AA` equal their Release output; the other seven DLLs and both exes are unchanged.
- **L1 fixed** (`LearningService.cs:171-183`, `:562-575`): the replay is kept on the episode; the wording is added only in `task_done(success=true)` when `Replays == 1 && OtherActs == 0`; otherwise `learned=false`. Replay steps are recorded in the run log by the runner, so a recipe `omniwin_do` ran inside an episode is still part of what `task_done` learns.
- **L2 fixed** (`:358`): a noisy episode gives `auto_run_eligible=false`.
- **L3 fixed** (`RunLogFilter.cs:42-50`, `SentInput` at `:103`): any tool outside the run log whose first line says `input_sent=true` marks the episode noisy; `workflow_run` and a recipe `omniwin_do` ran itself are excluded, correctly (both keep their own books).
- **M2, M4, M6 done.** Coverage: `artifacts/benchmarks/recipe-wording-coverage.json` (165 must-run phrases, 0 missed, 0 wrong). M3 and M5 stay open as noted.
- **`text_insert` (36 tools), read:** shares `PasteAsync` with `selection_replace` (gate as a `type` of the text, password fields and protected programs refused, non-text clipboard refused, earlier clipboard text put back, `confirmed` only on read-back). Documented: if text is selected at the caret, the paste replaces it. **M7 (minor):** `PasteAsync` uses `CancellationToken.None` for the gate and clipboard calls, so a caller's cancel is not seen until Ctrl+V (the stop key still reaches the key send). `Contract26LiveTests` has no `text_insert` test yet.
- **App speech (`src/OmniWin.App/Speech.cs`), read:** recording only between two hotkey presses, in memory, 120 s at most; the temp WAV is deleted after each call; `whisper-cli.exe` is the owner's own download (the app never downloads a program); voice commands only fill the command box. **S1 (fix before the first model download):** models come from `huggingface.co/ggerganov/whisper.cpp/resolve/main/…`, which is the official repository but a moving branch, and the file is not checked: no pinned revision and no SHA-256. Pin a commit and check each file's SHA-256 (as Handy's catalog does) before renaming `.part` to the model name; a mismatch deletes the app's own `.part` file and reports it. The `whisper-cli` flags are unverified (builder's own note).
- **Scope (for the owner):** the interim app, and now voice and dictation in it, were built without asking the owner, under "take the recommended option". Owner decision 7 named Handy's source. It is harmless where it sits (only when opened, nothing at start-up), but it is an addition the owner has not seen.
- **Docs updated by the reviewer:** contract (header, §8 `text_insert` row, 36 tools, §19 note with the new L1 to L3 behaviour), `docs/TOOLS.md` (learning rules, `text_insert`, 36 tools), `docs/INSTALL.md` (new section "The OmniWin app (optional, interim)").
- **Still open:** S1; all live checks (`Contract26LiveTests`, plus the app's windows, hotkeys, microphone and `whisper-cli`), which need the owner connected over Remote Desktop and hands-off.

## Follow-up 3, 2026-11-16 (builder addendum 3): S1, M7, `text_insert` live test

- **Results and hashes match:** App.Tests 55 of 55 (`2026-11-16-app2-release`), Router.Tests 165 of 165 (`2026-11-16-router2-Release`); `OmniWin.App.dll` `80E6C7D2…D0B9` and `OmniWin.Server.dll` `B40CBCDF…48A1` equal their Release output; the other seven DLLs and both exes are unchanged. The full suites were not rerun after addendum 2's suites; the changes since are in the app, `SelectionTools.cs` and the learning files, which these two suites cover.
- **S1 fixed** (`src/OmniWin.App/Speech.cs`): every address is pinned to commit `5359861c739e955e79d9a303bcbc70fb988958b1`; size and SHA-256 are checked on the `.part` file before the rename; a mismatch deletes only that `.part` file and nothing is installed. **The reviewer compared all eight recorded sizes and SHA-256 values with the publisher's own file list for that commit** (`huggingface.co/api/models/ggerganov/whisper.cpp/tree/<commit>`, metadata only, no model downloaded): **8 of 8 match exactly**. Minor: a download cancelled or broken mid-stream leaves the app's own `.part` file until the next try overwrites it.
- **M7 fixed:** `PasteAsync` takes the caller's token; the clipboard restore and the settle delay deliberately do not.
- **Live test added:** `Contract26LiveTests.Text_insert_puts_dictated_text_at_the_caret_and_puts_the_clipboard_text_back` (F01 editor, the fixture's own log as proof, password field refused with no input event, the owner's clipboard text put back; skips and touches nothing when the clipboard holds non-text). Read; not run.
- **Nothing open in code review now** except M3 (for the first real decision adapter) and M5 (rich clipboard formats, no window check before read-back; documented). **Still to run:** every live check: `Contract26LiveTests` (4 tests), and the app's windows, hotkeys, microphone and `whisper-cli`. This needs the owner connected over Remote Desktop and hands-off.

## Follow-up 4, 2026-11-16 (builder addendum 4)

- The `.part` point from follow-up 3 is fixed: `SpeechModels.DownloadAsync` (`Speech.cs:60-84`) wraps the fetch and check, and on any failure (cancelled, broken mid-stream, failed check) deletes only its own `.part` file and rethrows. App.Tests 56 of 56 (`artifacts/test-results/2026-11-16-app3-release`). `OmniWin.App.dll` `234D5CA9…5016` equals its Release output; `OmniWin.Server.dll` is unchanged (`B40CBCDF…48A1`). Code review stays closed; only the live run remains.

## Follow-up 5, 2026-11-16 (builder addendum 5): the live run

- **Checked:** `Contract26LiveTests` 4 of 4 (`artifacts/test-results/2026-11-16-live26c`; the earlier tries `live26` 0 of 4 and `live26b` 3 of 4 failed on test mistakes, as the addendum says). Regression: `Contract25LiveTests` 11 of 11, `LiveSessionTests` 3 passed, 2 placeholders skipped. `artifacts/benchmarks/learning-v1.json`: first run 7 calls, 660 ms of OmniWin time with no AI thinking; replay from words 1 call, p50 281 ms, max 307 ms, 20 of 20 with one Apply click each in the fixture's log; drift stopped at step 3 with no click and was repaired as `-v2`.
- **Accepted deviation from handoff 26 §11** ("20 of 20 trusted replays confirmed"): the instant runs answer `unverified`, which is correct, because F01's Apply click changes nothing UI Automation can see. The fixture's own log (exactly one Apply click per run) is stronger proof than the status.
- **New finding R1: a replayed `type` step adds to text already in the field.** In the live run the Name box ended as "robin r1 r2 r3Dana1 …", and each replay still reported success, because the test did not check the field's value. **Reviewer's decision** (it follows the owner-approved rule "if something on screen differs, it stops at that step"):
  - **Rejected:** replay as `set_value`, and clear the field first. Both would wipe whatever is in the field when the recipe runs, and that field can be the owner's own document (a recipe learned in an empty Notepad, replayed into the owner's notes).
  - **Do this:**
    1. When a `type` step is recorded, store only whether the target field was **empty** before the typing: a new optional `WorkflowStep`/`RunStep` field such as `target_empty_before` (true, false or absent). Never store the field's text.
    2. On replay, when it is true and the field is not empty now, **stop at that step with nothing sent**: `stale reason=field_not_empty input_sent=false`, with the detail "the field already has text; the first run typed into an empty field". This is a new reason word, accepted by the reviewer.
    3. A stop after step 1 opens the repair as usual. A stop at step 1 gives `needs_brain reason=recipe_not_ready` with that detail (not "its first control is not on screen"). The AI then clears or edits the field itself, and that act is recorded, so the recipe learns it.
    4. When the first run typed into a field that already had text (false), replay appends as before. When it is unknown (absent: old workflows, or a field whose value cannot be read), behaviour is unchanged. Document both.
    5. Version: an optional field keeps version 1 files readable. Bump the version only if the field becomes required.
  - **Tests:**
    - Unit: empty-then-text stops with input_sent=false and nothing typed; empty-then-empty types; non-empty first run appends; the field's text is never stored.
    - Live: the 20 instant runs must also check the fixture's logged Name value equals that run's value. Empty the box between runs with a test-only fixture command, as with `rename-apply`. Add one run with text left in Name: it stops, nothing is typed and no Apply click is logged.
    - Then run `Contract26LiveTests` again (owner connected).

## Follow-up 6, 2026-11-16 (builder addendum 6): R1 fixed, benchmarks, second live run

- **Checked (results and hashes):** `Contract26LiveTests` 4 of 4 after R1 (`2026-11-16-live26d`; 20 runs with the logged Name equal to each value, and one run with text left in Name stopped with nothing typed and no Apply click). Router 171/171, Core 157/157, Desktop 400 (0 failed, `r1-desktop2`). Full Release suite on the R1 build: 1,603 / 1,538 / 1 failed / 64 skipped (`r1-full-release`); the failure was a load-sensitive poll count in `TypingEngineTests` (eased to at least 2 in the test only; the 500 ms elapsed check is kept), and Desktop.Tests then passed. The full suite was not rerun after that one-line test change. All 9 DLL hashes in `publish\` equal their Release output (Server `8D32452F…1D71`, Core `B17C36A6…010B`, App `256FBC55…91F6`; both exes unchanged).
- **Benchmarks on 2.6 (pre-R1 publish):** nine tasks 20 of 20 on B01 to B06, B09, B10 (B07 not applicable over Remote Desktop); clicks-v5 no-change whole call p95 326.1 ms (budget 360), expect-met server p95 68.1 ms; typing-v9 F01 7.88 ms/char and typing-v10 Notepad 11.46 ms/char, both confirmed with exact read-back. learning-v2: replay from words p50 267 ms, max 301 ms.
- **Not read by the reviewer this time (usage limit):** the R1 code itself (`IFieldProbe`, the replay stop, `FieldEmptyTests.cs`). The live and unit results above cover its behaviour; a code read is left for the next session.
- **Open:** the Handy-based app (build tools the owner must approve); nobody has used the OmniWin app's windows, microphone or speech program yet.

## Follow-up 7, 2026-11-16 (builder addendum 7): Part D, the app from Handy's source; R1 code read

- **R1 code read (left open in follow-up 6):** `StepRecorder.cs:74-89` records `target_empty_before` only for `desktop_act type` with a described target; `DesktopFieldProbe.cs` returns null for a password field (never read, not even its length), reads a value pattern's emptiness or one character of a text pattern, and stores only yes/no; `WorkflowService.cs:211-214` stops with `stale field_not_empty input_sent=false` only when it was empty and is now not empty. When the field cannot be read at replay (null) it types as before: accepted, same as "unknown". `RouterService.cs:263` gives the field-specific detail at step 1. Accepted.
- **Part D checked:** `publishpp\omniwin-app.exe` SHA-256 `719BB500…B723` matches the report. `app/src-tauri/src/omniwin.rs` refuses any tool outside its 9-tool allowlist before calling the server. `tauri.conf.json`: `productName` OmniWin, `identifier` `com.omniwin.app`, updater `pubkey` empty and `endpoints` empty; `update_checks_forced_disabled()` is used by the updater path. Rust 345 passed / 0 failed / 20 ignored and frontend build and lint, per the logs named in the addendum (the reviewer did not rerun them). The server already lists `omniwin-app.exe` as protected.
- **D1 (fix before the owner uses it): Handy's own hotkeys are on by default.** `app/src-tauri/src/settings.rs` (default bindings, about lines 960-990): dictation `ctrl+space` and post-process dictation `ctrl+shift+space` on Windows. Handy 26 §1.4 (owner-approved) says every feature is off until the owner turns it on. Ctrl+Space is also taken from other programs (code editors' suggestions, keyboard-language switching) whenever the app runs. Fix: both default to no key, like the three OmniWin bindings; the owner sets them in Settings or onboarding.
- **D2 (fix): the app still calls itself Handy when it talks to AI services.** `app/src-tauri/src/llm_client.rs:190-196` sends `HTTP-Referer: https://github.com/cjpais/Handy`, `User-Agent: Handy/1.0 (+https://github.com/cjpais/Handy)` and `X-Title: Handy` to every polish or post-process provider. Handy's name is not ours to use (handoff 26 §9). Use OmniWin, or drop the Referer and X-Title headers.
- **Notes for the owner and docs (no fix asked):**
  - Speech models come from Handy's own host (`blob.handy.computer`) with the SHA-256 values in Handy's catalog, only when the owner picks one.
  - A provider key the owner pastes into Handy's settings is stored in plain text under `publishpp\Data` (Handy's design; OmniWin's own rule reads keys from an environment variable).
  - Handy's own dictation paste does not go through OmniWin's shared input lane, so the owner dictating while an AI is typing can mix input.
  - The German translation replaced "Handy" (also "mobile phone") everywhere.
  - Unsigned and no installer, so SmartScreen may warn.
  - Handy's Playwright tests were not run.
  - The interim .NET app (`publish\OmniWin.App.exe`) is still there.
- **Not verified by anyone:** the app's windows, hotkeys, microphone, a model download, dictation, voice command and polish.

## Follow-up 8, 2026-11-16 (builder addendum 8): D1 and D2 fixed

- **D1 fixed:** `settings.rs` gives `transcribe` and `transcribe_with_post_process` an empty `default_binding` and `current_binding` (the old Windows keys remain only as unused `_`-prefixed names). The builder reports that a saved settings file keeps its keys and onboarding offers Change. The builder removed `publishpp\Data`, which only its own start checks had created.
- **D2 fixed:** `llm_client.rs:188-189` sends `User-Agent: OmniWin/0.1` and no Referer or X-Title. No Handy header is left in the Rust source; only code comments citing Handy issue numbers remain.
- **Checked:** `publishpp\omniwin-app.exe` SHA-256 `1F4F5C32…8D6F` matches; `artifacts/app-handy-tests-2.log` ends "345 passed; 0 failed; 20 ignored".
- **Code review of contract 2.6 and Part D is closed.** Left for the owner: first use of the app (windows, hotkeys, microphone, a model download, dictation, voice command, polish).

## Follow-up 9, 2026-11-16 (builder addendum 9): window title

- **Checked:** `index.html` `<title>OmniWin</title>`, `lib.rs:1025` `.title("OmniWin")`, tray label from `version_label()`; `publishpp\omniwin-app.exe` SHA-256 `22315028…4E46` matches; no `Data` folder left in `publishpp`. The other "Handy" strings left in the source are code names and comments, which nobody sees.
- **D3 (small fix): Handy's mascot is still drawn.** `app/src/components/icons/HandyMascot.tsx` ("Handy's mascot") is used through `shared/Mascot.tsx` on the first two onboarding screens (`onboarding/screens/Welcome.tsx:97`, `TryIt.tsx:634`), the first thing the owner sees. Handy's brand assets must be replaced (handoff 26 §9). Draw the OmniWin mark there, or remove the mascot.
- **Note:** the builder could not open the app's UI with its own tools (the portable app is not a Start-menu app) and rightly did not add a shortcut without the owner. The owner's first use stays the UI test.

## Follow-up 10, 2026-11-16 (builder addendum 10): D3 fixed

- `HandyMascot.tsx` now draws only the OmniWin ring mark (two circles, `currentColor`), and `Mascot.tsx` no longer waves. `publishpp\omniwin-app.exe` SHA-256 `F1536FE0…9770` matches; no `Data` folder left. **No brand asset of Handy's is left on screen, as far as the source shows.** Code review of 2.6 and Part D stays closed. Open: the owner's first use of the app; moving OmniWin to another PC (builder offered a self-contained publish, waiting for the owner).

## Follow-up 11, 2026-11-16 (builder addendum 11): self-contained publish, moving guide

- **Checked:** `publish\OmniWin.Server.runtimeconfig.json` lists `includedFrameworks` Microsoft.NETCore.App 10.0.14 (self-contained); `publish\` is 198 MB. No source file changed since the R1 build. The seven library DLLs equal the addendum-6 hashes and their win-x64 Release output. `OmniWin.Server.dll` is now `BECB22A4…1FAA`, equal to `bin/Release/…/win-x64/OmniWin.Server.dll`. It differs from the tested non-RID build (`8D32452F…1D71`) only because it was built for one runtime, from the same source. The builder's smoke test (36 tools, no machine .NET) and the app link tests 3 of 3 ran on it. Accepted.
- **Moving guide (`OmniWin/MOVING_TO_ANOTHER_PC.md`, the builder's file), corrected by the reviewer:** it said to copy `tools` to the old path while step 1 suggests `C:\OmniWin\publish`. But `EngineCatalog.ResolveToolsDir` finds `tools` only by walking up from the server's own folder (or through `engines.tools_dir` or `OMNIWIN_TOOLS_DIR`), so those engines would not be found. The line now says to keep the old layout, or to put `tools` next to `publish`. `docs/INSTALL.md` has a new "Moving to another PC" section pointing to the guide.

## Closed, 2026-11-16

The owner declared the project done. Nothing is waiting for the builder or the reviewer. Open for the owner: first use of the app (`publishpp\omniwin-app.exe`), which nobody has tried yet; bugs found there come back as a new round.

