# OmniWin — Status Handoff (2026-11-06)

For the next AI picking up the build. Read `README.md`, then `02_Architecture_and_Contracts.md`, then `03_Build_Orchestration.md`, then this file. Where the owner's decisions below differ from the contract, **the owner's decisions win**.

---

## WS1 integration update on 2026-11-07

Latest review follow-up: Debug/Release builds have zero warnings/errors; each normal suite passes 470 tests, fails zero, skips five opt-in scenarios (total 475). One uninterrupted Release server session passed all ten owner tasks and both deletion refusals. The live fixture and final 200-cycle benchmark passed separately. Current evidence: `OmniWin/docs/status/WS1_Final_Review_Acceptance_2026-11-07.md` and `OmniWin/docs/benchmarks/WS1_Review_Followup_2026-11-07.md`.

F3 no-op raise accounting and F5 scan cadence are fixed. F5 p95 improved 33.612→3.344 ms. Standard native Edit typing checks improved 120.307→14.229 ms p95; total 323.146→280.759 ms. Several budgets remain unmet (click dispatch ~31 ms p95, typing dispatch ~60 ms p95, cold observation ~168–247 ms p95); no budget was changed and physical-console testing (T3) / owner decision D2 remain pending (owner uses RDP).

Important provider limit: fresh Notepad windows also showed corruption with the older full-check path. The retained native RichEdit path acknowledges each supported scalar before continuing. Supplementary Unicode and newline packets are rejected before input; explicit set_value or separate Enter keys remain available. Final Unicode readback acceptance used real Chrome, while Notepad ASCII save/edit passed. Earlier broad Notepad Unicode statements below are historical. Final Release Desktop hash: `EB52842A0B9035BFF5FA10F70E7B0800E440C25F28A5D8D350B1AE9D87EC889A` (updated after T1 retry rule update; historical acceptance hash was `80C9E2C7FEEFCBD8B1CB3E507596CB1DB03A0588B370AF3D4029959322304D80`).

### Earlier validation history

All ten owner acceptance workflows in §7 now pass through OmniWin's stdio MCP tools. Real Notepad editing/saving, Chrome launch, YouTube search, Maps routing, Explorer file movement, Unicode readback, a physical menu click, word replacement, and window switching were exercised. Fixture and Explorer deletion attempts returned denied without input. Only dedicated test files were created/moved; the exact outputs and raw evidence paths are in `OmniWin/docs/status/WS1_Acceptance_2026-11-07.md`.

This acceptance run corrected shell/UWP window discovery, missing Save As controls, modern Notepad Unicode burst corruption, and WinUI foreground/frame activation. Stop/activity/desktop checks precede each mutating activation step and partial activation input remains accounted for. Click point reuse requires fresh bounds, native ownership, and exact UIA hits; the final checks and original pattern fallback remain.

Final Debug and Release builds pass with warnings as errors, zero warnings, and zero errors. Each normal test run passes 406 tests, fails zero, and skips three opt-in UI scenarios. Live fixture and benchmark scenarios passed separately. Smart App Control remains off by the owner's choice; runtime loading is unblocked. The supported server EXE reports `dpi=per_monitor_v2`; the ad hoc DLL-hosted acceptance launcher was corrected and window-switch/menu checks repeated through the EXE. See the acceptance report for this validation limit.

Functional WS1 acceptance is complete; strict input-latency acceptance remains open. Budget values are unchanged. WS2, WS3, workflows, and broader hung-provider/multi-monitor coverage are still pending. The following paragraphs record earlier stages, not the current everyday-task status.

Final manifest-aware Release benchmark: 200 measured cycles passed with exact readback. P95 click checks 11.902 ms, physical click dispatch 31.457 ms, key checks 8.858 ms, 100-character typing checks 120.307 ms, typing dispatch 59.960 ms. Click point reuse reduced redundant provider queries but did not meet the strict gate. Full details and hashed raw evidence: `OmniWin/docs/benchmarks/WS1_Final_Validation_2026-11-07.md`.

### Earlier WS1 validation history

The Release diagnostics follow-up passed a complete 200-cycle run with 100-character typing and multiple controlled pointer runs. Release p95: click checks 12.802 ms, click dispatch 31.618 ms, key checks 8.878 ms, 100-character typing dispatch 49.325 ms. Repeated per-batch focus queries dominate typing prechecks, not native Edit text capture. Pointer controls reproduce roughly 29 ms p95 when changing editor-to-button focus even with OmniWin's activity hooks and UIA event subscriptions absent; already-focused clicks are approximately 0.5 ms p95 with both enabled. Exact Windows/provider internals remain untraced. Production input checks, hooks, batches, budgets, and verification behavior were preserved. See `OmniWin/docs/benchmarks/WS1_Release_Diagnostics_2026-11-07.md`; speed acceptance and everyday tasks remain open.

Final Debug/Release builds both passed with zero warnings/errors; both normal automated runs reported 392 passes, zero failures, and three opt-in skips. Release benchmark and accepted pointer controls passed separately. All owned fixture processes were closed.

