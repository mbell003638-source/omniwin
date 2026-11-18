# OmniWin: full project completion handoff (M2 to M5)

Copy everything below the line and send it to the AI.

---

**The owner wants the whole project finished in one go.** Don't report in parts and don't stop between phases. Phase 0 is already done by the reviewer. Do Phases 1 to 5 in order, keep logs as you go, and send **one** final report (Phase 6). The reviewer checks once, after that report, so the files won't change under you.

You now act for **WS0, WS2, WS3 and WS4** together, so you may edit any project this work order needs. Every owner rule below still applies, and so do the stop-and-skip rules in section 3.

## 1. Where things stand (verified by the reviewer, 2026-11-08)

- **WS1 desktop:** accepted. Its tools are registered: `windows_list`, `desktop_observe`, `desktop_act`, `desktop_keys`, `desktop_scroll`.
- **WS2 browser:** M1 accepted, and the Q6 Risky label is in place. No browser tool is registered yet.
- **WS0 Safety:** the gate, stop key, user-activity check, audit filter and metrics are wired in `Program.cs`.
- **Not built:**
  - `OmniWin.SystemTools`, `OmniWin.Ocr` and `OmniWin.Workflows` are empty;
  - `wait_for`, `run_steps` and the `workflow_*` tools don't exist;
  - the WS4 end-to-end suite and benchmarks are incomplete.
- **Tests now:** 590 total, 584 passed, 6 opt-in skipped, 0 failed (Debug and Release), after the reviewer's Phase 0 changes.
- **Release hashes now:**
  - Browser `4F74D03C…DAD7`
  - Core `C2BFF80F…7248`
  - Safety `9AF2E85A…953F`
  - Desktop `6A05DD17…050D`
  - Server `B9480AB8…B74D`

Read first:
- `OmniWin_Handoff/02_Architecture_and_Contracts.md`, all of it;
- `03_Build_Orchestration.md` §2 and §8;
- `04_Status_Handoff.md` §5 and §7;
- the briefs `agents/WS2_Browser_Engine.md`, `agents/WS3_System_Tools_OCR.md` and `agents/WS4_Tests_Benchmarks.md`;
- `00_START_HERE_New_AI.md` §5 (owner rules) and §6 T3;
- `07_Reply_to_New_AI_Start_WS2.md` (B1–B7);
- `12_WS2_M1_Accepted.md` and `14_WS2_Q6_Accepted.md`.

Where this file and a brief disagree, this file wins. Where this file and the owner's rules disagree, the owner's rules win.

## 2. Owner rules (never break)

1. **Owner rules 1–8** (`00_START_HERE_New_AI.md` §5) and **browser rules B1–B7** (`07_Reply_to_New_AI_Start_WS2.md`), every one, all the time.
2. **Nothing is blocked except deleting.** No confirmation prompts. The Risky label only records.
   - The safety checks stay: the stop key, the user-activity pause, stale, focus, hit-test, disabled and dialog checks, the dedicated profile, and the process blocklist (`04_Status_Handoff.md` §5).
   - Don't weaken, bypass or remove any of them, in any project.
3. **No deletes in product code.** Tests may delete only the temp folders they created.
4. **No downloads or installs** of any software, package or tool beyond the existing NuGet references. That includes Python, `uv`, Windows-MCP, Node and Playwright.
5. **No system or security setting changes.** That includes Smart App Control, Defender, UAC, the registry, display scale and Transparency effects, as well as PATH, startup entries and file associations.
6. **No real websites,** except the two owner tasks in Phase 5 (YouTube search, Google Maps route), and only there.
   - On any consent or cookie banner, choose the reject or decline option.
   - Never sign in, accept terms, or enter personal data.
7. **The owner's files and real browser profiles are off limits.**
   - Test files are created by the test, under names starting with `OmniWin-test-`.
   - Don't move, edit or delete any other file.
8. **Real mouse or keyboard input happens only in Phase 5,** with the owner present and after their one yes. Everything before Phase 5 runs headless or without real input. Hold `Local\OmniWin.UiInputTestLock` for any test that sends input or shows a window.
9. **Report honestly.** List every failed run. Copy every number, name and line from the files after your last edit. Never call something done without the test or artifact that proves it.

