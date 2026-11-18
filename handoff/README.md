# OmniWin Handoff

Everything a builder agent needs to start the OmniWin build.

> **New AI with no context: read `00_START_HERE_New_AI.md` first.** It has the current state, the owner's rules, the next tasks and the reply format.

## Read in this order

1. **`..\OmniWin_Master_Plan_and_AI_Prompt.md`** (on the Desktop): the plan, the reasoning, and what changed from v1.
2. **`02_Architecture_and_Contracts.md`**: the source of truth. Types, interfaces, tool list, text formats, safety rules, performance budgets, file ownership.
3. **`03_Build_Orchestration.md`**: workstreams, milestones, branch rules, the shared-desktop test lock, and kickoff prompts for each agent.
4. **`04_Status_Handoff.md`**: what's built, what's uncommitted, the owner's safety decisions (these override contract §13/§14), and what to do next.
5. **`05_Conversation_Summary.md`**: what the owner asked for and what was decided, in order.
6. **`06_WS1_Review_Findings_2026-11-07.md`**: the reviewer's open WS1 findings (F1–F6) and the pending owner decisions (D1, D2). WS1 builder: apply these next.
7. **`07_Reply_to_New_AI_Start_WS2.md`**: the WS2 kickoff, including browser rules B1–B7.
8. **`08_WS2_M1_Review_Findings.md`**: the reviewer's WS2 M1 findings (S1–S4, R1–R4, L1–L3).
9. **`09_WS2_Fix_Guide_Step_by_Step.md`**: the order to fix those findings, steps 0–12.
10. **`10_WS2_M1_Fix_Review.md`**: the reviewer's check of the fix round (N1–N6, report problems).
11. **`11_WS2_M1_Round3_Review.md`**: the reviewer's check of round 3 (P0, P1, P2, low items).
12. **`12_WS2_M1_Accepted.md`**: WS2 M1 accepted on 2026-11-08, with small follow-ups and open owner decisions. M2 waits for the owner.
13. **`13_WS2_Q6_Risk_Label_Task.md`**: owner-approved task to wire the Risky label into results and the audit log (record only, never blocks).
14. **`14_WS2_Q6_Accepted.md`**: the Risky label accepted on 2026-11-08 (589 tests pass), with two doc follow-ups and audit notes for M2.
15. **`15_Full_Project_Completion_Handoff.md`**: the single work order to finish the whole project (M2 to M5, all streams), with every open decision already made, one live session with the owner, and one final report. Done, finished by 19.
16. **`16_Addon_Look_Only_Screen_View.md`**: owner-approved add-on to file 15. Adds `screen_view` and `browser_view`, look-only screen pictures with passwords masked, as a fallback for pictures, charts and canvas apps. Also sets the fallback order (real buttons, text on screen, look-only view, detected boxes later), defers detected boxes because they need a download, and prepares the Windows-MCP comparison for later.
17. **`17_Review_Fixes_Before_Final_Report.md`**: reviewer findings F1–F10 from reading the M2–M5 code during the live session (paste gap, uninstall launch, screen-view masking). Done in the final round.
18. **`18_Final_Review_Round.md`**: reviewer findings from the 2026-11-09 run (161 browser-launch failures). Replaced by 19.
19. **`19_Final_Handoff_Finish_In_One_Go.md`**: the final work order (replaces 18). Browser launch fix first, then desktop and test work by parallel agents with separate files, one test run at a time, no leftover browsers, one final report. Done on 2026-11-10, except the live rerun.
20. **`20_Final_Report_Review.md`**: the reviewer's check of the final report. **Accepted.** The reviewer did follow-ups 2–4 itself, with tests.
21. **`21_Last_Round_Everything_Closed.md`**: work order for D1 in every file dialog, the F16 fix, D2 and T3 closed with Remote Desktop numbers, publish, and the live rerun. Done on 2026-11-10; checked in 22.
22. **`22_Final_Fix_Round.md`**: the reviewer's check of round 21, plus the final fix round. Covers the typing false `confirmed`, re-send limit, long text, focus fallback hardening, workflow `confirmed`, the harness closing only its own windows, honest metrics, the click benchmark in `bench/`, docs, and a full live run on the final code. Done on 2026-11-11; checked in 23.
23. **`23_Closing_Round.md`**: the reviewer's check of round 22, plus the closing round. The reviewer fixed the harness (click benchmark, honest metrics, own-window rules, Explorer checks, clipboard restore, `typing` command, data folders), the shared test lock, the workflow replay test and the docs. Left for the builder: the desktop engine items (typing settle and engine-level tests, a long `type` blocking other calls, the F24 re-check), the unanswered questions, targeted tests, and the live run. **Done and accepted on 2026-11-11** (builder report: `23_Builder_Report.md`). The reviewer checked every `.trx` count, the 9 hashes, the artifacts and the A8–A12 code; decided that `blocked rate_limit` is the code for "a type is running" (contract stays 2.4) and pinned it in `TypingEngineTests` (18 of 18 passed); and updated the docs. Two minor reporting gaps in `window_manage focus` went to entry 24.
24. **`24_Focus_Reporting_Fix.md`**: two small accuracy fixes in what `window_manage focus` reports (a stopped attempt's real reason and `input_sent`; a focus refused while a `type` runs). Targeted tests only. **Done and accepted on 2026-11-11** (builder report: `24_Builder_Report.md`). The reviewer checked the `.trx` counts, the 9 hashes and the code, added a kill-switch engine test (45 of 45 passed in the two classes rerun) and updated the docs. Nothing is waiting for the builder.
25. **`25_Mixed_Engines_Builder_Report.md`** (plan: `OmniWin\PLAN_mixed_engines.md`, the owner's own request): contract 2.5. `retry=`, `omniwin_guide`, `omniwin_do`, `engines_list`/`engine_call` bridging Windows-MCP 0.8.9 and agent-desktop 1.3.1, hover/drag, `screen_view labels`, media play/pause, power tools built but off, Laya wired but off. Full suites 1,437 / 0 failed; not run live. **Reviewed 2026-11-13:** counts, timing and the 9 hashes check out; the 2.5 section is folded into the contract (summary, §8, §13.5, §14, §18), `docs/TOOLS.md`, `INSTALL.md`, `README.md` and the status docs. Engine actions are not checked for deleting and do not pause for the user (contract §13.5); **the owner decided on 2026-11-14 to keep them on.** Laya: PyTorch installed under `tools\laya\.venv` only (recorded at the time as owner-approved; **the owner said on 2026-11-17 they never approved installing it**, and meant Laya only as an optional slot for a future model such as Jev or another third-party AI); the offline gate failed every criterion (`artifacts/benchmarks/laya-eval-v1.json`), so Laya stays off and `omniwin_do` uses the phrase table only (docs updated). Live checks done 2026-11-14 and checked by the reviewer: `Contract25LiveTests` 11 of 11, nine tasks 160 of 160, `clicks-v4` and `typing-v6/7/8` as expected, on the reviewed build (not republished). Nothing is waiting for the builder or the owner. Contract 2.5 is closed. Reviewer follow-ups 2026-11-14:
- `bench compare` (OmniWin against both engines, `docs/status/WS4.md`);
- a corrected `omniwin_guide` engines topic, republished. `OmniWin.Server.dll` is now `98AF9140…68D7`; the rest is unchanged from the builder's 2.5 hashes.
26. **`26_Self_Learning_and_App_Handoff.md`** (owner-approved scope, 2026-11-15) and **`26_Builder_Report.md`**: contract 2.6. Learned recipes (`task_done`, `workflow_manage`, exact-wording replay from `omniwin_do`), an optional decision-model slot (off, no adapter), selected-text tools (`selection_get`, `selection_replace`, `polish_prompts`), and one input lane for all OmniWin processes. 35 tools. Parts A to C built and published; Part D (the app from Handy's source) not started: it needs Rust, CMake, the Vulkan SDK and the C++ Build Tools (administrator), all for the owner to approve. **Not run live yet.** **Reviewed 2026-11-15** (`27_Contract26_Review.md`): Release 1,532 tests, 0 failed; the one Debug failure is a timing flake; the 9 hashes match. The contract, `docs/TOOLS.md` and `docs/RUNNING_TESTS.md` are updated. **Three fixes are needed before the live run (L1 to L3):** a wording is learned without `task_done`; a noisy recipe can run by itself; engine and selection acts don't mark a task noisy. **2026-11-16:** the Debug rerun has 0 failed. The builder is making L1 to L3 (the owner told it to continue) and added `text_insert` (36 tools) and an interim .NET app, `publish\OmniWin.App.exe` (not the Handy app; the reviewer read it and it looks safe, but nobody has seen it run). **L1 to L3 fixed and checked (2026-11-16):** Release and Debug 1,592 tests, 0 failed; the new hashes match. `text_insert` and the app's dictation and voice code read OK. S1 is fixed too: the model downloads are pinned and checksum-checked, and the reviewer matched all eight checksums with the publisher's list. Current hashes: `OmniWin.Server.dll` `B40CBCDF…48A1`, `OmniWin.App.dll` `234D5CA9…5016` (after the `.part` cleanup fix, follow-up 4). **Live run 2026-11-16 (owner connected):** `Contract26LiveTests` 4 of 4, `Contract25LiveTests` 11 of 11; a task done once in 7 calls replays from the words in 1 call (p50 281 ms, 20 of 20). **Open: R1.** A replayed `type` adds to text already in the field. The reviewer decided: stop at that step with nothing sent when the first run typed into an empty field and the field now has text; never clear the field (follow-up 5). **R1 fixed and rerun live (2026-11-16):** `Contract26LiveTests` 4 of 4, nine tasks 20 of 20, clicks and typing within budget; hashes match (Server `8D32452F…1D71`). The reviewer has not yet read the R1 code itself (follow-up 6). **Part D built (2026-11-16):** the OmniWin app from Handy's source is at `publishpp\omniwin-app.exe` (`719BB500…B723`), with Handy's Rust tests 345 passed and 0 failed. The reviewer has now read the R1 code too (follow-up 7). D1 and D2 are fixed (follow-up 8): every hotkey now starts with no key, and the app names itself OmniWin to AI services. The window title is now OmniWin (follow-up 9; app hash `22315028…4E46`). Handy's mascot is replaced by the OmniWin mark (D3, follow-up 10; app hash `F1536FE0…9770`). **Code review is closed; next is the owner's first use of the app.** **2026-11-16: the owner declared the project done**; they will try the app later and report bugs. Self-contained publish and the moving guide: review 27, follow-up 11.

28. **`28_Training_and_GitHub_Plan.md`**: the plan to teach OmniWin everyday tasks and publish it on GitHub (owner-approved 2026-11-17), with the results: 79 of 80 tasks and 621 wordings learned (57 run by themselves from the user's words), three replay bugs fixed by the reviewer, and the public repository https://github.com/mbell003638-source/omniwin (release v2.6.0).

## What's in this folder

| Path | Contents |
|---|---|
| `02_Architecture_and_Contracts.md` | Shared build contract |
| `03_Build_Orchestration.md` | Multi-agent build plan and kickoff prompts |
| `04_Status_Handoff.md` | Current state and owner decisions (2026-11-06) |
| `05_Conversation_Summary.md` | Summary of the conversation so far |
| `06_WS1_Review_Findings_2026-11-07.md` | Reviewer findings for WS1 speed and correctness, with owner decisions pending |
| `archive\OmniWin_Master_Plan_v1_original.md` | The original v1 plan, unchanged |
| `agents\WS1_Desktop_Engine.md` | Desktop engine brief (UI Automation, checked actions, foreground) |
| `agents\WS2_Browser_Engine.md` | Browser engine brief (dedicated profile, page element index) |
| `agents\WS3_System_Tools_OCR.md` | System tools + OCR fallback brief |
| `agents\WS4_Tests_Benchmarks.md` | Fixtures, end-to-end suite, benchmarks, baseline brief |

## Workstream briefs

Each brief in `agents\` covers one stream's scope, owned files, interfaces, tools, milestone deliverables, test plan, performance budgets, definition of done, open questions, and a kickoff prompt. The contract still wins on any conflict. WS0 (lead) has no separate brief: its scope is in `03_Build_Orchestration.md` §1 and §7.

## Contract gaps found by the brief-writing agents

Settled in contract 2.1 at M0 (see the top of `02_Architecture_and_Contracts.md`):

1. ~~No test config path.~~ The server takes `--data-dir <path>`; tests must always pass it. (WS4)
2. ~~Input stopped partway.~~ Use `status=error input_sent=true reason=input_interrupted`. (WS1, WS2)
3. ~~Re-check after confirm.~~ When the user confirmed, the engine resolves and pre-checks again before input; if that fails, the result is `blocked`. (WS1, WS2)
4. ~~Where shared interfaces live.~~ Every §7 interface is in `OmniWin.Core`. (WS1, WS3)
5. "Browser attached" in `omniwin_status`: settled. The browser engine registers an `IComponentStatus` named `browser` with state `attached`/`detached`.

Still open (each brief's §9 has details): the `browser_tabs` output format and its missing untrusted-content header; `close_tab` is rated Normal while `ctrl+w` is Risky. (WS2, WS3)

## Current state (2026-11-08)

- **WS2 Browser Engine M1 is accepted** (see `12_WS2_M1_Accepted.md`), and the **Q6 Risky label is in place** (see `14_WS2_Q6_Accepted.md`). Whole solution: 589 tests, 583 passed, 6 opt-in skipped, 0 failed in Debug and Release. Release Browser DLL `D09496E390936BF1E951C9481B5471F60700DD83B4088E66BAEB36052D04A658`. Owner policy: nothing is blocked except deleting; non-web links are allowed and labelled Risky in results and the audit log. M2 has not started.

The WS1 notes below are from 2026-11-07; the test counts there predate WS2.

- .NET 10 SDK 10.0.403 is installed.
- **WS1 Desktop Engine is built, validated, and accepted:** all 10 owner tasks and both delete refusals passed in a single server session.
- **Test suite:** 470 passed, 0 failed, 5 opt-in skipped in both Debug and Release configurations with 0 warnings and 0 errors.
- **Release Desktop DLL hash:** `EB52842A0B9035BFF5FA10F70E7B0800E440C25F28A5D8D350B1AE9D87EC889A` (updated following T1 retry rule addition; historical acceptance hash was `80C9E2C7FEEFCBD8B1CB3E507596CB1DB03A0588B370AF3D4029959322304D80`).
- **Notepad limitation:** RichEdit corrupts supplementary Unicode during packet delivery; the engine enforces scalar caret progress acknowledgement and rejects supplementary Unicode and newline packet text prior to input (use explicit `set_value` or separate Enter key actions).
- **Open speed items & D2:** Click dispatch (~31 ms p95), typing dispatch (~60 ms p95), and cold observation (~168–247 ms p95) exceed strict budgets; D2 (revisiting the 1 ms click-dispatch budget) is pending physical-console testing (T3).
- **Historical (2026-11-06 M0):** Solution skeleton, Core contracts, and stdio server with `omniwin_status` previously verified 30/30 passed. Measurements are in `OmniWin\docs\status\WS0.md`.
