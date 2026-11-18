# OmniWin: mixed-engines build report (contract 2.5)

Date: 2026-11-13/14. Built from `OmniWin\PLAN_mixed_engines.md` under the owner's approval ("approve, don't stop until finished"; power tools OFF; both engines; Laya added). Compile-only during the build; one test round at the end.

## Result
- Debug and Release builds with `-warnaserror`: 0 warnings, 0 errors.
- **Final full suites on the finished code:** Release 1,437 total / 1,386 passed / **0 failed** / 51 skipped; Debug the same. `.trx` in `artifacts/test-results/2026-11-13-final-release` and `...-final-debug`. Edge 0 before and after every run. The 51 skips are the usual 49 plus two `RealCaptureTests` that skip while the Remote Desktop session is `Disc`.
- New: `tests/OmniWin.Router.Tests` (80 tests): router (evaluated against 615 + 178 labelled commands), guide, engine catalog and filters, bridged results, power tools. Also new tests in Core (retry), Safety (drop target), SystemTools (media play/pause), Ocr (label painting), Desktop (drag/hover, lane).
- Published `publish\` (30 tools by default); smoke over stdio PASS; exits when stdin closes. No engine process left after a run.
- The Remote Desktop session was `Disc` during the test round above, so the live checks were run afterwards, when the owner reconnected (session `Active`, hands off). Results are in "Live checks" below.

## Live checks (2026-11-14, session Active, owner hands off, Edge 0 before and after every run)
New file `tests/OmniWin.Integration/Contract25LiveTests.cs` (11 tests, all input goes through repo tests; each effect is checked against something independent of OmniWin's own answer: the fixture's own event log, `GetCursorPos`/`GetAsyncKeyState`, the audio read-back, or `windows_list`). `.trx` in `artifacts/test-results/2026-11-14-live` and `...-live-drag`.
- First run: 10 of 11 passed. The one failure was my test, not the product: it expected a selection event on Item 1, but the F01 list starts with Item 1 already selected, so pressing it logs nothing. Fixed the assertion (the list must end on Item 4); the drag test then passed, and the button was up afterwards.
- **hover:** channel `physical_hover`, `input_sent=true`, `retry=` present; the real cursor ended inside the fixture window, the left button was up, the fixture logged 0 clicks and 0 input events.
- **drag:** Item 1 dropped on Item 4 (`physical_drag`); the fixture's list ended on Item 4, the left button was up 300 ms later. A drop target that is not an element ref (`w2`) gave `blocked unsupported_action input_sent=false` and sent nothing.
- **`screen_view labels=true`:** after an observe, the reply names `labels=N refs_gen=<gen>` with N >= 10 and the same generation as the observe; a never-observed window and the whole screen are refused with nothing sent.
- **`omniwin_do`:** real volume set to a value, stepped up, mute, unmute (each read back by `audio`); the original volume and mute are restored in a `finally` (restore was executed; the test does not read it back a second time). Minimize, restore and "switch to" a window by its title, each checked in `windows_list`. An open-ended request and a risky one (`delete everything...`) came back as `needs_brain` (`no_rule`, `risky_words`) with `input_sent=false`.
- **Engines acting on the real desktop:** agent-desktop `desktop_snapshot` then `desktop_click` on the ref of Apply, and Windows-MCP `Snapshot` then `Click` at the coordinates it gave for Apply. Both reported `unverified`, `input_sent=true`, `retry=check_state` (never `confirmed`), and the fixture's own log shows exactly one Apply click each.
- OmniWin's own click after the engines ran worked (the shared input lane was free).

## Benchmarks re-run on the 2.5 build (2026-11-14, Release, real input, Edge 0 before and after each)
- Nine tasks (`bench/reports/2026-11-13-omniwin-tasks-v1.json`, 20 runs each): B01-B06, B09, B10 **20 of 20 passed**, 0 failed, 0 not run; B07 not applicable (Windows disables the Scale box over Remote Desktop, as documented); false_success, wrong_target, input_sent_mismatch and leftover_windows are 0 in every task. (Last round B07 was `not_run` because the owner's Settings window was open; this time it was recorded `not_applicable` like the docs say.)
- Clicks `artifacts/benchmarks/clicks-v4.json` (40 per case): expectation met server p50 46.0 ms, p95 64.8 ms (budget <= 120); no-change 40 of 40 "No visible change.", whole server call p50 322.5 ms, p95 345.3 ms (budget <= 360).
- Typing: `typing-v6` F01 1,000 chars, `typing-v7` F01 2,000, `typing-v8` Notepad 1,000: all `confirmed`, `retry=done`, read back exactly (7.75, 7.76 and 11.02 ms per character). The Notepad window was closed by the benchmark; 0 Notepad and 0 Edge processes remain.
- Left alone: 3 `%TEMP%\OmniWin-test-*` folders from older runs (the docs say they are the owner's to remove; nothing was deleted).

## What was built
1. `retry=` on every action result line (`RetryAdvice`), contract 2.5.
2. `omniwin_guide(topic)`; the `tools` topic reads the tools the running server really has registered.
3. `desktop_act` `hover` and `drag` (channels `physical_hover`, `physical_drag`; the button is always released; a drop onto the Recycle Bin or a delete action is denied via `ActionDescriptor.DropTarget`); `screen_view labels=true`; `media` `play`/`pause` (idempotent). `notify` was dropped (unpackaged toast needs a registered shortcut = system change).
4. Engine bridge: `engines_list` (always), `engine_call` (when an engine is installed). Windows-MCP 0.8.9 (85 packages pinned by hash, installed with `--require-hashes`) and agent-desktop 1.3.1 under `tools\`. Results never `confirmed`; an engine call holds the shared input lane (`IInputLane`); the stop key and timeouts end the process tree.
5. Power tools `shell`, `files`, `registry`, `process`: built, **off** (`power_tools.enabled=false`), not registered by default. Deleting/replacing needs `allow_delete`.
6. `omniwin_do`: phrase router (stage A) with a `needs_brain` reply; Laya (stage B) as a separate offline process, wired, **off**.

## Filters that make "power tools off" real
OmniWin applies them itself and also passes Windows-MCP `--exclude-tools`: Windows-MCP `PowerShell`, `Registry`, `FileSystem`, `Process` removed; arguments `App.executable/args/cwd` and `save_path` of `Screenshot`/`Snapshot` refused; agent-desktop `launch`, `batch`, `close_app`, `trace_export`, `sandbox_*` removed. Found during the build: windows-mcp needs `DiscoverProbeTimeout` raised to its start-up time (default 5 s made the client fall back to the old handshake, which that server refuses). **Both engines can still drive a terminal through the screen like any app**; only the dedicated command tools and arguments are filtered.

## Router numbers (reported, not hoped)
Strict table vs the easy set (written next to the rules): 473 of 482 commands answered. Vs hand-written natural paraphrases: **16 of 123 (13%)**, and 0 of 55 near-miss sentences would run. So the table is safe but narrow; the gap (107 of 123) is exactly what Laya, or the brain, would have to cover. The hard set found and fixed three real mistakes: "start the song again" / "go to the next song" matching window or app rules, and "deleted" not counting as a risky word.

## Laya: gate run, FAILED (after the owner approved the PyTorch download on 2026-11-14)
Laya offline gate, run 2026-11-14 on this PC (`artifacts/benchmarks/laya-eval-v1.json`; CPU, PyTorch 2.15.0, zero-shot, 793 labelled commands, no input sent): intent accuracy 61% (easy set 63%, hand-written hard set 55%); at the highest threshold tried (0.995) it still answers 33% of the commands and 4.8% of ALL commands would be run as the WRONG one (hard set 18% at 0.9); confident mistakes exist at confidence 1.00 (for example "find me a good thriller on netflix and play it" -> media_play), so no threshold makes it safe; median latency 827 ms (p95 928 ms; a 30-item trial gave 422 ms). All three gates failed (wrong-action <= 0.5%, accepted accuracy >= 95%, median <= 50 ms), so **Laya is not integrated and stays off** (`router.laya.enabled` false). The phrase router is the router. Not tried: fine-tuning (the vendor says it is needed; its repository has no training code) and GPU/NPU acceleration (no discrete GPU; the Intel Arc iGPU would need a separate PyTorch build).

### Earlier text, kept for the record: Laya before the gate
Weights are on disk and verified (typed-decisions, 842 MB; SHA-256 matches Hugging Face). Running them needs PyTorch + transformers in `tools\laya\.venv` (hundreds of MB to a few GB). The plan said to ask before that download, so it was NOT made. After it: `tools\laya\.venv\Scripts\python tools\laya\eval_laya.py` runs the offline gate (wrong-action rate <= 0.5%, accepted accuracy >= 95%, median <= 50 ms, hard set judged separately). This PC has no discrete GPU (Intel Core Ultra 5 125H + Arc iGPU), so the 50 ms gate may well fail on CPU; if it does, Laya stays off and Stage A remains. Zero-shot accuracy is claimed near chance by the vendor; fine-tuning code is not in the repo and was not written.

## Other things to know
- `uv python install` wrote a shim `C:\Users\<you>\.local\bin\python3.14.exe` (outside `tools`; not on PATH). Left in place. `tools\README.md` explains how to avoid it next time (`UV_PYTHON_BIN_DIR`).
- `tools\` is about 1.2 GB (Laya weights 842 MB). `tools\_dl` holds the two extracted archives; nothing deleted.
- Existing tests changed for the new line format and tool list: `tests/OmniWin.Core.Tests/ResultFormatterTests.cs`, `tests/OmniWin.Integration/FormatTests.cs` (regex), `tests/OmniWin.Integration/Harness/Contract.cs` and `SurfaceTests.cs` (three new always-on tools, optional ones allowed). Docs: `README.md`, `docs/TOOLS.md` and `OmniWin_Handoff/02_Architecture_and_Contracts.md` got an appended "Contract 2.5" section; the reviewer is asked to fold it in.
- Edited in place for 2.5: the focus-attempt busy detail now names what holds the lane (a type or an engine call).

## Hashes (SHA-256 of `publish\`; each DLL equals its Release build output)
| File | SHA-256 |
|---|---|
| OmniWin.Browser.dll | 8CF456F4471B4EBD3D73A795CF57F20B996366DE9287C892A040B637DF39D8B4 |
| OmniWin.Core.dll | C04C2D50D7E3064213F58F2654B9E844BBA9416AE6E54F66F6B8C2F43A5FC46F |
| OmniWin.Safety.dll | 78491FB571305C827D4A38C28BDE150A9702B2C1580218A388FD5B5F1DD36FC2 |
| OmniWin.Desktop.dll | 3816905243B1BFA287031EF27C0F98DC4ECBEF0B7880E0B683047B10FF785312 |
| OmniWin.Server.dll | 7B27AF2AF10E6BFEDF3A59191C23908B74F0073EDE831C3D6BFBCDD83CB1FB5F |
| OmniWin.SystemTools.dll | BD7A672BE22B53F39DE11C100A7C3AEBF510CE759F320E3C9B9DE9F8CE20DBA6 |
| OmniWin.Ocr.dll | 66FD8CAA4D2AE17FD53FCB3577F4BED7B77179890D71020BCBA295F8483140D3 |
| OmniWin.Workflows.dll | 2D80A82ADF2E88F3671D6CFAEE6AF5112E3FB6E3B9F4CDC6E56945B3AE5149D4 |
| publish\OmniWin.Server.exe | 30AED1E984758B6DAE89D3BCE95E3D732AD9BAD2B49DDEA092DD6493CF8BBA75 |

## Still open
- Not exercised live: `omniwin_do` pause/play/next against a real media session (nothing was playing; media toggling was avoided on purpose), and `engine_call` with a long-running or failing engine call (stop key and timeout paths are covered by unit tests only).
- Reviewer: please fold the appended "Contract 2.5" section into `docs/TOOLS.md` / the contract, and decide whether `Contract25LiveTests` joins `docs/RUNNING_TESTS.md` section 3.
- Laya stays off (gate failed); front end (hotkey box, voice) is deferred by the owner.