## 3. Stop-and-skip rule (instead of asking)

If an item can only be done by breaking a rule in section 2, or needs a decision this file doesn't make:
- don't do it;
- **don't stop the whole work order;**
- add one line under "Owner decisions needed" in the final report (what, why, and your recommendation);
- continue with the next item.

## 4. Decisions already made for you (don't ask about these)

| Topic | Decision |
|---|---|
| **Contract** | You act as WS0 for contract changes. Record every change from this work order in `02_Architecture_and_Contracts.md` under "Changes in 2.4", and set `ContractVersion = "2.4"`. Update the C# samples to match the code. |
| **Q7 tabs as windows** | `window_opened` and `window_closed` work for browser tabs, as approved. |
| **Q8 page dialogs** | Never auto-accept. While a dialog is open, acts return `blocked modal_blocking` with the dialog text under the untrusted header. Add two `browser_nav` actions: `dialog_dismiss`, and `dialog_accept`. `dialog_accept` goes through the gate with the dialog message as the target name, so accepting a delete confirmation is denied by the existing no-delete rule. `dialog_dismiss` is always allowed. |
| **Q9 frames** | Index same-origin frames, each with its own isolated world. List cross-origin frames in `frames=N`; acting inside one returns `blocked cross_origin_frame`. |
| **Q11 tab list** | As approved: untrusted header, `tN` ids, active marker, quoted titles and URLs. |
| **Selector resolve** | `ResolveSelectorAsync` (browser, and desktop if it needs it) records its scan in history and returns the ref with its generation, so `workflow_run` can act on it. Interface change in Core. |
| **Audit for browser tools** | `ToolCallAudit.Redact` keeps `bN` refs and `tN` tab ids, and logs a URL as scheme and host only (no path, query or fragment). |
| **Speed** | No speed work in this order. Measure every §15 row and list every miss for the owner's single sign-off in the final report. The owner already accepted slower browser speed on bigger pages. |
| **WS3 questions Q1–Q10** | Use the "Proposed" answer in the WS3 brief §9, with the changes in the rows below. |
| **WS3: no confirmations** | `clipboard_get` is never confirmed. It's labelled Risky, its text gets the untrusted header, and the text is never logged. |
| **WS3: Risky labels** | `window_manage close` is labelled Risky (record only). |
| **WS3: blocklist** | The blocklist is the owner's (§5): the secure desktop, sign-in prompts and OmniWin itself. OCR denies and masks only those windows. |
| **WS3: `app_launch`** | It may start any Start-menu app or AUMID, terminals included (the owner allows terminals). Two design limits from contract §8 and 03 §8 stay: |
| | - it never passes `args` to a command processor or script host (`cmd`, `powershell`, `pwsh`, `wt`, `wscript`, `cscript`, `mshta`), because a command line there would get around the no-delete check that typed commands go through; |
| | - an arbitrary `.exe` path is refused unless it's in `app_launch_allow_paths`. |
| | Both are pre-existing contract rules, not new blocks. List them in the final report so the owner can see them. |
| **WS3: user-activity pause** | It applies to system acts that send real input or change the foreground window, and not to audio, media or clipboard. |
| **WS3: engine names** | `ActionDescriptor.Engine` is `system` for system tools and `ocr` for OCR. |
| **WS3: audit** | The existing `ToolCallAudit` host filter writes the audit line. |
| **WS3: run log (Q8)** | A system-tool act is recorded as a step with its tool and arguments. Typed or clipboard text is saved only as a named parameter placeholder, never as the text itself. |
| **Baseline** | Skip the Windows-MCP baseline, because it needs Python and `uv` downloads. Report OmniWin's own numbers and mark the comparison tables "not run: needs a download the owner hasn't approved". |
| **WS4 tasks that change settings** | Drop B08, which toggles Transparency effects; B07 only reads. Drop every manual-checklist item about the confirm dialog (it doesn't exist) and about 150 % display scale (a setting change). |
| **Fixture mapping** | Map the WS4 brief's F01–F17 and W01–W10 to existing fixtures where they match, add the missing ones, and record the mapping in `docs/status/WS4.md`. |
| **Packaging and clients** | `dotnet publish -c Release` to `publish/`, with no installer, registry, PATH or startup changes. Write `docs/INSTALL.md` with the exact MCP client config snippets. Register OmniWin in a client only in Phase 5, and only in the clients the owner names, with their yes. |
| **Desktop edits** | Edit `OmniWin.Desktop` only where this work order needs it, such as interface changes, `oN` refs from OCR, or the foreground result from WS3 Q2. Never change its input path or its guards. List every Desktop edit in the final report. |
| **Safety edits** | Don't edit `OmniWin.Safety` or the delete policy. If an item needs a Safety edit, use section 3. |

## 5. The work

After **every** phase:
- build Debug and Release with `-warnaserror`;
- run the full suite in both;
- get 0 failures, with no existing test removed, skipped or weakened without a written reason;
- add a dated entry to the right `docs/status/WSx.md`: what changed, the test names, counts, and failed runs.

Don't wait for anyone between phases.

### Phase 0: leftovers (already done by the reviewer; don't redo)

The reviewer did these on 2026-11-08 and logged them at the end of `docs/status/WS2.md`. Start at Phase 1. Leave these changes as they are:

1. **The two doc follow-ups from `14_WS2_Q6_Accepted.md`:**
   - the stale Q6 line in `WS2.md` is fixed;
   - the `ActResult` and `AuditEntry` C# samples in the contract now show `ExpectOutcome` and `Risk`.
2. **The guard follow-ups from `12_WS2_M1_Accepted.md`:**
   - **The hook:** `BrowserEngine.ConnectionCreated` (internal) runs on every new CDP connection, reconnects included, before its first command.
   - **Where it's used:** the browser tests and the benchmark install `NavigationGuard` through it.
   - **The guard test:** it uses `http://omniwin-guard-test.invalid/`.
   - **The new test:** `NavigationGuard_is_installed_on_every_new_connection_through_the_engine_hook`.
   - **For new code:** every new test or harness that starts a browser engine must set `ConnectionCreated` the same way.
3. **Contract §13 and §14** match the owner's policy. Leftover confirm-dialog wording in §4, §5 and §7 is marked as not used under the owner's policy.

### Phase 1: M2, every tool registered

1. **Browser tools** (`src/OmniWin.Server/Tools/BrowserTools.cs`):
   - register `browser_tabs`, `browser_nav`, `browser_observe` and `browser_act`, exactly as in §8 and the WS2 brief §4;
   - register the engine in `Program.cs` (lazy start) with its `IComponentStatus`;
   - handlers have timeouts, never throw, take the kill-switch-linked token, and use `ToolDocs` and `ResultFormatter`.
2. **Browser M2 items:**
   - Q7, Q8, Q9 and Q11 as decided in section 4;
   - the selector resolve change and the audit redaction;
   - `browser_not_connected` when the browser is gone.
3. **System tools and OCR (WS3):**
   - implement `OmniWin.SystemTools` and `OmniWin.Ocr` per the WS3 brief §1–§4, with the section 4 decisions;
   - register all nine tools in `Tools/SystemTools.cs` and `Tools/OcrTools.cs`;
   - OCR uses the built-in `Windows.Media.Ocr` only;
   - `oN` refs click through `desktop_act`, click only, and a capture older than 10 s is `stale`.
4. **`wait_for`:** routes to the desktop or browser engine by condition.
5. **`omniwin_status`:** reports every engine, including `browser attached`/`detached`, the system tools and OCR.
6. **Unit tests** for every new piece. Tests that would send real input or show a window are tagged and skipped until Phase 5.

### Phase 2: M2 and M3, the end-to-end suite and adversarial cases (no real input)

1. **The stdio harness** (`tests/OmniWin.Integration/`):
   - start the server with `--test-mode` and a test config in a temp data dir;
   - add the stdout guard;
   - ground truth comes from the fixture event logs, never from OmniWin's own output.
2. **Groups 1–8** from the WS4 brief §4. Run every case that needs no real input now: surface, formats, browser acts (headless), system reads, audit and redaction, the stop key, the "no repeated input" rule, and the safety cases.
3. **Adversarial:** one test per WS4 brief §3 case, asserting the exact status, reason and `input_sent`, plus the event log. Real-input cases are tagged for Phase 5.
4. **Benchmarks:** the micro benchmarks and the e2e latency runner for every §15 row. Write them to `artifacts/benchmarks/` and `bench/reports/` under new file names, and record each sample's status and `input_sent`.

### Phase 3: M4, run log and workflows (WS0, `OmniWin.Workflows`)

1. **Run log** per contract §12. `ActResult.StepId` is filled in.
2. **The tools:** `run_steps` (`stop_on_failure` defaults to true), `workflow_list`, `workflow_save` and `workflow_run`.
   - Saved workflows are declarative JSON under the data dir's `workflows\`, never code.
   - `workflow_run` resolves each selector fresh and uses the normal act path and gate. It stops at the first step that isn't `confirmed` and returns that step plus a current observation.
   - A saved workflow can't contain a step the gate would deny: saving re-checks every step.
3. **WS4 group 8 tests,** and the `workflow_run` per-step benchmark.

### Phase 4: M5, release build and docs

1. **`dotnet publish -c Release` to `publish/`.** On first run in a clean temp data dir, it creates its config and dedicated browser profile.
2. **Docs:**
   - `docs/INSTALL.md`: how to run it, and the MCP config snippets for Claude Code and Antigravity;
   - `docs/TOOLS.md`: every tool, its parameters and its result format;
   - update `README.md` and every `docs/status/WSx.md`.
3. **The 03 §8 checks** that need no real input:
   - no tool runs scripts, shells or arbitrary executables;
   - the audit log records every tool call;
   - every proposal is closed or listed for the owner.

### Phase 5: the live session (the owner present at the PC, not over RDP)

This is the only phase with real input.

1. **Ask the owner once, with the full list below,** how long it takes, and that they must leave the mouse and keyboard alone while it runs. Their yes covers exactly this list, in this one session.
2. **If the owner isn't available,** skip Phase 5 entirely and go to Phase 6. List Phase 5 as the one remaining step, with the exact command that starts it.

The list:
1. **The tagged real-input tests:** groups 3–7 desktop cases, adversarial real-input cases, the fixture app scenarios, and system acts such as `window_manage` and `audio`, which are read and restored.
2. **Accuracy gates over 20-run sets** (WS4 brief §4 table): 0 wrong-target actions, 0 `input_sent` mismatches, 0 false `confirmed`, suite success of at least 98 %, and 100 % of adversarial cases.
3. **The owner's 10 everyday tasks** plus the delete refusal (`04_Status_Handoff.md` §7), driven through the MCP tools in one server session:
   - use `OmniWin-test-` files only;
   - task 6 moves a test file the run created;
   - the delete refusal is tried on that test file;
   - tasks 4 and 5 are the only real websites.
4. **WS1 T3,** the physical-console click test from `00_START_HERE_New_AI.md` §6. Record the numbers the owner needs for decision D2.
5. **The real-input benchmarks** for the desktop §15 rows.
6. **MCP client registration,** only in the clients the owner names, with their yes.

Leave every test file where the run put it, and list them so the owner can remove them.

### Phase 6: one final report, then stop

Use these headings:

1. **Result:** Yes or No for each phase, and for each item in Phases 0–5.
2. **Changes:** per project, the files changed, with their purpose. List every edit in `OmniWin.Desktop`, `OmniWin.Core` and `OmniWin.Server` separately.
3. **Tool surface:** the tool list the harness read back from the server, compared with §8.
4. **Tests:** exact counts, Debug and Release, per project. Name every skipped test and give its reason.
5. **Failed runs:** every one, with what you changed.
6. **Live session:** what ran, the results, the test files left behind, or "not run" with the command to start it.
7. **Measurements:** every §15 row with p50, p95, budget and pass/fail, copied from the artifact files, with file names and keys. Then the accuracy-gate table.
8. **Rules check:** rules 1–8 and B1–B7, one line each, plus the section 2 rules of this file.
9. **Design limits kept:** the two `app_launch` limits from section 4, and anything else like them.
10. **Owner decisions needed:** every section 3 item, plus D2 (click budget) and the budget misses for sign-off.
11. **Hashes:** SHA-256 of every Release DLL and the published exe.

Then stop. The reviewer checks everything at once.
