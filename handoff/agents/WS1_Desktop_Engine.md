# WS1: Desktop Engine, Workstream Brief

Planning brief for the WS1 builder agent. The sources of truth are `02_Architecture_and_Contracts.md` (the contract, v2) and `03_Build_Orchestration.md`. If this brief disagrees with the contract, the contract wins. Below, "§N" means a contract section and "03 §N" means a section of the orchestration file.

Branch: `ws1-desktop`, in its own git worktree (03 §3). At each milestone WS0 merges WS1 after WS3 and before WS2.

---

## 1. Mission and scope

**Mission:** build the desktop engine. It reads top-level windows through UI Automation into compact Observations, acts on their elements with checks before and after every input, and exposes all of this through the five WS1 tools. Follow the §1 priorities in order: never act on the wrong target, never hide uncertainty, then be fast.

**In scope**
- `IDesktopEngine`, all eight methods (§7).
- `IForegroundService`, used by WS1's own input path and by WS3 (§4, §7).
- The UIA layer choice: FlaUI.UIA3 or raw `UIAutomationClient` COM interop (§3). Record the choice and the reason in `docs/status/WS1.md`.
- The `uia-worker` MTA thread, its command `Channel<T>`, and the separate MTA thread for event handlers, with the handler rules in §5.
- `eN` and `wN` ref allocation, per-window generations, the live cache, event-driven invalidation, and diffs (§9.1, §9.2, §10.1).
- Pre-checks and the act order `resolve → precheck → gate → input → verify` (§7), with the exact reason codes of §10.2.
- Desktop input: physical click, UIA patterns, Unicode typing, keys, wheel scroll. All Win32 calls go through CsWin32 (§3, §16).
- Default expectations (§10.4), explicit expectations, and event-driven waits (§11).
- `ElementDescriptor` (with `Path`) and `ActionDescriptor` (with a redacted `ValuePreview`) for Safety and the run log (§6, §7, §12).
- Honoring the kill switch and user activity between input chunks (§13.3). Refusing elevated and secure-desktop targets (§13.4).
- Clicking OCR word refs `oN` through `IOcrRefResolver` (§7).
- Per-phase timing in `Timing` and `IMetrics` (§6, §15).
- `Tools/DesktopTools.cs` (§8).

**Out of scope** (owner in brackets)
- Risk classification, the confirm dialog, the kill-switch hotkey, input hooks, the audit log, config loading [WS0, `OmniWin.Safety`]. WS1 calls these; it does not build them.
- Rendering text for observations, diffs and results [WS0, `ResultFormatter`]. WS1 builds structured data only (§9).
- The `wait_for` tool and its routing [WS0]. WS1 implements only `WaitForAsync`.
- Run log, `workflow_save`, `workflow_run`, `run_steps` [WS0]. WS1 supplies descriptors and `ResolveSelectorAsync` (§12).
- Window management (minimize, move, snap, close) and app launch [WS3].
- OCR capture and recognition, `screen_ocr` [WS3].
- DOM access and CDP [WS2]. WS1 still observes Chromium/Electron windows through UIA (the report-only row in §15).
- Fixture app, end-to-end stdio suite, benchmark harness [WS4].
- Everything §8 lists as deliberately absent: scripts, shells, screenshots to the model.

---

## 2. Files owned and files not to touch

**Owned by WS1** (§4, 03 §1):

| Path | Contents |
|---|---|
| `src/OmniWin.Desktop/` | UIA engine, input, foreground service |
| `src/OmniWin.Server/Tools/DesktopTools.cs` | The five WS1 tools |
| `tests/OmniWin.Desktop.Tests/` | Unit, engine and UI-input tests |
| `docs/status/WS1.md` | Status report (03 §5) |
| `docs/contract-proposals/WS1-*.md` | Contract proposals (§17) |

WS0 creates the empty projects at M0 (§4). WS1 can start coding in its own folders before that (03 §2).

