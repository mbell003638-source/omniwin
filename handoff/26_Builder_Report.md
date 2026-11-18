# OmniWin: builder report for handoff 26 (contract 2.6)

Date: 2026-11-15/16. Built from `26_Self_Learning_and_App_Handoff.md` after the owner said "build it, take the recommended option at every decision". Parts A, B and C are built, tested without real input, and published. **The live checks have not run** (the Remote Desktop session was `Down` the whole time). **Part D (the app) is not started**: it needs build tools only the owner can install.

## Result in one look
| Part | State |
|---|---|
| A. Learning core | built; unit-tested; **live test written, not run** |
| B. Decision-model slot | built; tested with a scripted stand-in; no real model wired (none chosen) |
| C. Selected-text tools | built; unit-tested; **live test written, not run** |
| D. The app from Handy's source | **blocked**: Rust, CMake, Microsoft C++ Build Tools and the Vulkan SDK are not installed; the C++ Build Tools need an administrator install (checked 2026-11-16: no Visual Studio, no Windows SDK, and the build shell is not elevated) |

- Builds: Release and Debug, `-warnaserror`, 0 warnings.
- **Release full suite:** 1,532 total, 1,469 passed, **0 failed**, 63 skipped (`artifacts/test-results/2026-11-15-final-release`). Round 25 counted 1,437: 84 tests are new this round, and the 11 round-25 live tests were added after that count.
- **Debug full suite:** 1,532 total, 1,468 passed, **1 failed**, 63 skipped (`...-final-debug`). The failure is `ForegroundAndPumpTests.AwaitTypedScalar_gives_up_quietly_after_its_time_when_the_caret_is_stuck` (an elapsed-time assertion, line 414; code not touched this round). Run alone in Debug it passed 3 of 3. I report it as a timing flake under full-suite load, not as fixed. **Follow-up 2026-11-16:** cause found and the test fixed (test only, no product change): it counted how many 2 ms pauses fit into 60 ms using Thread.Sleep, which Windows rounds up to its timer tick (about 15 ms, more under load). The test now times its own pause. The whole Desktop.Tests project then passed in Debug and in Release (400 total, 395 passed, 0 failed, 5 skipped each; .trx in artifacts/test-results/2026-11-16-desktop-Debug and -Release). The full Debug suite was not run again as a whole.
- The 63 skips include the 11 `Contract25LiveTests` and the 3 new `Contract26LiveTests` (checked in the .trx: all 14 `NotExecuted`); live tests skip without `OMNIWIN_LIVE_SESSION=1`. I did not break down the other 49.
- Edge 0 before and after every run. Published `publish\` (35 tools); smoke over stdio PASS on the published exe; it exits when stdin closes.

## What was built
**A. Learning** (`src/OmniWin.Server/Learning/`: `LearningService.cs`, `RecipeStore.cs`, `RecipeTemplate.cs`, `LearningTools.cs`).
- `omniwin_do` opens an episode on `needs_brain`, lists up to three similar recipes, and adds a `learn:` line. `task_done` saves the recorded steps as a workflow plus a recipe card; events are appended to `recipes\events.jsonl`; trust is computed from them (3 clean replays). A trusted, low-risk, desktop-only recipe runs from the exact wording with new values. A replay that stops part-way opens a repair; the next `task_done` saves `-v2`. A wording the AI used for a recipe is learned as an alias. `workflow_manage`: disable, enable, right, wrong_pick. `workflow_list`, `omniwin_status` and `omniwin_guide` (topics `recipes`, `selection`) updated.
- One input lane for every OmniWin process in the session (`src/OmniWin.Desktop/MachineLane.cs`, a lock file Windows frees when the holder dies).
- Decisions I took where the handoff left a choice (please review):
  1. **Window titles are generalised** in learned steps: `invoice 42 - Word` is saved as `* - Word`; a title without ` - ` stays exact. Found by reading the resolver: it matches the saved title, so a recipe learned in one document would not have found the app with another one open.
  2. **Slot values**: at most 5 words and no joining word (and, then, also, but, after, before, plus), so "fill the form for bob and then close it" goes to the AI. The value keeps the user's spelling when it is found once in the request.
  3. **The phrase table no longer swallows learnable requests**: "open invoice 42 in word" reads as "open an app", fails with nothing sent, and now falls through to recipes and then to the AI with an episode. `template_conflict` only counts rules that take no free name.
  4. **Newest recipe wins** for the same wording (the older is `superseded`), so a match is never ambiguous.
  5. A first step that finds nothing and sends nothing gives `needs_brain reason=recipe_not_ready` and is **not** counted against the recipe.
  6. A request with a line break or a brace never runs a recipe (both found by the labelled set: each had run one).
  7. An act that is not in the run log makes the episode `noisy`; the recipe is still saved (it has to earn trust by replaying cleanly).
**B. Decision slot** (`DecisionService.cs`): off by default; act / shadow rules, shadow scoring, forced shadow after a wrong pick, remote models blocked without `allow_remote`. **No adapter exists**: any provider name other than `none` reports "no adapter in this build". For a recipe with one text parameter the value is the single run of request words not found in its wordings; recipes with two or more parameters are never run by the model.
**C. Selected text** (`src/OmniWin.Server/Tools/SelectionTools.cs`, `src/OmniWin.Desktop/DesktopSelection.cs`): `selection_get`, `selection_replace`, `polish_prompts`. Replace pastes through the clipboard after the gate check, reads back, and restores the earlier clipboard text; it refuses when the clipboard holds non-text. **No clipboard fallback** for apps without a text pattern (they get `unsupported_action` with advice). MCP "sampling" was **not** checked.
**For Part D, already in the server:** `OmniWin.Server --validate-config <file>`; `OmniWin.App.exe` / `omniwin-app.exe` are protected processes; `prompts.json` path.

## Tests added (all without real input)
`tests/OmniWin.Router.Tests`: 80 to 157 tests. `LearningTests` (26), `RecipeWordingTests` (21), counts from the .trx. `LearningTests` (learn, trust, instant run, refusals, noisy, drift and repair with old files byte-identical, wording aliases, manage, append-only, privacy, config), `RecipeWordingTests` (a **238-phrase labelled set, 165 that must run and 73 that must not: 0 wrong recipes, 0 wrong values**; the test allows up to 10% of the must-run phrases to be missed and passes, but does not print how many were; plus the whole no-run set through `omniwin_do` with no input sent), `DecisionTests` (17), `SelectionTests` (13). `tests/OmniWin.Desktop.Tests/MachineLaneTests` (4, one with a real second process that is killed while holding the lane).
The labelled set found two real mistakes, both fixed: a request containing `{name}` and a two-line request each ran a recipe.

## Not run, not verified (the honest list)
1. **Everything live.** `tests/OmniWin.Integration/Contract26LiveTests.cs` (3 tests) is written and compiles but has never run: learn by hand on F01, 3 replays, 20 instant runs with the fixture's own Apply count as proof, the "Rename target" drift, repair to `-v2`, selection read and replace with clipboard restore, and the password field. It also writes `artifacts/benchmarks/learning-v1.json`. So **the speed-up is still unmeasured**, and `selection_get` has never read a real control. Expect first-run fixes.
2. Nine tasks, clicks and typing were not re-run on 2.6 (the act path changed only by the extra lane and the episode hook).
3. Two server processes typing against each other on the real desktop (the lane is unit-tested with real file locks and a real second process, not with two servers sending input).
4. The audit log was not inspected for goal text after a real `omniwin_do`/`task_done` (by code, `goal`, `template`, `text` and `params` are not in the logged keys).
5. Part D, entirely.

## Reviewer-owned files I touched
- `tests/OmniWin.Integration/Harness/Contract.cs`: five tools added to the always-registered list.
- `OmniWin_Handoff/02_Architecture_and_Contracts.md`: appended section 19 "Contract 2.6 details" (new reason words listed there for you to accept or rename: `learning_off`, `no_episode`, `too_few_steps`, `too_many_steps`, `too_many_recipes`, `risky_words`, `template_mismatch`, `template_conflict`, and the `needs_brain` reasons `recipe_not_ready`, `model_unavailable`).
- New file `tests/OmniWin.Integration/Contract26LiveTests.cs`. `docs/*` not touched: `docs/TOOLS.md` and `docs/RUNNING_TESTS.md` need the 2.6 tools and the new live class.

## Downloads and installs
None. For Part D a read-only check found: Node 24 and npm present; **not present**: `rustc`, `cargo`, `cmake`, `bun`, the MSVC compiler (`cl`), the Vulkan SDK. Handy's Windows build is MSVC-based (its `build.rs` stages the MSVC runtime), so there is no route without the C++ Build Tools, and those need an administrator install the owner has to approve at the Windows prompt.

## Hashes (SHA-256 of `publish\`; each DLL equals its Release build output)
| File | SHA-256 |
|---|---|
| OmniWin.Browser.dll | A4F58C4574CB7EB50F0F6C02F2A17E35E2D150A4984BFBFA34DFFF8170AB70A9 |
| OmniWin.Core.dll | B9B7E72F2E528E50E2B5BABE28C388780DD0BF6D9C18158C8B50092AEF126D8A |
| OmniWin.Safety.dll | D4C546C58559626C1F180A7F428B51248854F70B79F8CDF646DC761C3F2AD0A2 |
| OmniWin.Desktop.dll | B6D6C0817F62A739DE9097AD03323E0780C8C1ED2B7DEF2EFD2C7FF0FE7D487C |
| OmniWin.Server.dll | 3CCC6AB60B09099BB018D1EDC5855848BAF810294CDB8273A9F9BAC0BD01DD87 |
| OmniWin.SystemTools.dll | D4AF98944FD8DECF388964515410492CAA454D88A1FA2151DB572DE24935F3C2 |
| OmniWin.Ocr.dll | 4B997545C9DAFC8AE6EDF28F019CEA83B4477BA6797DF198E7A195A0A126F536 |
| OmniWin.Workflows.dll | AE7DD51E5DB4DC62208DDA0E22091C13B93396ABD9EDDF4DC64686DD10F39366 |
| publish\OmniWin.Server.exe | 30AED1E984758B6DAE89D3BCE95E3D732AD9BAD2B49DDEA092DD6493CF8BBA75 |

## What is needed from the owner
1. **Connect over Remote Desktop and stay hands-off** for a few minutes, so the live checks can run (`query user` must show `Active`).
2. **For the app (Part D):** install, or say yes to, Microsoft C++ Build Tools (administrator), CMake, the Vulkan SDK and Rust. Or decide that the app should be built another way.

## Addendum 2026-11-16: Debug suite, and an interim app built with the tools already here

**Full Debug suite re-run** after the test fix: 1,532 total, 1,467 passed, **0 failed**, 65 skipped (`artifacts/test-results/2026-11-16-full-debug`). Two more tests skipped than in the earlier Debug run (63); I did not find out which two.

**Part D, interim (decision taken under "take the recommended option"):** Handy cannot be built here (no Visual Studio, no Windows SDK, no Rust, no CMake; the shell is not elevated; installing Microsoft's Build Tools needs an administrator approval and a licence acceptance I will not give for the owner). So that the owner has a way to use learning without an AI client open, I built a small app with .NET, which is installed: `src/OmniWin.App` (WinForms), published as `publish\OmniWin.App.exe` beside the server. **It is not the Handy-based app of decision 7 and has no dictation, no voice and no speech models.** Those still need the build tools.
- **What it has:** a tray icon (Command box, Polish selected text, Settings, Quit); a command box that sends the typed request to `omniwin_do`, shows the result in plain words, offers Copy when it "needs your AI", and Right / Wrong after a learned task ran (`workflow_manage`); an optional hotkey for the box and one for polish (both **off** until ticked in Settings; never Ctrl+Alt+Shift+K); polish-by-hotkey through `selection_get`, an OpenAI-style chat address (a model on this PC, or a hosted one only with the owner's tick; the key comes from an environment variable) and `selection_replace`; a settings window for learning, quick commands, engines, the decision model, the hotkeys and the polish model.
- **How it stays safe:** it holds no automation logic; it starts its own `OmniWin.Server.exe` and talks MCP to it, so every check applies. OmniWin's settings are saved only after `OmniWin.Server.exe --validate-config` accepts the candidate file; `power_tools.*` is shown and never written. When the box gets `needs_brain` it calls `task_done(success=false)` so no half-done task is learned. The process name is on the server's protected list. It starts nothing with Windows.
- **Tests:** `tests/OmniWin.App.Tests`, 40 tests, 0 failed (`artifacts/test-results/2026-11-16-app-release`): plain-word results (a result is never upgraded), hotkey rules, settings, the config editor against the real server's validation (rejected settings change nothing; untouched keys are kept), the MCP link to a real server (read-only calls, restart after a stop), the polish pipeline and the chat request with stand-ins. One real bug found and fixed: a wrong server path was reported as "the server stopped answering".
- **Not verified:** the windows themselves. No one has seen the tray icon, the box or the settings window; the hotkeys, the box stepping aside before a request runs, and polish against a real model were never exercised (the session was disconnected, and no model address is configured). The published app was started once and stayed up for 4 seconds without crashing, then was closed; that is all that is known about it live. Expect layout and behaviour fixes on first use.
- Publishing the app did not change the server files: `OmniWin.Core/Safety/Server/Desktop.dll` in `publish\` still equal their Release build output, and the published server still passes the smoke test with 35 tools. `publish\OmniWin.App.exe` SHA-256 `E0187D4C6F4FF148264EA4D4FC5978B0F61C3FB96CA5F5B439AEFC8770C23177`, `OmniWin.App.dll` `C7D99387DE02225AE9F2CF4975B79AC2F3AD93CB3D98155D545D58984E730828`.
- The solution now has two more projects (`src/OmniWin.App`, `tests/OmniWin.App.Tests`). The full solution suites above were run before they were added; only the app tests were run after.

## Addendum 2, 2026-11-16: review 27 fixes, `text_insert`, voice and dictation in the app

**Review 27 fixes (all in `src/OmniWin.Server/Learning/*` and `Hosting/RunLogFilter.cs`).**
- **L1:** a wording is no longer learned at replay time. The replay (name, values) is kept on the episode; the alias is added only in `task_done(success=true)`, and only when exactly one clean replay and **no other act** (before or after it, recorded or not) happened in the episode. Otherwise `learned=false` with "The saved recipe was only part of this task ...". No `task_done`, or `success=false`: nothing is learned.
- **L2:** a noisy episode gives `auto_run_eligible=false`; such a recipe never runs without the AI, however often it replays cleanly.
- **L3:** `RunLogFilter` now reports every tool outside the run log whose first line says `input_sent=true` (engine_call, selection_replace, text_insert, a quick command through omniwin_do, power tools) as an untracked act: the episode becomes noisy. `workflow_run`, and a recipe `omniwin_do` ran itself (its answer starts with `workflow_run `), are excluded because those replays keep their own books. (Without that exclusion a stopped instant run would have made its own repair episode noisy.)
- **M2:** the labelled-set test prints and writes its coverage: `artifacts/benchmarks/recipe-wording-coverage.json`: 238 phrases, 165 must run, **0 missed**, 0 wrong recipe, 0 wrong value.
- **M4:** the alias cap counts aliases only (10), whether or not the recipe has a main template.
- **M6:** `RecipeStore.Append` returns whether the line was written; `workflow_manage` answers `status=error reason=engine_timeout ... retry=safe` and "The note could NOT be written ... Nothing changed" when it was not. Other callers still treat the event as bookkeeping.
- **M1** was already done (the test times its own pause). **M3** and **M5** are not done (M3 belongs to the first real adapter; M5: rich-text clipboard content is still not preserved, and there is no window check before the read-back).
- New tests for these in `LearningTests`: wording only on task_done success and only when the recipe was the whole task (four cases), ten-alias cap, noisy never auto-runs after trust, an untracked act makes the task noisy, what the filter counts, a note that cannot be written is an error (the events file is held open by the test).

**New tool `text_insert(text)` (36 tools).** For dictation: pastes text at the caret of the focused control through the same path as `selection_replace` (gate asked as for a `type` with the text, refused for password fields, protected programs, a delete command in a terminal and a non-text clipboard; earlier clipboard text put back; `confirmed` only when read back at the caret; an app without a text pattern gets the paste and `unverified`). `SelectionService.ReplaceAsync` and `InsertAsync` share one `PasteAsync`. `Harness/Contract.cs` has one more line. Three unit tests. Never run on a real control.

**Voice and dictation in the app (`src/OmniWin.App/Speech.cs`).** Built with what is here, so that decision 7's features exist in some form before the Handy build is possible:
- A microphone recorder over Windows' own wave-in interface (no library), 16 kHz mono, in memory only, at most 120 s, recording only between two presses of a hotkey.
- Speech to text by running whisper.cpp's `whisper-cli.exe` on this PC (the recording is a temp file for the length of the call, then removed).
- Eight Whisper models to choose from (tiny 75 MB to large v3 3 GB; addresses and sizes as in OpenWhispr's registry). **Nothing is downloaded by the build or by me**: a model comes only from the Settings window's "Download this model" button after a confirmation; the owner sets the path of `whisper-cli.exe` themselves.
- Dictation hotkey: the words go to `text_insert`. Voice-command hotkey: the words are put into the command box for the owner to check and run with Enter; nothing spoken runs by itself. Both hotkeys are off by default.
- **This is not Handy's catalog** (76 models, Parakeet and others): only the Whisper family, through the command-line program, so it is slower per utterance than an engine that stays loaded.
- **Not verified at all on real hardware:** the recorder has never opened a microphone, `whisper-cli.exe` has never been run (the flags `-m -f -nt -np -l` are from memory of whisper.cpp, not checked against a binary), no model has been downloaded, and the hotkeys and windows have never been used. The tests cover the logic with stand-ins only.

**Tests after all of this.**
- Full suites on one build (before the small M6 change): Release 1,592 total, 1,529 passed, **0 failed**, 63 skipped; Debug the same (`artifacts/test-results/2026-11-16-final-release`, `...-final-debug`). Edge 0 before and after; no server or app process left.
- After M6: Router.Tests 165 of 165 in Release and in Debug (`artifacts/test-results/2026-11-16-router-Release`, `-Debug`). App.Tests 53 of 53 (Release, run before M6; the app was not changed by M6). The full suites were not run again after M6.
- Published; smoke PASS on the published server (36 tools); the published app started and stayed up 4 s, then was closed.

**Hashes after addendum 2 (SHA-256 of `publish\`; each DLL equals its Release build output).** Only `OmniWin.Server.dll` and `OmniWin.App.dll` changed since the table above.
| File | SHA-256 |
|---|---|
| OmniWin.Browser.dll | A4F58C4574CB7EB50F0F6C02F2A17E35E2D150A4984BFBFA34DFFF8170AB70A9 |
| OmniWin.Core.dll | B9B7E72F2E528E50E2B5BABE28C388780DD0BF6D9C18158C8B50092AEF126D8A |
| OmniWin.Safety.dll | D4C546C58559626C1F180A7F428B51248854F70B79F8CDF646DC761C3F2AD0A2 |
| OmniWin.Desktop.dll | B6D6C0817F62A739DE9097AD03323E0780C8C1ED2B7DEF2EFD2C7FF0FE7D487C |
| OmniWin.Server.dll | B12D4BAD0804ABD4D6D80A28F6F4E63FDCCE90065344D8C683BAE2A450D93596 |
| OmniWin.SystemTools.dll | D4AF98944FD8DECF388964515410492CAA454D88A1FA2151DB572DE24935F3C2 |
| OmniWin.Ocr.dll | 4B997545C9DAFC8AE6EDF28F019CEA83B4477BA6797DF198E7A195A0A126F536 |
| OmniWin.Workflows.dll | AE7DD51E5DB4DC62208DDA0E22091C13B93396ABD9EDDF4DC64686DD10F39366 |
| OmniWin.App.dll | 943CBF034C122CF8B3EBE03E41C2343BAAD7948405E6E65802109418F85785AA |
| publish\OmniWin.Server.exe | 30AED1E984758B6DAE89D3BCE95E3D732AD9BAD2B49DDEA092DD6493CF8BBA75 |
| publish\OmniWin.App.exe | E0187D4C6F4FF148264EA4D4FC5978B0F61C3FB96CA5F5B439AEFC8770C23177 |

**Still open.** All live checks (the session has been `Down` throughout; `Contract26LiveTests` has never run, and it has no test yet for `text_insert`). The app's windows, hotkeys, microphone and speech program. The Handy-based app (needs build tools the owner must install). `docs/*` and the contract need `text_insert` and the changed L1 to L3 behaviour (reviewer).

## Addendum 3, 2026-11-16: review 27 follow-up 2 (S1, M7, live test for text_insert)

- **S1 done.** Each speech model's address is pinned to commit `5359861c739e955e79d9a303bcbc70fb988958b1` of `huggingface.co/ggerganov/whisper.cpp` (never `main`), and `SpeechModels.All` records each file's size in bytes and SHA-256. Those eight values were read from that repository's own public file listing (`/api/models/ggerganov/whisper.cpp` and `.../tree/main`, 2026-11-16); **no model file was downloaded, so the values are the publisher's, not hashes I computed**. `DownloadAsync` hashes the `.part` file and compares size and SHA-256 before the rename; on a mismatch it deletes only its own `.part` file and throws a plain message, which the Settings window shows. Tests: a matching file is kept; one changed byte, a cut-off file and a 404 install nothing and leave no `.part`; an existing model is not replaced by a download that fails its check; every list entry is pinned, has a 64-hex checksum, and a shown size within 10% of the real one.
- **M7 done.** `PasteAsync` takes the caller's token (gate and clipboard calls). The restore of the earlier clipboard text still uses no token on purpose, so it happens even when the call is cancelled.
- **Live test added, not run:** `Contract26LiveTests.Text_insert_puts_dictated_text_at_the_caret_and_puts_the_clipboard_text_back` (F01 editor, the fixture's own log as proof, clipboard restore, and a refused insert into the password field). The class now has 4 live tests; none has ever run.
- **Tests:** App.Tests 55 of 55 (`artifacts/test-results/2026-11-16-app2-release`); Router.Tests 165 of 165 (`...-router2-Release`), both Release. The full suites were not run again after these changes (they touched `SelectionTools.cs`, `Speech.cs`, `SettingsForm.cs` and tests only).
- **Republished.** Smoke PASS, 36 tools. Changed hashes: `OmniWin.Server.dll` `B40CBCDF37BE04F02451E91D57CDE491BA8370B960CFC8800EBEFBB46A5848A1`, `OmniWin.App.dll` `80E6C7D253DE3D848561341FE7ECFA17EFEC588D30B1CD792FDDC076A536D0B9` (each equals its Release build output). The other seven DLLs are unchanged from the addendum 2 table; the two `.exe` files were not re-hashed this time.
- Session still `Down` (checked after publishing). No live run was started.

## Addendum 4, 2026-11-16: partial download file (reviewer's minor point from follow-up 3)

A model download that is cancelled, breaks mid-stream or fails its check now always removes its own `.part` file (one try/catch around fetch and check in `SpeechModels.DownloadAsync`). New test: a stream that fails after 100,000 bytes and a cancelled download both leave the folder empty. App.Tests 56 of 56 (`artifacts/test-results/2026-11-16-app3-release`). Only the app was republished: `OmniWin.App.dll` 234D5CA9C93986D30C40457BC3CE7F503CF3DA52D0B7E19AC08A6FCB48E85016 (equals its Release build output); the server files were not touched.

## Addendum 5, 2026-11-16: the live run (session Active, owner hands off, Edge 0 before and after)

The owner connected (session 3, `rdp-tcp#0`, Active). **All 4 `Contract26LiveTests` pass** (`artifacts/test-results/2026-11-16-live26c`). It took three runs; both earlier failures were mistakes in my test, not in the product:
1. First run, 4 of 4 failed: the tests named controls of the wrong fixture (`Editor`, `Toggle`, `Secret`, `Rename target` exist in an old fixture file; the harness starts the scenario app, whose F01 has `Name`, `Agree`, `Apply`). Fixed the names; the password check moved to the F15 scenario (`WinForms secret`), and F01 got one test-only stdin command, `rename-apply`, that relabels Apply as "Save changes" (its clicks stay logged as `Apply`).
2. Second run, 1 failed: the test demanded `status=confirmed` for each instant run. **The product answered `unverified`, correctly**: clicking F01's Apply changes nothing UI Automation can see, so OmniWin cannot confirm it. The fixture's own log shows exactly one Apply click per run, which is the proof the test uses. The test now accepts confirmed or unverified with `steps=3/3`.

**What the live run shows** (`artifacts/benchmarks/learning-v1.json`):
- A task done once by hand (7 tool calls), saved with `task_done`, became trusted after 3 replays, then **ran 20 times from the user's words in one call each**, each with its own value, each with exactly one Apply click in the fixture's log.
- **Timing:** first run by hand 660 ms of OmniWin time with **no AI thinking** (a test drives the calls back to back); replay from words p50 **281 ms**, max 307 ms. So the measured gain here is 7 calls to 1. Each of the 7 calls of a real first run also includes the AI's thinking, which this test cannot measure; the real speed-up is therefore larger than these numbers show, by however long the AI thinks per step.
- **Drift:** after Apply was renamed, the instant run stopped at step 3 with **no click arriving anywhere**; the AI-side finish plus `task_done` saved `-v2`, which earned trust and then ran from the words again.
- **Selected text:** `selection_get` read the real F01 selection; `selection_replace` replaced it, read it back as `confirmed`, the fixture logged the new text, and the earlier clipboard text was back afterwards; a reused `sel` id sent nothing.
- **`text_insert`:** dictated text was inserted at the caret, `confirmed`, logged by the fixture, clipboard restored.
- **Password field (F15):** `selection_get` and `text_insert` were both `denied`, nothing sent, the secret not returned, and the password box logged no change.

**Regression check on the 2.6 build (shared input lock now in every act):** `Contract25LiveTests` 11 of 11, `LiveSessionTests` 3 passed + 2 placeholders skipped (`artifacts/test-results/2026-11-16-live-*`). No process left; Edge 0.

**Found by the live run, not fixed (needs a decision):** a learned `type` step **adds** to text already in the field; it does not replace it. In the run the Name box ended as "robin r1 r2 r3Dana1 ...". That is what the step did the first time (typing into an empty box), but for a form that already has text it is wrong. Options for the reviewer/owner: replay `type` into a field as `set_value` when the first run typed into an empty field; or clear the field first when the first run started empty; or leave it and document it. The test was written to expect appending, so it passes either way.

**Still not done:** the app's own windows were not exercised by me (OmniWin refuses to drive OmniWin's own app by design, and real input must go through repo tests); the owner can now try them. The Handy-based app still needs the build tools. The nine-task, click and typing benchmarks were not re-run on 2.6.

## Addendum 6, 2026-11-16: benchmarks on 2.6, review 27 R1 (type into a non-empty field), second live run

The owner said "go ahead" with the session Active (session 3) and hands off. Edge 0 before and after every run; no process left.

**Benchmarks on the 2.6 build** (before R1; R1 changes only learned replays):
- Nine tasks (`bench/reports/2026-11-16-omniwin-tasks-v1.json`, 20 runs each): B01 to B06, B09, B10 **20 of 20 passed**, 0 failed, 0 not run; B07 not applicable over Remote Desktop, as before.
- Clicks `artifacts/benchmarks/clicks-v5.json` (40 per case): expectation met server p50 45.1 ms, p95 68.1 ms; no-change 40 of 40 "No visible change.", whole call p50 323.4 ms, p95 326.1 ms (budget 360).
- Typing: `typing-v9` F01 1,000 characters and `typing-v10` Notepad 1,000: both `confirmed`, read back exactly (7.88 and 11.46 ms per character). The Notepad window was closed by the benchmark.

**R1 as decided in review 27 follow-up 5.**
- `RunStep`, `RunStepDraft` and `WorkflowStep` gain an optional `target_empty_before` (true / false / absent). It is recorded only for a `desktop_act type` with a described target, from a new read-only `IFieldProbe.FieldIsEmptyAsync` (UI Automation value or text pattern; a password field is never read and gives "absent"). Only the yes/no is stored, never the text. Version 1 files without the field still load (tested).
- On replay, `true` and the field now has text: the step stops with `status=stale reason=field_not_empty input_sent=false` and "The field already has text; the first run typed into an empty field. Nothing was typed." At step 1, `omniwin_do` answers `needs_brain reason=recipe_not_ready` with that detail. After step 1, the normal repair applies. `false`: types as before (appends). Absent: no change.
- New reason word `field_not_empty` in `ReasonCodes` (accepted by the reviewer).
- Unit tests (`tests/OmniWin.Router.Tests/FieldEmptyTests.cs`, 6): learned empty and the field has text now (stops, nothing typed, the text kept), the same from the user's words (needs_brain, nothing sent), learned empty and the field empty now (types), learned non-empty (appends), the field could not be read (no change; an old file without the field loads), and only yes/no stored (the field's text is in no file). Router.Tests 171/171, Core.Tests 157/157, Desktop.Tests 400 (395 passed, 5 skipped).
- Fixture: a second test-only stdin command for F01, `clear-name`.

**Second live run:** `Contract26LiveTests` 4 of 4 (`artifacts/test-results/2026-11-16-live26d`). The learning test now empties Name before each replay and checks that the fixture's logged Name **equals** that run's value (20 of 20), and has one run with text left in Name: `needs_brain reason=recipe_not_ready`, nothing typed, no Apply click, the text unchanged. Timing (`artifacts/benchmarks/learning-v2.json`): first run by hand 693 ms (no AI thinking), 20 runs from words p50 267 ms, max 301 ms.

**Final full Release suite on the R1 build:** 1,603 total, 1,538 passed, **1 failed**, 64 skipped (`artifacts/test-results/2026-11-16-r1-full-release`). The failure was `TypingEngineTests.A_character_whose_original_never_lands_...` (round-23 code, not changed this round): it counted at least 6 polls in a 500 ms wait, and under the loaded run only 4 fitted although the wait took 1,484 ms. Passed 3 of 3 alone. Fixed in the test only (the count is now "at least 2"; the 500 ms rule is the elapsed-time assertion, unchanged); Desktop.Tests then 400 / 395 passed / 0 failed / 5 skipped (`...-r1-desktop2`). The full suite was not run again after that one-line test change.

**Republished** (smoke PASS, 36 tools). Every DLL changed because `OmniWin.Core.dll` changed (new reason word and contract fields):
| File | SHA-256 |
|---|---|
| OmniWin.Browser.dll | 2FBF9ED31949F4DEB6F2D5C2C4DF1A445876F6B1E64506FE5E069F820E41CEF0 |
| OmniWin.Core.dll | B17C36A6C2A7651DB064B1BF165EAB11E79C6191B8FA5C3946F4F2CCD0B9010B |
| OmniWin.Safety.dll | 3269D65DA0DBAA5CAAEFC21E70399E437A51B1C8DF076AD149708F8FDD38AAD3 |
| OmniWin.Desktop.dll | 64F9D81D605FA3597EAB16C67F3ED7A2962603E5A75F8D21D72EB35FE1FB0385 |
| OmniWin.Server.dll | 8D32452F59732C8574ABB347032D598F13BD4ACDC85C42AEB82B2C063B711D71 |
| OmniWin.SystemTools.dll | 06DAC15A9037E249002143BEA729472F6733897DCC479FEDF3336474A4DF31F6 |
| OmniWin.Ocr.dll | 681070B72D482361C6FE162A695542C337CE384EED0976E91C3ADCD7F240F7E7 |
| OmniWin.Workflows.dll | D591998093D3009CD140273E4756345DD8927F8E69AAA6C8AF38AC01D6B3F98D |
| OmniWin.App.dll | 256FBC55DDBA94E5A74A252CDF8497844C73E27FA1376F834FE85157B4EF91F6 |
Each equals its Release build output.

**Still open:** the Handy-based app (needs Microsoft C++ Build Tools, Rust, CMake and the Vulkan SDK; the Build Tools need an administrator approval); the OmniWin app's windows, microphone and speech program have not been used.

## Addendum 7, 2026-11-16: Part D, the OmniWin app built from Handy's source

The owner installed the build tools (checked read-only: Rust 1.100.0, CMake 4.4.4, MSVC 14.44, Windows SDK 10.0.26100, Vulkan SDK 1.4.363.0, Bun 1.4.4 under the winget package folder, not on PATH). **Part D is built.**

**Source.** Handy at commit `aca8985190c3ad7f361d8a80cb30f179513ed690` (2026-11-14, v0.10.1, MIT), copied into `OmniWin\app` from the earlier read-only clone with `git archive` (no `.git`). `LICENSE` kept; `NOTICE.md` lists the changes. JavaScript packages installed with `bun install --frozen-lockfile` (348 packages, as Handy's lock file pins them); Rust crates came from crates.io during the build (Handy's `Cargo.lock`). Handy's voice-detection model is part of its source and matches its catalog SHA-256 (`0d871a80…88e6`); nothing else was downloaded.

**Proof of the toolchain first:** an unchanged build of Handy succeeded (`artifacts/app-build-baseline.log`, 15 min 39 s).

**What was changed** (all in `OmniWin\app`):
- `src-tauri/src/omniwin.rs` (new): the app starts its own `OmniWin.Server.exe` (found through `OMNIWIN_SERVER`, beside the app, or in a `publish` folder above it) and talks MCP over stdio; it may call only `omniwin_do`, `omniwin_status`, `task_done`, `workflow_list`, `workflow_manage`, `selection_get`, `selection_replace`, `text_insert`, `polish_prompts`. Two new frontend commands, `omniwin_call` and `omniwin_server_path` (added to `src/bindings.ts` by hand in the generator's format; a debug run would regenerate the file the same way). Polish selection: `selection_get` → Handy's selected post-processing prompt and provider (`llm_client::send_chat_completion`) → `selection_replace`.
- `actions.rs`: three actions. `omniwin_voice_command` is Handy's transcribe action with the paste replaced by "open the command box with the words" (nothing runs until Enter). `omniwin_command_box` shows the window on the OmniWin page. `omniwin_polish` runs the polish above.
- `settings.rs`: the three bindings have **no key by default**; `shortcut/mod.rs` and `handy_keys.rs` never register a binding without a key. Update checks default off and are forced off (`update_checks_forced_disabled` always true).
- `tauri.conf.json`: `productName` OmniWin, `identifier` `com.omniwin.app`, `mainBinaryName` `omniwin-app`, no updater endpoints or key, no updater artifacts, Handy's signing command removed.
- Branding: app icons generated by `bun tauri icon` from `scripts/make-omniwin-icon.py` (a plain ring mark drawn without libraries); the 12 tray PNGs replaced; `HandyTextLogo` / `HandyHand` now draw the OmniWin mark; "Handy" replaced by "OmniWin" in all 26 language files (**note: in German "Handy" also means mobile phone; every German occurrence was replaced, which may read oddly in a few places**).
- Frontend: a new **OmniWin** page (first in the sidebar): command box (Enter runs `omniwin_do`; plain-language result; Copy when it needs the AI, and then `task_done(success=false)` so nothing half-done is learned; Right / Wrong after a learned task), the three hotkey fields, the learned tasks (`workflow_list`), the server path, and the safety line. English strings added; other languages fall back to English.
- The tool scripts `build-omniwin-app.ps1`, `test-omniwin-link.ps1` and `test-app-all.ps1` set the tool paths for their own run only; nothing system-wide was changed.

**Built and published.** `publish\app\omniwin-app.exe` (SHA-256 `719BB500D7F574848E39B41EC252B5FB52CB74B35A20FBD990409B2ED82DB723`, rebuilt after the lint fix; build log `artifacts/app-build-omniwin-2.log`) (release, no installer) with Handy's 13 speech-engine DLLs, its `resources` folder, `LICENSE-Handy.txt`, `NOTICE.md`, and a `portable` marker, so **all its data (settings, models, history) stays in `publish\app\Data`, not in AppData**. Started hidden twice for 8 s each: it stayed up; its log shows the speech engine loading the **Vulkan backend (the Intel Arc iGPU) and the CPU backend**, no errors; the first settings file shows the three OmniWin hotkeys empty and update checks off; nothing was left running.

**Tests.**
- All of the app's Rust unit tests, Handy's own and three new ones for the OmniWin link: **345 passed, 0 failed, 20 ignored by Handy** (`artifacts/app-handy-tests.log`). The link tests ran against the real `publish\OmniWin.Server.exe`: a tool outside the list is refused before anything starts; `omniwin_status` and `polish_prompts` answer; after a stop the server starts again; no server process was left.
- Frontend: `tsc` + `vite build` pass; ESLint clean after one fix (the logo's product name is now a constant, not a translatable literal).
- Handy's Playwright browser tests were not run (they need a browser download).

**Not done or not verified.**
- **Nobody has used the app's windows yet**: the OmniWin page, the hotkeys, a real microphone, a speech model download, dictation, the voice command and polish against a real provider. The first run shows Handy's onboarding, which asks to pick and download a speech model (the owner's choice).
- Handy's default dictation hotkey (`Ctrl+Space`) is unchanged and active whenever the app runs.
- The interim .NET app (`publish\OmniWin.App.exe`) still exists; the owner can use either. The plan's Handy-based app replaces it.
- The app's polish uses Handy's post-processing prompts, not OmniWin's `prompts.json` (the AI route); the two lists are separate.
- No installer, unsigned; Windows SmartScreen may warn the first time.

## Addendum 8, 2026-11-16: review 27 follow-up 7 (D1, D2) and a frontend test

- **D1:** Handy's two dictation hotkeys (`transcribe`, `transcribe_with_post_process`) now also have **no key by default**, like the three OmniWin hotkeys; a binding without a key is never registered. Only `cancel` (Escape) keeps its key, and Handy registers it only while recording. Onboarding already reads the key as `current_binding ?? ""` and its demo offers "Change" to set one; `formatKeyCombination("")` gives an empty string (checked). A settings file the owner already saved keeps its keys: Handy fills in only missing bindings from the defaults (unchanged code). **The `publish\app\Data` folder created by my own start checks was removed**, so the owner's first start gets the new defaults; the final start check confirmed all five hotkeys empty and then its data folder was removed too.
- **D2:** requests to model providers now send `User-Agent: OmniWin/0.1` and no `Referer` or `X-Title` (they named Handy's project page).
- **Frontend test:** the answer reader moved to `src/lib/omniwinResult.ts` (behaviour unchanged) with `src/lib/omniwinResult.test.ts` (`bun run test:omniwin`): done, unverified never shown as done, recipe names from both rules, stopped, needs-AI with suggestion, refused, failed. Passes; Handy's `test:keyboard` passes; ESLint clean; `tsc` + `vite build` pass.
- **Tests:** all Rust unit tests 345 passed / 0 failed / 20 ignored (`artifacts/app-handy-tests-2.log`).
- **Republished:** `publish\app\omniwin-app.exe` SHA-256 `1F4F5C325AFC089B48B077C2A5831B49694C15F964B401FCD170EC39B0F28D6F` (build log `artifacts/app-build-omniwin-4.log`); started hidden 8 s, stayed up, nothing left running.
- Still not done: no person has used the app's windows, hotkeys, microphone, model download, dictation, voice command or polish.

## Addendum 9, 2026-11-16: window title, and why the app's UI was not driven by me

- Starting the app with its window shown revealed the **window title was still "Handy"** (`lib.rs` `.title("Handy")`), as were the page `<title>` (`index.html`) and the tray tooltip (`tray.rs`, "Handy v…"). All three now say OmniWin. Rebuilt (`artifacts/app-build-omniwin-5.log`) and republished: `publish\app\omniwin-app.exe` SHA-256 `223150283ED54A04E1044B667C9CD349FF8531BE3FB01DFA8113E771BD824E46`. A start with the window shown confirmed the title "OmniWin"; the process was closed and its `Data` folder removed.
- Still named "Handy" and left alone on purpose: test data in Rust unit tests (custom-word examples) and a macOS autostart test path; they do not reach the user.
- **I tried to exercise the app's window with the computer-use tool, and could not:** it grants access only to apps found in the Start menu or by name, and this portable app is in neither. Adding a Start-menu shortcut would be an install-like change the owner has not approved, and OmniWin's own tools refuse the app by design. So the OmniWin page, the command box, the hotkeys, the microphone, a model download, dictation, voice command and polish are still **untested by anyone**. The owner can try them, or approve a Start-menu shortcut so the computer-use tool can drive the window (a microphone and a model download would still need the owner).

## Addendum 10, 2026-11-16: D3, Handy's mascot

`src/components/icons/HandyMascot.tsx` (Handy's mascot glove, shown through `shared/Mascot.tsx` on the onboarding Welcome and TryIt screens) now draws the plain OmniWin ring mark in the theme's logo colour; the wave animation was removed (the "slap" animation class is kept for TryIt's re-keyed effect). ESLint clean, `tsc` + `vite build` pass. Rebuilt (`artifacts/app-build-omniwin-6.log`) and republished: `publish\app\omniwin-app.exe` SHA-256 `F1536FE07F9179B4388FD37253635156ADCF3ECBBBECA1CBE8F587D752FC9770`; started hidden 8 s, stayed up; its `Data` folder removed; nothing left running. Not seen on screen by anyone yet.

## Addendum 11, 2026-11-16: self-contained server, and a move guide

- The owner said yes to bundling .NET. `dotnet publish src/OmniWin.Server -c Release -r win-x64 --self-contained true -o publish` (it downloaded Microsoft's win-x64 runtime packs from NuGet once). `publish` now holds `coreclr.dll`, `hostfxr.dll` and the framework (`OmniWin.Server.runtimeconfig.json` lists `includedFrameworks`); the folder is about 198 MB.
- Checks: the stdio smoke test passed with `DOTNET_ROOT` pointed at a folder that does not exist and `DOTNET_MULTILEVEL_LOOKUP=0` (36 tools); the app's Rust link tests against this server, 3 of 3; nothing left running.
- Hashes now: `OmniWin.Server.dll` `BECB22A48D7330CD44DC7F8BE78D9B70AB0466DFE1D02F9A68018E5C81A24FAA` (RID-specific build), `OmniWin.Server.exe` `30AED1E984758B6DAE89D3BCE95E3D732AD9BAD2B49DDEA092DD6493CF8BBA75` (unchanged). The other OmniWin DLLs in `publish` were not re-hashed after this publish.
- The interim `OmniWin.App.exe` in the same folder is still framework-dependent (needs the .NET 10 Desktop Runtime); the Handy-based app does not use .NET.
- New owner guide `OmniWin\MOVING_TO_ANOTHER_PC.md` (linked from `README.md`): the parts and where they live, what a new PC needs, six steps, what does not move cleanly (the Python engines under `tools` remember their path; learned tasks depend on the screen), and a check list.
