# WS1 desktop engine status

Updated 2026-11-11. Shared contract 2.4. Everything below "Current state" is history: its test counts are the counts of that day.

## Current state (2026-11-11)

- **Contract 2.5 (2026-11-13, mixed engines):** `desktop_act` gained `hover` and `drag` (a drop onto the Recycle Bin or a delete action is denied), every result line carries `retry=`, and an acting `engine_call` holds the same input lane as a `type` (another act gets `blocked rate_limit`, and a focus attempt's detail names what holds the lane). Full suites passed (1,437, 0 failed); drag and hover have not run live yet. Engine actions are outside the desktop delete rule and the user-activity pause (contract §13.5).
- **Handoff 24 (2026-11-11, focus reporting):** only the changed projects were rerun (Release, with build, `.trx` in `artifacts/test-results/2026-11-11-release/`): Desktop.Tests 384 total, 379 passed, 0 failed, 5 skipped; SystemTools.Tests 173, 170 passed, 0 failed, 3 skipped; SystemTools live 172 passed, 1 skipped. The reviewer added one engine test (stop key pressed → `denied kill_switch`, nothing attempted) and reran only `ForegroundAndPumpTests` and `TypingEngineTests`: 45 of 45 passed. All 9 published files equal their build and the report (Core changed, so every DLL was rebuilt; `OmniWin.Server.exe` unchanged).
- **Handoff 23 (2026-11-11, desktop engine A8–A12):** only the changed projects were rerun, in Release, each with its build, `.trx` files in `artifacts/test-results/2026-11-11-release/`: Desktop.Tests 380 total, 375 passed, 0 failed, 5 skipped; SystemTools.Tests 168, 165 passed, 0 failed, 3 skipped; live: Integration `LiveSessionTests` 3 passed, 2 placeholders skipped (the clipboard restore test passed live for the first time), `DesktopScenarioTests` 27 of 27, `DesktopLiveTests` 1 of 1, SystemTools live 167 passed, 1 skipped. Nine tasks (`tasks-v3`): 160 of 160 on the eight that ran, B07 not run (the owner's Settings window was open). Long typing `typing-v3`/`-v4`/`-v5` (F01 1,000 and 2,000, Notepad 1,000): all `confirmed`, read back exactly. The reviewer checked the counts in every `.trx`, that each final run started after the last source edit, and that all 9 published files equal their build and the report. Debug was built (0 warnings) but not tested this round.
- **Last full suite run (2026-11-11, final code, handoff 22):** Release and Debug each 1,242 total, 1,193 passed, 0 failed, 49 skipped; `msedge.exe` 0 before and after. Live over Remote Desktop on the 2026-11-11 build: Integration 143 (141 passed, 2 placeholders skipped), SystemTools 162 passed, OCR 84 passed, nine tasks 160 of 160 on the eight that apply (B07 not applicable under Remote Desktop), 1,000 and 2,000 characters typed and confirmed. Details in WS4.md.
- **Typing (F16 and the Notepad re-send), final rules:**
  - every control gets one character per batch and each character is acknowledged before the next;
  - on Notepad's editor a character whose caret never moved within 250 ms (and another 80 ms) is sent again **at most once**; if it is still not taken the act ends `error readback_mismatch` with `input_sent=true`;
  - after any re-send, `confirmed` needs the text to stay unchanged for 500 ms (read about every 100 ms, at most 2 s) and match exactly; a different stable text is `contradicted`, no stable or readable text `unverified` (handoff 23, A8);
  - the time limit of one `type` act is the normal 4 s plus 60 ms per character, capped at 120 s, so long text finishes; when it runs out the act ends `error engine_timeout`, `input_sent=true`.
- **A long `type` and other calls (handoff 23, A10):** characters go out one per worker call, so `desktop_observe`, `desktop_wait`, `windows_list` and describe are answered between characters. Another physical act, a second `type` or a `window_manage focus` gets `blocked rate_limit` at once with `input_sent=false` (contract decision: no new code; focus since handoff 24). The time spent sending characters is booked under `input` (`typing-v3`: precheck 11.5 ms, input 7,651.7 ms).
- **Focus fallback, final rules:** plain `SetForegroundWindow`; then the input-queue attach and the F24 tap, both skipped when the window in front does not answer a no-op message within 100 ms; the F24 tap is also skipped when a remote-desktop or virtual-machine client is in front (`mstsc`, `msrdc`, `vmconnect`, `VirtualBoxVM`, `vmware`, `vmware-vmx`). The tap is decided again immediately before the key goes out, against the window in front at that moment (handoff 23, A11). A tapped F24 is reported as channel `key:F24`, also when the attempt stops after the tap. `window_manage focus` reports the engine's real `input_sent`: a window already in front is `confirmed` with `input_sent=false`.
- **`window_manage focus` reports the real reason (handoff 24, fixed):** a focus attempt stopped by a check (a held modifier, a changed window, the secure desktop) or refused before it starts reports the stop itself. If nothing was sent, it gets the stop's own status and reason with `input_sent=false`, for example `blocked user_active` or `denied kill_switch`. If something was sent before the stop, it gets `error input_interrupted` with `input_sent=true` and channel `key:F24` or `api`. A focus refused because a `type` is running gets `blocked rate_limit` with `input_sent=false`. Only a plain failure with no stop is `error foreground_failed`. Tests: `ForegroundAndPumpTests` (engine, including the reviewer's kill-switch test) and `WindowManageTests` (service).
- **Workflows:** a run is `confirmed` only when every step was `confirmed`; otherwise it is `unverified` and names the steps.
- **`window_manage`:** a `wN` id that no longer exists returns `blocked not_found`; it never falls back to a title or process match.
- **Budgets:** the older "strict budgets" are superseded by per-phase click budgets measured with `OmniWin.Bench clicks` (`artifacts/benchmarks/clicks-v1.json`, 2026-11-11): whole server call p95 about 84 ms, pre-checks 13-15 ms, dispatch 19-28 ms, verify 45-48 ms (p95). The budgets set from them are in contract §15 and hold for a click whose effect is seen. Correction (reviewer, 2026-11-11): the "no_change" case of `clicks-v1` clicked Apply right after Agree, so focus moved and the wait ended early; a click with no visible change still waits the 300 ms settle ceiling. `clicks-v2` (reviewer, 10 clicks per case) shows 10 of 10 "No visible change." at a whole server call p50 of 323 ms and p95 of 338 ms. `clicks-v3` (builder, handoff 23, 40 clicks per case): no-change 40 of 40 "No visible change.", whole server call p50 321 ms, p95 339 ms (budget <= 360 ms); expectation met p95 64 ms (budget <= 120 ms). Fixed in handoff 23: the time spent sending the characters of a `type` was booked under `precheck`; it is now under `input`.
- **Saved Windows setting changed by the builder:** `HKCU\Control Panel\Desktop\ForegroundLockTimeout` was the Windows default 200000 and was written as 2147483647 on 2026-11-10 at about 20:27 local time, by mistake (the builder believed it was restoring it). OmniWin's own code never changes it. The owner decides whether to set it back.
- **T3:** not applicable, the owner uses Remote Desktop only.

## Current review follow-up

The final review build passes 470 automated tests in each of Debug/Release, with zero failures and five opt-in skips. Both builds have zero warnings/errors. All ten owner tasks and both deletion refusals passed through one uninterrupted Release server PID; the live fixture and 200-cycle benchmark passed separately. See [final acceptance](WS1_Final_Review_Acceptance_2026-11-07.md) and [review measurements](../benchmarks/WS1_Review_Followup_2026-11-07.md).

F3 no-op frame accounting is fixed. F5 event-unavailable window listing improved p95 from 33.612 to 3.344 ms at the measured cadence. Owner-approved native editor focus checks reduced standard native Edit typing checks p95 from 120.307 to 14.229 ms, while total typing improved from 323.146 to 280.759 ms. Several strict budgets failed at that time (superseded: see Current state); cold observations were about 169–228 ms p95. The physical-console comparison is not applicable: the owner uses Remote Desktop only (T3).

Real Notepad testing exposed packet corruption beyond focus identity. Its verified native path now waits for scalar caret progress, and rejects supplementary Unicode/newline packet text before input. Explicit set_value and separate Enter actions remain available. Final Unicode acceptance used Chrome; supported Notepad ASCII typing/save/edit was verified. This limitation supersedes the earlier broad Unicode claims below.

Final Release Desktop SHA-256: `EB52842A0B9035BFF5FA10F70E7B0800E440C25F28A5D8D350B1AE9D87EC889A` (updated following T1 retry rule addition; historical acceptance hash was `80C9E2C7FEEFCBD8B1CB3E507596CB1DB03A0588B370AF3D4029959322304D80`). R1 was resolved by recording the Task 4 contradicted step truthfully in the acceptance record and adding the retry rule to `ToolDocs.RetryRules`. Budgets, deletion policy, terminal guard, stop/activity checks, and final ownership/hit checks remain unchanged except the owner's explicit D1 native-focus approval. D2: click speed was measured live over Remote Desktop on 2026-11-10 (p50 324 ms, p95 336 ms). Most of that is the fixed 300 ms wait of a click that changes nothing, not the click itself; the per-phase measurement is the `clicks` benchmark (see WS4.md). T3: not applicable, the owner uses Remote Desktop only.

## Earlier acceptance result

All ten owner workflows passed through OmniWin's stdio MCP tools, including real Notepad editing/saving, Chrome/YouTube/Maps, Explorer file movement, and window switching. Fixture and Explorer deletion attempts returned denied with no input. The live acceptance run exposed and fixed shell-window discovery, Save As tree traversal, modern Notepad Unicode corruption, and WinUI frame/focus issues. See [the acceptance record](WS1_Acceptance_2026-11-07.md) for evidence, exact scope, and test files left for inspection.

Final Debug and Release builds passed with warnings as errors, zero warnings, and zero errors. Each normal suite passed 406 tests with zero failures and three opt-in skips. The live fixture regression and repeated-input diagnostics passed separately. Runtime is unblocked in the owner's current Smart App Control-off environment.

Strict performance acceptance was open at that time (superseded by the per-phase click budgets: see Current state). Budgets were unchanged; final ownership/hit tests, per-batch focus checks, stop/activity boundaries, and policy authorization remain enforced. Physical input requires an active, unlocked session. Explicit supported UIA patterns retain covered-window operation; this does not make shared physical input safe during simultaneous user activity.

The final manifest-aware Release run passed 200 measured cycles: p95 click checks 11.902 ms, physical click dispatch 31.457 ms, key checks 8.858 ms, 100-character typing checks 120.307 ms, and typing dispatch 59.960 ms. The raw reports include individual samples and assembly hashes. See [the final validation report](../benchmarks/WS1_Final_Validation_2026-11-07.md) for current results and the bounded pointer/WndProc investigation.

The supported server launch is `OmniWin.Server.exe`, whose manifest reports `dpi=per_monitor_v2`. The ad hoc acceptance client initially used `dotnet OmniWin.Server.dll` and reported DPI-unaware mode. Its completed workflows remain recorded; final apphost window-switching and physical Notepad menu checks also passed. Use the EXE for deployment and future acceptance.

## Historical Release diagnostics follow-up

The full Release profile passed 200 measured cycles including direct 100-character typing. Click checks p95 were 12.802 ms, click dispatch 31.618 ms, key checks 8.878 ms, and 100-character typing dispatch 49.325 ms. Typing checks measured 115.565 ms p95, primarily eight repeated focus queries totaling 105.298 ms p95; capture itself was 0.789 ms. A prior partial run stopped on real user activity and is excluded.

Controlled pointer experiments reproduced slow button delivery when focus changes from the editor to the button: atomic clicks measured approximately 29 ms p95, including with OmniWin's activity hooks and UIA subscriptions absent. Repeated clicks with focus already on the button measured 0.500 ms p95 with both enabled. Mouse moves were under 0.1 ms p95. These results identify a reproducible focus-transition condition, not the exact underlying Windows/provider mechanism. All accepted pointer runs verified every counter increment with no deletion.

Optional per-action timing and opt-in pointer controls were retained. See [the historical Release diagnostics report](../benchmarks/WS1_Release_Diagnostics_2026-11-07.md) for methods and accepted artifacts. At that stage performance acceptance and the ten everyday tasks were still open; the current result above supersedes that functional status.

Final Debug and Release solution builds passed with zero warnings/errors. Both normal automated runs passed 392 tests with zero failures and three opt-in skips (functional fixture, repeated-input benchmark, pointer diagnostics); the benchmark and accepted diagnostics were executed separately and passed.

## Historical repeated input benchmark

The opt-in benchmark now performs 10 warmup cycles and 200 measured cycles of physical clicking, editor focus, Ctrl+A, and Unicode typing. It uses the production stop/activity hooks and SafetyGate, verifies every click counter and typed value, and writes individual action/phase timings to JSON. Two complete runs passed with 200 measured samples per operation per run. The full automated suite reports 392 passed, zero failed, and two opt-in skips (functional fixture and benchmark).

The retained baseline measured p95 checks (resolve plus precheck) at 17.979 ms for clicking, 32.823 ms for typing, and 10.775 ms for keys. Physical click dispatch p95 was 30.785 ms. A smaller ancestor-cache experiment produced no useful latency improvement and was removed. See [the historical benchmark report](../benchmarks/WS1_Input_2026-11-07.md) for p50/p95, raw artifact paths, methodology, and that rejected experiment.

## Earlier review and runtime validation

This follow-up implements the second pasted review.

- Typed CR, LF, and CRLF in a command target use the same readable-command submission check as Enter, including a final check before a newline batch.
- Current-command capture uses the complete editable value or a bounded paragraph around the caret. A recognized prompt anchors text captured from terminal scrollback, including wrapped input before the caret. Unknown prompts, non-caret selections, oversized commands, and ambiguous boundaries block submission.
- The lexical filter recognizes loop and conditional bodies, executable PowerShell blocks, CMD start/call, and Format-Volume, Clear-Disk, and Remove-Partition. New cases preserve safe quoted examples, comments, echo output, and inert data.
- Native Edit selection capture precedes TextPattern. The TextPattern path avoids repeated suffix transfers and redundant full-source copies. Capture and expected output are limited to 262,144 UTF-16 characters.
- After the initial UIA desktop scan, topology refreshes use native HWND inventory. UIA metadata is rebound for new windows and bounded owned-window modality checks.

After the owner turned Smart App Control off, VerifiedAndReputablePolicyState=0 was confirmed on 2026-11-07. The solution build passed with warnings as errors, zero warnings, and zero errors. The full automated run found 393 tests: 392 passed, zero failed, and the opt-in live scenario was skipped. The live scenario then passed separately, covering physical input, insertion readback, deletion denial, modality, occlusion, and covered-window UIA actions. The stdio MCP integration test passed as part of the automated run. No source changes were needed to remove the runtime blocker.

The prior blocked attempt found 105 passes, 287 failures, and one skip. Application Control rejected OmniWin.Core.dll with FileLoadException and HRESULT 0x800711C7. At that time VerifiedAndReputablePolicyState=1 and Code Integrity event 3077 also named OmniWin.Safety.dll in server and test output. Certificate-store inspection found no code-signing certificate in CurrentUser/My or LocalMachine/My. These load failures are resolved in the current owner-selected environment.

Trusted signing remains an option for running with Smart App Control enforced. It requires a certificate issued by a trusted provider and access to its private key, with executable and DLL outputs signed after their final build. [Microsoft SAC signing guidance](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)

Earlier small Debug fixture timings on this PC, superseded by the repeated benchmark above:

| Operation | p50 ms | p95 ms | Samples |
|---|---|---|---|
| Warm observation | 0.117 | 0.220 | 30 |
| Combined action prechecks | 5.192 | 35.608 | 32 |
| Physical click dispatch | 22.225 | 30.269 | 2 |
| windows_list, cold and warm | 1.438 | 73.790 | 2 |
| UIA set_value dispatch | 0.797 | 4.103 | 3 |

Functional checks pass, but full performance acceptance remains open. The warm observation sample meets its target; precheck tails, cold window discovery, and physical click dispatch exceed their budgets. Two click and window-list samples do not establish a stable distribution. Earlier timing tables below remain historical.

Windows Terminal promotes paragraph text units to the document. Command capture therefore bounds text around the caret and requires a recognized prompt before accepting it as a command. Customized terminal prompts can require provider-specific support. [Microsoft Terminal text-range implementation](https://github.com/microsoft/terminal/blob/main/src/types/UiaTextRangeBase.cpp)

## Review fixes

The original integration is committed as 8c8bd34. The follow-up fixes address the pasted engineering review.

- Delete and Ctrl+Delete use shell file-view evidence in the focused control path, including Open/Save dialogs hosted by other applications.
- Command inspection recognizes the reviewed shell wrappers, nested command arguments, find deletion, xargs, and destructive robocopy options. Quoted examples and ordinary echo output stay usable.
- Terminal controls inside editors are identified from control and ancestor metadata. The engine inspects existing command text before submission, combines split typing, and checks character-key sequences. A command target without readable current-line data blocks submission with unsupported_action.
- type captures the original text and selection, then verifies the resulting insertion or replacement. Missing selection information produces unverified. Password contents stay unread.
- Stop and activity checks precede foreground changes. Completed focus operations are included in input_sent accounting, including a later gate denial. Cancellation during verification describes completed input.
- Empty generic panes flatten without advertising a physical click.
- Window discovery has a separate event epoch and a bounded fallback. Process metadata uses retained process handles with liveness checks. Live ancestor properties use cache requests.

Before the current Application Control block, validation was 329 automated tests passed, zero failed, and one opt-in live scenario skipped by the normal run. That scenario passed separately with the added append, prefix replacement, and mid-text insertion checks. The solution build passed with warnings as errors.

The latest small fixture sample measured warm observation p95 at 0.226 ms, action prechecks at 40.851 ms, and physical click dispatch at 27.643 ms across two clicks. Warm windows_list measured 1.278 ms once, with a cold sample of 76.525 ms. These samples do not establish full performance acceptance, which remains open.

The deletion policy remains a heuristic for exposed controls and recognizable commands. Unnamed deletion controls, indirect script effects, and unsupported provider behavior remain limitations. Actual terminal providers and window-switching stress cases need broader acceptance testing.

## Implemented

- Raw UI Automation COM through Interop.UIAutomationClient with explicit CacheRequests and configured connection and transaction timeouts.
- A dedicated MTA command worker and a separate MTA event-registration worker. Event callbacks signal invalidation and waits.
- All eight IDesktopEngine methods and IForegroundService. The server exposes windows_list, desktop_observe, desktop_act, desktop_keys, and desktop_scroll with the contracted parameter names.
- Top-level window discovery starts with UIA desktop children and supplements owned windows through native enumeration. Native owned dialogs are indexed separately from their owner.
- Session-monotonic window and element IDs, per-window generations, eight retained snapshots, diffs, bounded retired-reference metadata, and selector descriptors.
- Observations cap the indexed visible tree at 1000 elements and 4000 visited nodes, with the configured depth limit. The request max_elements controls the rendered projection.
- Native WindowFromPoint and UIA hit checks, actionable-descendant checks, modal and elevation checks, active-desktop checks, and focus-locked Unicode and key batches.
- A single physical-click or UIA pattern dispatch path. Partial native batches release remaining synthetic input. Provider failures after dispatch conservatively report possible input delivery.
- Default readback checks, explicit expectations and compound conditions, event-driven waits with a documented 25 ms provider fallback, and fresh target verification.
- Existing Safety components integrated into DI, including the deletion policy, physical stop hotkey, non-injected user-activity hooks, and redacted per-tool audit logging.

## Background operation

Explicit invoke, set_value, clear, toggle, select, expand, collapse, and scroll_into_view actions address UIA controls directly. They support a covered background window while the user works in another window. The engine continues to validate identity, generation, enabled state, ownership, desktop access, and policy.

Physical click, Unicode typing, keys, wheel scrolling, and focus changes retain the activity guard. An action directed at the actively used foreground window also retains the guard. set_value replaces the control's complete value. The target application's own response can open a dialog or change focus.

The live fixture verifies background set_value and invoke while another window covers the target and the activity service reports recent user input. It verifies the foreground window stays unchanged for those fixture actions, deletion remains denied, and shared input stays paused.

UIA behavior depends on the target provider. The implementation uses the documented control-pattern APIs. Separate interactive sessions have separate Windows input stations. [Invoke API](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationinvokepattern-invoke), [ValuePattern API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.valuepattern.setvalue), [Windows input stations](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations)

## Initial integration verification

| Check | Result |
|---|---|
| Solution build with warnings as errors | Zero warnings and errors |
| dotnet test --solution OmniWin.sln | 226 passed, zero failed, one opt-in live test skipped |
| Opt-in desktop fixture, run separately | Passed |
| Desktop deterministic tests | 94 passed |
| Safety tests, including original config tests | 109 passed |
| Core tests | 22 passed |
| Stdio MCP integration test | One passed |
| MCP tool names and parameter schemas | All five desktop tools verified |
| MCP audit redaction and stdin-close shutdown | Verified by integration test |
| Actual desktop read-only smoke | Window discovery, control observations, and generation diffs verified |

The fixture covers Unicode and whole-value readback, clear, focus, key input, checkbox toggle, list selection, one physical button activation, deletion denial, disabled controls, modal discovery and blocking, owned-dialog closure, occlusion, wheel scrolling, stale refs, and background UIA behavior.

Early fixture runs exposed asynchronous foreground activation and owned-dialog discovery defects. Both were corrected. Fixture startup now sends an explicit ready signal and presents its own temporary window before input checks. The final extended scenario passed on the idle desktop.

Regular tests keep physical input opt-in. The live scenario holds Local\OmniWin.UiInputTestLock on one STA orchestration thread for its full lifetime and cleans up its owned fixture process.

## Commands

The SDK is installed under C:\Program Files\dotnet. These commands were run from the repository.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build OmniWin.sln -warnaserror
& 'C:\Program Files\dotnet\dotnet.exe' test --solution OmniWin.sln
```

For the isolated live scenario after a regular build, on an unlocked idle desktop

```powershell
$env:OMNIWIN_UI_TESTS = '1'
& '.\tests\OmniWin.Desktop.Tests\bin\Debug\net10.0-windows10.0.19041.0\OmniWin.Desktop.Tests.exe' -class OmniWin.Desktop.Tests.DesktopLiveTests -noColor -noLogo -showLiveOutput
Remove-Item Env:OMNIWIN_UI_TESTS
```

The desktop sandbox denies Microsoft Testing Platform's named-pipe connection and restricts real desktop visibility and hotkey registration. The full dotnet test command and actual desktop checks passed outside that sandbox.

## Measurements

Debug build on this PC, from the final extended live scenario. These are small validation samples. Full action times include app reaction and verification waits.

| Operation | p50 ms | p95 ms | Samples |
|---|---|---|---|
| Warm observation | 0.139 | 3.186 | 30 |
| Mixed cold and cached observations | 0.164 | 59.605 | 61 |
| Combined action prechecks | 3.900 | 25.573 | 26 |
| UIA set_value dispatch | 0.743 | 1.398 | 3 |
| Unicode dispatch across typing batches | 2.552 | 5.253 | 2 |
| Native pointer dispatch, one click and one wheel batch | 0.188 | 7.784 | 2 |
| UIA invoke dispatch | 0.680 | 10.227 | 6 |
| windows_list | 21.723 | 88.641 | 2 |

Warm observation meets the 5 ms target in this sample. Precheck and window-list latency exceed the contract targets in this sample. Broader cold-read, click-dispatch, WPF, Chromium, hung-provider, and multi-monitor benchmark runs are still required for the full performance acceptance gate.

## Integration decisions and remaining work

- Work continued in the existing checkout to preserve the pending Safety work. Safety wiring, its regression tests, the minimal fixture, and the MCP integration test were included as WS1 dependencies.
- Structured desktop nodes and diffs stay private to the engine because Core exposes Observation.Text. Escaping and action-result rendering reuse Core.ResultFormatter.
- Wheel scrolling reports channel api. Explicit UIA actions report their existing channels. The request's last retained snapshot is the base for keys and scrolling.
- Typed-command inspection passes a boolean to the gate and keeps ValuePreview redacted. Audit logs redact typed values, key sequences, and expectation contents.
- The owner-selected deletion policy is heuristic. Unnamed deletion icons and indirect script effects remain outside its recognition.
- Native stop-hotkey registration and activity-hook registration were verified through status. Physical stop/resume and hung-provider stress tests remain separate acceptance work.
- OCR refs are supported through IOcrRefResolver injection. The OCR provider belongs to WS3.
- Browser/CDP, system tools/OCR, run logs/workflows, and the full benchmark suite belong to the remaining workstreams.

## Parallel lanes, 2026-11-08 (owner request: handle several tasks at once)
- **Change:** `ActLanes` (`src/OmniWin.Desktop/ActLanes.cs`) replaces the single `_input` semaphore. Real mouse/keyboard acts stay strictly one at a time (physical lane). UI Automation pattern acts (`invoke`, `set_value`, `clear`, `toggle`, `select`, `expand`, `collapse`, `scroll_into_view`) take only their window's lock, so pattern acts on different windows, and a physical act on another window, now overlap. Two acts on the same window never overlap. Lock order is physical lane then window lock; waits take the stop-linked token. `UiaDesktop.WindowIdOfRef` finds a ref's window. Edits in `DesktopEngine.cs` (field, `BringToForegroundAsync`, `DisposeAsync`, the start of `ExecuteAsync`, the `finally`) and `DesktopVerification.cs`.
- **Limit kept:** the UI Automation calls themselves still run one after another on the single worker thread, so only the gate, verification, typing-acknowledgement and idle waits overlap; a hung app can still hold the worker for the UI Automation transaction timeout. A worker pool was not built (the index, refs and generations are single-thread state). Details and the reason: `docs/contract-proposals/WS1-parallel-lanes.md`.
- **Tests (Debug, fakes only):** `ActLanesTests` (10): same-window serialization, different windows overlap, physical exclusivity, pattern acts never wait for the physical lane, physical/pattern same-window exclusion in both orders, stop token cancels every lane and leaks nothing, cancelled window wait releases the physical lane, lease semantics, 100 parallel mixed acts with no deadlock, no ref reuse. Whole Desktop project: 268 total, 263 passed, 5 skipped, 0 failed. Safety: 232 of 232. No existing Desktop test was changed.
- **Not verified live:** no real UI Automation, window or input was used in this change. The overlap with real apps needs a live run.
- **Window-message lane:** designed only (risks and required checks in the proposal).

## Handoff 19 desktop items D1–D4, 2026-11-09

Desktop tests after these changes: 293 total, 288 passed, 0 failed, 5 skipped (the five live/diagnostic tests that need `OMNIWIN_UI_TESTS`, `OMNIWIN_UI_BENCHMARK` and similar variables on an interactive desktop). `msedge.exe` count 0 before and 0 after. Nothing here needed a desktop window, real input or a browser.

### D1: no false `confirmed` on file dialogs and address bars (done)
- **Cause:** `set_value` on the Save dialog's File name box and on Explorer's address bar reads back correctly through UI Automation, but the app ignores the value when it acts, so the readback proved nothing.
- **Fix (first version, by title and name; superseded by the structural rule in the update below, which works in any language):** `src/OmniWin.Desktop/ValueSettingPolicy.cs` recognises those targets (an edit or combobox named "File name…" in a Save as / Save / Open / Select… / Browse… / Choose… / Export / Import / Upload window; Explorer's address band or its edit, `AutomationId 41477`). `DesktopEngine` (before the confirmed return, about line 438) turns a matching readback of `set_value` or `clear` into `status=unverified` with the detail "…Use type here (select all first), then press Enter, and check the result." Password fields are never matched. `type`, `click` and every other target are untouched.
- **Tool text:** the `desktop_act` description in `src/OmniWin.Server/Tools/DesktopTools.cs` now says these cases are never confirmed and tells the model to use `type`.
- **Tests:** `ValueSettingPolicyTests` (recognised targets, ordinary targets not flagged, null and password not flagged, per-action rule, detail text), no real window.
- **Update 2026-11-10 (closed):** the policy also matches the File name box by AutomationId 1148 / `FileNameControlHost` and the address bar by id 41477 or an ancestor of class `Address Band Root` / `Breadcrumb Parent`. `set_value` is never reported confirmed there (reported `unverified`); typing names and paths works. Confirmed live on Windows build 10.0.26300.

### D2: task 10 `foreground_failed` (environment, not a regression)
- **What was compared:** the lane gate, F1, the UI Automation worker shutdown and the CJK change. None of them touches `NativeDesktop.TryForeground` or `UiaDesktop.BringToForeground`; the physical lane only serialises calls and is off by default.
- **Evidence:** on this PC `SPI_GETFOREGROUNDLOCKTIMEOUT` returns 2147483647 ms (0x7FFFFFFF), so Windows refuses a foreground change that does not come from the process holding input. Disconnected or locked RDP sessions and an elevated Task Manager (UIPI) also refuse it. Task 10 passed on Nov 7 in a session where that lock was not in force.
- **Result:** no code change was justified; the foreground check was not weakened. The click budget was later measured (D2, see WS4.md).
- **Update 2026-11-10 (fixed in code):** `NativeDesktop.TryForeground` now tries in order: plain `SetForegroundWindow`; attach to the foreground thread's input queue and retry; a synthetic tap of the unused key F24 (reported `input_sent=true`) and retry; then the child-focus fallback. This follows the documented rule that a process that received the last input event may take the foreground. Task 10 passed 9/9 live with the live foreground lock timeout at 2147483647. OmniWin changes no system setting; the saved registry value was changed by the builder, by mistake (see Current state). Sources: Microsoft Learn `SetForegroundWindow`; Raymond Chen, "I warned you: The dangers of attaching input queues".

### D3: CJK typing change reverted
- Removed the `UnicodeBatchLimit(className, text)` overload and its two tests; `DesktopActions` is back to `UnicodeBatchLimit(className)`. At that time it returned 1 for `RichEditD2DPT` and 16 otherwise; since the F16 fix it returns 1 for every control.
- **F16 is FIXED (2026-11-10)** (this section's "open bug" text is superseded). Symptom: the first characters typed right after an earlier action on the same box came out wrong ("ab" at the start of "XYZ" gave "bbXYZ"). Fix: one scalar per batch plus a per-scalar acknowledgement (caret move, or settle time for the first scalar): `AwaitTypedScalar` in `src/OmniWin.Desktop/NativeDesktop.cs` and the typing loop in `DesktopActions.cs`; `DesktopText.UnicodeBatchLimit` returns 1. Proof: unit theory `Every_control_gets_one_scalar_per_batch_so_a_busy_target_cannot_merge_characters`; live test `F16_first_typing_after_an_earlier_action_inserts_every_character_in_order_at_the_start` (27/27 live `DesktopScenarioTests` in Release; 30/30 server-driven runs). The exact Windows-internal mechanism was not proven.

### D4: one slow window must not block every read (done)
- **Cause:** a window whose provider answered very slowly (Explorer's taskbar can take about 18 s) was read on every refresh, so the call hit its time budget and the whole call failed with `engine_timeout`.
- **Fix:** `src/OmniWin.Desktop/SlowWindowTracker.cs` plus `UiaDesktop.ReadWindow`. Each window's metadata read is timed. A read longer than 1.5 s, or one that UI Automation abandons (`UIA_E_TIMEOUT`, `RPC_E_TIMEOUT`), marks that window slow for 30 s. While marked, the window is listed with `inaccessible=slow` and no UI Automation call is made for it; other windows are read normally. After the cool-down it is probed once more, so a recovered window returns by itself. A new process behind the same handle is never treated as slow, and closed windows are forgotten. `slow` is treated like the other inaccessible states: `desktop_act` returns `blocked reason=engine_timeout` with a clear detail and `input_sent=false`, and `desktop_observe` shows an empty window marked `inaccessible=slow`.
- **Limit:** the one UI Automation call that lists every top-level window is not per-window and is not covered; it still uses the global UI Automation time limits.
- **Tests:** `SlowWindowTrackerTests` (fake clock and fake read times: slow window skipped while others stay readable, cool-down expiry, same handle with a new process, forgotten closed windows, timeout HRESULT classification).
- **Live check later:** with Explorer's slow taskbar present, `windows_list` and `desktop_observe` of another window should return normally and list the taskbar window as `inaccessible=slow`.