**Do not touch.** Write a proposal or ask the owner instead.
- `OmniWin.sln`, `Directory.Build.props`, `.editorconfig`, `global.json` [WS0].
- `src/OmniWin.Core/`, including `ResultFormatter` and `ToolDocs` [WS0]. If a Core type is missing or wrong, propose the change and use an adapter inside `OmniWin.Desktop` until it is accepted (§17).
- `src/OmniWin.Server/Program.cs`, `app.manifest`, `Hosting/`, and every other file in `Tools/` [WS0, WS2, WS3].
- `src/OmniWin.Safety/`, `src/OmniWin.Workflows/` [WS0].
- `src/OmniWin.Browser/` [WS2]; `src/OmniWin.SystemTools/`, `src/OmniWin.Ocr/` [WS3].
- `testapps/`, `tests/OmniWin.Integration/`, `bench/` [WS4]. Ask WS4 for fixture changes and list the request under "Blocked" in `docs/status/WS1.md`.
- All other `tests/` folders, top-level `docs/`, other streams' status and proposal files, and the handoff documents.

---

## 3. Interfaces provided and consumed (§7)

### Provided

| Interface / method | Consumer | Planning notes |
|---|---|---|
| `IDesktopEngine.ListWindowsAsync` | `windows_list` | Fill `WindowInfo.Inaccessible` (§6), so elevated and secure-desktop windows are listed but marked. |
| `ObserveAsync` | `desktop_observe` | Three scopes; live cache vs cold read; diff from `SinceGeneration`; full result with `IsDiff=false` when the base was evicted (§9.2). Respect the `MaxElements` hard cap and `observe.max_depth` (§14). |
| `ActAsync` | `desktop_act`; WS0 `run_steps`, `workflow_run` | Every action in `DesktopActRequest`. `oN` refs allow click-type actions only. Stale validation per §10.1. |
| `SendKeysAsync` | `desktop_keys` | Keys grammar in §7. Foreground and focus checks before the first key. |
| `ScrollAsync` | `desktop_scroll` | Wheel notches over a ref or window. Refs that only moved by scrolling stay valid (§10.1). |
| `WaitForAsync` | WS0 `wait_for` | Every §6 expectation kind except `url_contains` (browser only). Event-driven; 25 ms polling only as a documented fallback (§11). |
| `DescribeAsync` | WS0 Safety, Workflows | `ElementDescriptor` with `Path` from the top-level window (§6). |
| `ResolveSelectorAsync` | WS0 `workflow_run` (M4) | Returns a fresh `eN` from process, title pattern and `Path` (§12). |
| `IForegroundService.BringToForegroundAsync` | WS3 `window_manage focus`; WS1 input path | Returns true only when the change is verified. WS1's own failures map to `foreground_failed`. Deliver at M1 so WS3 is not blocked. |

### Consumed

| From | What | Used for |
|---|---|---|
| WS0 Core (§6) | `Timing`, `Observation`, `ActResult`, `WaitResult`, expectations, `ElementDescriptor`, `SelectorSegment`, `WindowInfo` | Every engine result |
| WS0 Core (§8, §9) | `ResultFormatter`, `ToolDocs` | Output text and tool descriptions |
| WS0 Safety (§7) | `ISafetyGate.AuthorizeAsync` | Called after precheck, before any input. Not allowed → `denied`, `input_sent=false`. Fill `Process` and `WindowTitle` accurately: the Forbidden and typing rules in §13.1 depend on them. |
| WS0 Safety | `IKillSwitch` | Link every handler's token to it; check before each input chunk (§5, §13.3). |
| WS0 Safety | `IUserActivity` | Stop before the next input chunk when the user is active → `user_active` (§13.3). |
| WS0 Safety | `IMetrics` | Per-phase times (§15). |
| WS0 config (§14) | `timeouts.*`, `observe.*` | `IUIAutomation2` transaction and connection timeouts, default expect timeout, default max elements, max depth. |
| WS3 Ocr (§7) | `IOcrRefResolver.TryResolve` | Map `oN` to a box and hwnd. Captures older than 10 s → `stale`. Hit-test the point against the capture window and other top-level windows. Without an explicit expectation the best result is `unverified`. |