The subsequent repeated-input benchmark completed two runs, each with 10 warmup cycles and 200 measured clicks, typing operations, key operations, and editor focus operations. Both passed with real stop/activity hooks enabled and verified fixture output. Baseline checks p95: click 17.979 ms, typing 32.823 ms, keys 10.775 ms; physical click dispatch p95 30.785 ms. A smaller ancestor cache showed no useful latency improvement and was removed. The benchmark harness and reports are retained; engine source is unchanged by this follow-up. The automated suite now reports 392 passed, zero failed, and two opt-in skips. See `OmniWin/docs/benchmarks/WS1_Input_2026-11-07.md` for full measurements and profiling handoff. Everyday acceptance tasks remain untested.

The second review fixes are implemented in commit a602577. After the owner turned Smart App Control off, registry state 0 was confirmed and runtime validation succeeded: the solution built with zero warnings/errors; 392 automated tests passed, zero failed, and one opt-in live test was skipped. That live scenario passed separately, including typing/readback, physical clicking, deletion refusal, and covered-window background UIA actions. No source changes were needed to resolve the DLL-load blocker. Full performance acceptance remains open: the latest small fixture sample measured precheck p95 35.608 ms, physical click p95 30.269 ms (two samples), and cold/warm windows_list p95 73.790 ms (two samples). See `OmniWin/docs/status/WS1.md` for details.

The baseline is committed as 8c8bd34. The earlier first-review validation reported 329 automated tests passed and the extended live fixture passed separately; the results above supersede that run.

The repository now contains the desktop engine and its five MCP tools, with the existing Safety components wired into the host. Contract 2.2 incorporates the owner's policy from §5. The earlier M0 snapshot below remains as the historical starting point.

- All desktop interface methods are implemented, including foreground service, waits, descriptors, and selector resolution.
- Window and element identities are session-local and monotonic. Observations support retained-generation diffs.
- Input uses checked physical dispatch or a single UIA pattern channel. Keyboard input is focus-locked.
- Safety includes deletion-policy regression tests, the physical stop hotkey, real-input hooks, and redacted per-tool audit records.
- A minimal WinForms fixture and opt-in physical-input scenario cover text readback, Unicode input, clicks, toggles, selection, denial, modal dialogs, occlusion, scrolling, and stale references.
- The validation record and measured timings are in `OmniWin/docs/status/WS1.md`. Remaining browser, system/OCR, workflow, and broad benchmark work is tracked separately.

---

## 1. Why this handoff exists

The previous AI (Claude) stopped before building the desktop engine (the part that clicks and types). An automatic safety check reviews that AI's responses. It stopped the work twice on the same piece:
- the first time on a detailed brief for that engine;
- the second time partway through writing the engine's code.

Both times it said not to produce that content again. This file therefore covers only the current state and the owner's decisions; it adds no instructions for that engine. The contract and the existing briefs in `agents\` are unchanged.

---

## 2. Repository

| Item | Value |
|---|---|
| Path | `C:\Users\<you>\Desktop\OmniWin` |
| Branch | `main` |
| Last commit | `3da6c1a` M0: solution skeleton, Core contracts, stdio server with omniwin_status |
| SDK | .NET 10.0.403, pinned in `global.json`. New shells may not have it on PATH; it's in `C:\Program Files\dotnet`. |
| Build | `dotnet build OmniWin.sln -warnaserror` → 0 warnings, 0 errors (checked 2026-11-06, including the uncommitted files) |
| Test | `dotnet test --solution OmniWin.sln` → 30/30 pass (Microsoft.Testing.Platform, xunit.v3) |

Uncommitted changes in the working tree:

```
 M Directory.Packages.props                     added Logging.Abstractions 10.0.14, Interop.UIAutomationClient 10.19041.0
 M src/OmniWin.Core/SafetyContracts.cs          GateResult gained an optional Detail
 M src/OmniWin.Desktop/OmniWin.Desktop.csproj   package references only; no code
 M src/OmniWin.Safety/OmniWin.Safety.csproj     CsWin32, Logging.Abstractions
 M src/OmniWin.Safety/OmniWinConfig.cs          confirm_mode "off" default; blocked_words_extra
 M tests/OmniWin.Safety.Tests/ConfigStoreTests.cs
?? src/OmniWin.Safety/AuditLog.cs
?? src/OmniWin.Safety/DeletePolicy.cs
?? src/OmniWin.Safety/KillSwitch.cs
?? src/OmniWin.Safety/NativeMethods.txt
?? src/OmniWin.Safety/SafetyGate.cs
```

---

## 3. Done and verified (M0, committed)

- Every project from contract §4 exists and builds (17 in total).
- `OmniWin.Core` holds all §6–§7 contract types and interfaces, plus ResultFormatter, ToolDocs, CoreJson, ReasonCodes and RollingMetrics. This is contract **2.1**; the changes are listed at the top of the contract.
- `OmniWin.Safety` has the data paths with the `--data-dir` override and a strict config loader.
- `OmniWin.Server` is a stdio MCP server whose one tool is `omniwin_status`.
  - The manifest declares PerMonitorV2 and asInvoker; the running server confirmed `dpi=per_monitor_v2`.
  - Startup refusals: exit 2 for a bad argument, 3 for a config problem, 4 when elevated.
  - Measured: the status handler takes about 0.035 ms (p50); a full stdio round trip about 0.42 ms (p50); warm startup about 305 ms.