Until WS0 Safety and WS3 Ocr merge, use test doubles that implement these interfaces exactly. The act path must still call the gate.

---

## 4. Tools registered (§8 rows owned by WS1)

All five live in `src/OmniWin.Server/Tools/DesktopTools.cs`.

| Tool | Engine call | Default risk | Notes |
|---|---|---|---|
| `windows_list` | `ListWindowsAsync` | Read | Rendered per §9.4. |
| `desktop_observe` | `ObserveAsync` | Read | `since_gen` → `SinceGeneration`; `max_elements` defaults from config. |
| `desktop_act` | `ActAsync` | Normal/Risky | `gen` → `Generation`; `expect` uses the JSON form in §11. |
| `desktop_keys` | `SendKeysAsync` | Normal/Risky | The gate decides which keys are Risky (§13.1); WS1 passes `Keys` in the `ActionDescriptor`. |
| `desktop_scroll` | `ScrollAsync` | Normal | `amount` is wheel notches (§7). |

Rules for every handler:
- Tool and parameter names exactly as in §8; set explicit names in the SDK attributes.
- Descriptions use the `ToolDocs` constants that carry the §10.3 retry rules.
- Keep handlers thin: parse, call the engine, format. They count against the 1 ms overhead budget (§15).
- Async, with a `CancellationToken` linked to the kill switch and a bounded timeout that returns `error engine_timeout` with an accurate `input_sent` (§5).
- No exception leaves a handler (§16). Nothing is written to stdout (§3).

`wait_for` (WS0), `screen_ocr` and `window_manage` (WS3) are not WS1 tools, even though they call WS1 code.

---

## 5. Milestone deliverables (03 §2)

| Milestone | WS1 delivers |
|---|---|
| **M0** (WS0 skeleton) | Coding starts in WS1 folders against the contract. UIA layer chosen and recorded. `uia-worker` thread and command channel read a WinForms and a WPF window with a CacheRequest. Section 9 proposals filed. `docs/status/WS1.md` created. Builds against WS0's empty projects once they land. |
| **M1** (engines standalone) | Every `IDesktopEngine` method works against WS4's fixture app; `ResolveSelectorAsync` may be partial. All actions with pre-checks and default expectations. Diffs and event invalidation. `IForegroundService` ready for WS3. Gate, kill switch, user activity and metrics called through their interfaces (test doubles allowed). Unit and engine tests green. First p50/p95 recorded. |
| **M2** (integrated through MCP) | `DesktopTools.cs` registers all five tools. Real Safety and `IMetrics` wired in; WS3's `IOcrRefResolver` consumed. WS4's end-to-end suite drives the WS1 tools over stdio. First full performance table in the status file. |
| **M3** (hardening) | Every adversarial desktop case returns the expected status and `input_sent`. Owned budgets met, or exceptions approved by WS0. Chromium/Electron cold read reported. A hung app gives `engine_timeout`; a window that closes mid-act gives an accurate status and `input_sent`. `ResolveSelectorAsync` complete. |
| **M4** (workflows, WS0) | Support WS0: selectors resolve reliably on fixture workflows, including after the app restarts, and descriptor `Path` values are stable. Fix defects found through `run_steps` and `workflow_run`. |
| **M5** (release) | Zero warnings, all tests green, final p50/p95 in the status file, no open WS1 proposals, final tool descriptions. Support WS0 and WS4 with packaging. |

---

## 6. Test plan

Three layers, all in `tests/OmniWin.Desktop.Tests/`:

1. **Unit tests** (no UIA, no input). Ref numbers are monotonic and never reused. Generations bump on change. Diff building. Truncation and `omitted:` counts. Keys grammar parsing. The 4 px and scroll-move rules in §10.1. Default-expectation checks. `all_of`/`any_of` reporting. Password redaction in descriptors. `InputSent` on every failure path. OCR capture age over 10 s → `stale`.
2. **Read-only engine tests** against the fixture app: list windows, observe, describe, and waits triggered by the fixture changing itself. These send no real input, so they don't need the test lock, but they do need an interactive desktop.
3. **UI-input tests**: anything that clicks, types, sends keys, scrolls or changes the foreground.

**Test lock (03 §4).** Every UI-input test holds the named mutex `Local\OmniWin.UiInputTestLock` for its whole run. Put these tests in one non-parallel group that takes the lock at the start and releases it at the end. If the lock is not acquired within a bounded wait, fail with a clear message. Never run without the lock. Tag the tests so they can be excluded where no interactive desktop exists. Run them locally, and don't use the PC while they run.

**Ask WS4 for** fixture windows that cover the scenarios below, and for a way to read which inputs each fixture control actually received. That way "nothing was sent" is checked, not assumed.

| Fixture scenario (plain terms) | What to check | Expected |
|---|---|---|
| Form with every common control, WinForms and WPF | Roles, names, states, actions; untrusted-content header; cold-read time | Matches §9.1; p95 ≤ 100 ms at ≤ 500 elements |
| Same form, observed twice | Second read from cache; `since_gen` diff | p95 ≤ 5 ms; diff per §9.2 |
| Password box | Observe output and `ActionDescriptor` | `value=<redacted>`; no password anywhere |
| Text box; a field that alters what is typed | `type`, `set_value`, `clear` | `confirmed`; the altering field → `contradicted readback_mismatch` |
| Checkbox, combo box, list, tree | `toggle`, `select`, `expand`, `collapse`, `focus` | `confirmed` on match, `contradicted` on mismatch (§10.4) |
| Button that opens a dialog | Click with and without `window_opened` | With: `confirmed`. Without: `unverified` plus diff |
| Button that does nothing | Click | `unverified`, "no visible change" |
| Modal dialog over its owner | Act on the owner; `scope=dialog` | `blocked modal_blocking`; modal markers in observe (§9.1) |
| Target covered by another top-level window | Click | `blocked occluded_by_window`; fixture received nothing |
| Target covered by a panel in its own window | Click | `blocked occluded_same_window` |
| Disabled control; control scrolled out of view | Act | `blocked disabled`; `blocked offscreen` |
| Element removed or renamed after observe | Act with the old `gen` | `stale`, `input_sent=false`, fresh diff |
| Window gen advanced, element unchanged or only scrolled | Act | Proceeds |
| Window closed after observe | Act on its refs | `stale` |
| Another window takes focus between precheck and input | Type | `focus_changed` (stolen) or `focus_failed` (could not set); accurate `input_sent` |
| Foreground change refused | Keys; `BringToForegroundAsync` | `foreground_failed`; method returns false |
| Fixture UI thread hangs | Any act | `error engine_timeout`, accurate `input_sent` |
| Gate double denies | Act | `denied` with the gate's reason, nothing received |
| Kill-switch double trips, or user-activity double goes active, during a long `type` | Typing stops before the next chunk | No further input, no key left held, accurate `input_sent` (status per proposal 3) |
| Key sequence such as `ctrl+a delete` into a named window | `desktop_keys` | Received in order; `Keys` reached the gate |
| Long list | Scroll by notches | Content moves; earlier refs still valid |
| Element appears, disappears, or changes value after a delay | `WaitForAsync`, each desktop kind | `Met` via events; not met at timeout |
| Very large tree (over 1000 nodes) | `max_elements` | `truncated:` footer; hard cap 1000 |
| OCR double with fresh, old, and covered words | `desktop_act` on `oN` | Click → `unverified`; old → `stale`; covered → `blocked occluded_by_window`; `type` → `unsupported_action` |
| Edge window on the reference PC | Cold observe | Time reported (target ≤ 400 ms) |
| Elevated window (manual setup) | List and act | `inaccessible=elevated`; `blocked elevated_target` |

Every act test asserts `input_sent` and checks it against what the fixture received. No test may accept `confirmed` unless the expectation was actually observed (§15 accuracy gates).