- Details are in `OmniWin\docs\status\WS0.md`.

---

## 4. Written but not committed, not run, not tested

These are in `src/OmniWin.Safety/`, compile cleanly, and **are not registered in `Program.cs` yet**. Nothing calls them.

| File | What it is |
|---|---|
| `DeletePolicy.cs` | Decides what counts as deleting (§5). Static, pure, easy to unit test. |
| `SafetyGate.cs` | `ISafetyGate`: allows everything except what DeletePolicy forbids; refuses everything while the stop key is active. No dialog. |
| `KillSwitch.cs` | `IKillSwitch`: global hotkey from config (default Ctrl+Alt+Shift+K) on its own message-loop thread. Press once to stop, press again to resume. Never run yet. |
| `AuditLog.cs` | `IAuditLog`: one JSON line per tool call in `<data dir>\logs\audit-YYYYMMDD.jsonl`, written in the background. |

Missing for these: unit tests (DeletePolicy above all), DI registration in `Program.cs`, and a live check that the hotkey registers and toggles.

---

## 5. Owner's decisions (these override contract §13 and §14)

Decided by the owner on 2026-11-06:

- **No confirmation prompts.** `confirm_mode` defaults to `"off"`. `"risky"` is refused at startup because the dialog doesn't exist.
- **Only deleting is blocked.** Sending, editing, typing, launching, closing windows and everything else are allowed. "Deleting" means:
  - buttons and menu items whose name matches delete words: delete, remove, erase, discard, uninstall, wipe, shred, move to trash/bin/recycle bin, empty trash/bin/recycle bin, permanently delete, clear history/browsing data/cache/cookies, clear all, reset this PC, factory reset (plus `blocked_words_extra` in config);
  - element ids containing delete, remove, erase, discard, trash or uninstall;
  - the keys `shift+delete` and `ctrl+shift+delete` anywhere, and `delete` and `ctrl+d` in Explorer;
  - typed delete commands (`del`, `rm`, `rmdir`, `rd`, `erase`, `Remove-Item`, `format`, `diskpart`, `git clean`, …) typed into places that run commands: terminals, the Run box, and Explorer's address bar.
- **No terminal block.** Terminals and every other app may be automated.
- **Kept, because they never get in the way:** the stop key and the audit log.
- **Process blocklist:** only the secure desktop and sign-in prompts (`consent.exe`, `LogonUI.exe`, `CredentialUIBroker.exe`, where Windows refuses injected input anyway) and OmniWin's own process. The owner can add more with `process_blocklist_extra`.
- **Known limit:** this filter works on names, keys and text. It can't recognise an unnamed trash icon, or a script that deletes something as a side effect.

**Contract not yet updated:** §13 still describes Risky confirm dialogs and a terminal blocklist, and §14 still shows `"confirm_mode": "risky"`. Update them to match this section.

---

## 6. Not built yet

| Stream | Missing |
|---|---|
| WS0 | Safety registration and tests (§4); `wait_for`, `run_steps`, workflows; a call-tool filter for the audit log and per-tool metrics; rolling file log |
| WS1 | The desktop engine and its tools: `windows_list`, `desktop_observe`, `desktop_act`, `desktop_keys`, `desktop_scroll` |
| WS2 | The browser engine and its tools |
| WS3 | System tools (`apps_list`, `app_launch`, `window_manage`, audio, media, clipboard, system info) and OCR |
| WS4 | Fixture app and pages, end-to-end suite, benchmarks, Windows-MCP baseline |

The scope for each stream is in the contract and in `agents\WS1`…`WS4`.

---

## 7. Owner's most-used tasks (use as the baseline and acceptance list)

The owner asked for these:

1. Open Notepad, type a line, save it to the Desktop.
2. Launch an installed app by its Start-menu name.
3. Open Chrome.
4. In Chrome, go to youtube.com and search for a video.
5. In Chrome, open Google Maps and get a route between two places.
6. In Explorer, move a file from Downloads to the Desktop.
7. Type into a text field and confirm the text reads back correctly.
8. Click a button or menu item and confirm the expected result.
9. Edit existing text: select a word and replace it.
10. Switch between two open windows.

Plus one refusal check: trying to delete (for example, pressing Delete on a file in Explorer) must come back `denied` with `input_sent=false`.

---

## 8. Suggested order for the next AI

1. Add unit tests for `DeletePolicy`. Then commit the Safety files.
2. Register `SafetyGate`, `KillSwitch` and `AuditLog` in `Program.cs`, and check `omniwin_status` shows them.
3. Update contract §13 and §14 to match §5 above, and bump `ContractVersion`.
4. Build the remaining streams following the contract and the briefs, running the §7 tasks as acceptance checks.