---

## 7. Performance budgets owned (§15)

Measure in-process with `Stopwatch` around each phase, using the §6 phase keys, and record through `IMetrics`. Exclude gate/confirm time and app reaction time; report them separately (§7, §15). Measure a Release build on the reference PC (§2).

| §15 row | p95 budget | Phases measured |
|---|---|---|
| `desktop_observe` from live cache, ≤ 150 elements, incl. formatting | ≤ 5 ms | read + format |
| `desktop_observe` cold read, ≤ 500 elements, Win32/WinForms/WPF | ≤ 100 ms | read + format |
| `desktop_observe` cold read, Chromium/Electron | report only (target ≤ 400 ms) | read + format |
| Act pre-checks (resolve + hit-test + modal/focus) | ≤ 10 ms | resolve + precheck |
| Input dispatch: click | ≤ 1 ms | input |
| Input dispatch: type 100 characters | ≤ 20 ms | input |
| Verify after the awaited event arrives | ≤ 5 ms | verify, from event arrival |
| `windows_list` | ≤ 5 ms | whole engine call |

WS1 also contributes to two rows it does not own: tool-call overhead ≤ 1 ms (keep handlers thin) and `workflow_run` per step ≤ 20 ms (the cost of `ResolveSelectorAsync`).

**Reporting in `docs/status/WS1.md`** (03 §5, §16). Keep these sections current: Done, In progress, Blocked (including requests to WS0 and WS4), Open proposals, and Decisions (the UIA layer choice). Put performance in one table, one row per §15 row above:

| Operation | Budget | p50 | p95 | Samples | Target app | Date | Commit | Result |
|---|---|---|---|---|---|---|---|---|

`Result` is `met`, `missed`, or `exception approved by WS0` with a link to the decision. State the sample count, and use enough samples after warm-up for a stable p95 (hundreds, not tens). Note anything that skews numbers, such as an attached debugger. Update the table at every milestone before WS0 merges. WS4's end-to-end numbers are the cross-check; if they disagree with WS1's in-process numbers, say so.

---

## 8. Definition of done (§16)

WS1 is done for a milestone when all of these hold:

- [ ] `dotnet build -warnaserror` passes with zero warnings, and `dotnet test` passes on `ws1-desktop` (03 §3).
- [ ] Unit tests pass.
- [ ] WS1's engine and UI-input tests pass against the fixture app, with input tests run under the test lock.
- [ ] p50/p95 measured and recorded in `docs/status/WS1.md` for every row in section 7.
- [ ] No file touched outside the section 2 list.
- [ ] Every contract deviation is recorded as an approved proposal.

Coding standards from §16 and §5, checked in review:

- [ ] Async all the way; every public async method takes a `CancellationToken`; no `.Result`, `.Wait()` or `Thread.Sleep`; `Task.Delay` only in bounded polling fallbacks, with a comment.
- [ ] No exception escapes a tool handler; `InputSent` is accurate on every path.
- [ ] Win32 only through CsWin32; UIA COM objects used only on the apartment that created them.
- [ ] UIA event handlers only enqueue invalidations; no UIA calls and no handler changes inside a handler (§5).
- [ ] No allocation-heavy LINQ in observe formatting or other hot paths.
- [ ] Nothing written to stdout (§3).
- [ ] From M3: zero wrong-target actions and zero false `confirmed` in WS4's suite for desktop cases (§15).

---

## 9. Open questions to raise as contract proposals (§17)

File each as `docs/contract-proposals/WS1-<topic>.md` (problem, proposed change, impact on others) during M0 or early M1. Code to the current contract until WS0 accepts (§17, 03 §6).

1. **`WS1-interface-location`**: §6 puts shared types in `OmniWin.Core`, but §7 lists `IDesktopEngine`, `IForegroundService` and the desktop request records under `OmniWin.Desktop`, and `IOcrRefResolver` under `OmniWin.Ocr`. As written, `OmniWin.SystemTools` must reference `OmniWin.Desktop`, and `OmniWin.Desktop` must reference `OmniWin.Ocr`. With the 03 §3 merge order (WS3 before WS1), WS3's call to `IForegroundService` would not build on `main` until WS1 merges. Propose moving the cross-stream interfaces into Core.
2. **`WS1-formatter-input`**: §9 says engines build structured data and `ResultFormatter` renders it, but the contract defines no structured node or diff types and no formatter signature. Propose them in Core (WS0 may add members, §6).
3. **`WS1-partial-input`**: If the kill switch or user activity stops an act mid-typing, some input was already sent, but `denied` and `blocked` both mean `input_sent=false` (§10.2). Propose which status to use, and define an "input chunk" for typing and key sequences (§13.3).
4. **`WS1-key-release`**: §13.3 says tripping the kill switch sends key-ups for held keys and buttons. Only the desktop input layer knows which modifiers it holds. Propose whether Safety or WS1 sends them.
5. **`WS1-keys-focus-target`**: §13.1 makes `delete` in a file list and `enter`/`space` on a lexicon-named element Risky, but §7 says `ActionDescriptor.Target` is null for keys, so the gate cannot see the focused element. Propose setting `Target` to the focused element for `desktop_keys`.
6. **`WS1-channels`**: The `ActResult.Channel` values (§6) cover none of `expand`, `collapse`, `focus`, `scroll_into_view`, `clear`, or wheel scroll. Propose values to add.
7. **`WS1-keys-generation`**: `KeysRequest` and `ScrollRequest` carry no generation, yet `ActResult.After` is a diff since the request's generation (§6). Propose a base, such as the target window's last observed generation.
8. **`WS1-audit-redaction`**: §13.4 requires one audit entry per tool call, with typed text as `<N chars>` and no passwords. The contract doesn't say whether the host or the tool file calls `IAuditLog.Record`, or how the host learns that a field is a password. Propose the split.
9. **`WS1-config-and-metrics`**: WS1 needs `timeouts.*` and `observe.*` (§14), but no config type for engines is named. `IMetrics.Record(op, ms)` has no agreed op names, so `omniwin_status` can't map ops to §15 rows. Propose both in Core.
10. **`WS1-package-refs`**: WS0 creates the projects at M0 (§4). Confirm that WS1 may add package references (UIA layer, CsWin32) to the project files in its own folders, and say where versions are pinned.

---

## 10. Kickoff prompt for the WS1 builder agent

> You are WS1, the desktop engine for OmniWin. Read `README.md`, `02_Architecture_and_Contracts.md`, `03_Build_Orchestration.md`, and `agents/WS1_Desktop_Engine.md`, in that order. The contract wins on any conflict.
>
> Work on branch `ws1-desktop` in your own git worktree. Edit only the paths in section 2 of your brief: `src/OmniWin.Desktop/`, `src/OmniWin.Server/Tools/DesktopTools.cs`, `tests/OmniWin.Desktop.Tests/`, `docs/status/WS1.md`, and `docs/contract-proposals/WS1-*.md`. Never edit the `.sln`, Core, Safety, or another stream's files. If you need a change there, write a proposal.
>
> Start now, before M0 lands. Choose FlaUI.UIA3 or raw `UIAutomationClient` COM interop and record why in your status file. Build the `uia-worker` MTA thread and its command channel. File the proposals in section 9 of your brief. Then deliver M1: the full `IDesktopEngine` and `IForegroundService` working against WS4's fixture app, using the act order resolve → precheck → gate → input → verify, the exact §10.2 reason codes, and an accurate `input_sent` on every path. Use test doubles for Safety and OCR until those merge, but always call the gate.
>
> Never act on the wrong target, never report `confirmed` without observing the expectation, and never write to stdout. Any test that sends real input must hold `Local\OmniWin.UiInputTestLock` for its whole run. Measure p50/p95 for your §15 rows and keep `docs/status/WS1.md` current. Before every merge, `dotnet build -warnaserror` and `dotnet test` must pass.
