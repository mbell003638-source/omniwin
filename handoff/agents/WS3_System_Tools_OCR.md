# WS3: System Tools + OCR (workstream brief)

**Source of truth:** `02_Architecture_and_Contracts.md` ("the contract", cited as §N). `03_Build_Orchestration.md` is cited as "03 §N". If this brief and the contract disagree, the contract wins.
**Branch:** `ws3-system`, in its own worktree (03 §3). **Merge slot:** after WS0 and WS4 fixtures, before WS1 and WS2 (03 §3).

---

## 1. Mission and scope

**Mission.** Give the model typed system tools and a local OCR fallback. They run through direct Windows APIs inside the one long-running server process (§1, §5). WS3 implements `ISystemTools` and `IOcrFallback` (§7) and the WS3 rows of the tool surface (§8). Nothing is spawned per action, and nothing runs a shell or script (§5, §8, 03 §8).

**In scope**
- `OmniWin.SystemTools`: audio, media, windows, apps, system info, clipboard (§4, §7).
- `OmniWin.Ocr`: capture plus `Windows.Media.Ocr`, the `oN` word ids, and `IOcrRefResolver` (§4, §7, §9.1).
- MCP handlers in `Tools/SystemTools.cs` and `Tools/OcrTools.cs`, using `ToolDocs` wording and `ResultFormatter` output (§8, §9).
- Calling the safety gate inside every WS3 act, in the order `resolve → precheck → gate → input → verify`, with an `ActionDescriptor` whose `Target` is null (§7).
- Phase timings and `IMetrics` records for every WS3 op (§6, §15).
- WS3 unit tests and WS3 real-desktop tests (§4, 03 §4).

**`app_launch` limits.** `app` is a Start-menu name or an AUMID and nothing else (§7 `LaunchAppAsync`, §8 "Deliberately absent"). An arbitrary path or executable is Forbidden unless it is listed in `app_launch_allow_paths` (§13.1, §14). An unrecognised string is refused. It is never passed to a shell or to a generic "open" call.

**Out of scope**
- Observing or acting on UI elements, `windows_list`, focus logic, and clicking `oN` refs: WS1. WS3 calls `IForegroundService` and does not re-implement focus (§4).
- The gate, confirm dialog, kill switch, user-activity hooks, audit log, metrics store, config, `ResultFormatter`, `ToolDocs`, host wiring: WS0 (§4, §13).
- Run log and workflows: WS0 (§12).
- Fixture app, end-to-end suite over stdio, benchmark runner, baselines: WS4 (§4).
- Returning screenshots or images to the model. This is deliberately absent (§8).

---

## 2. Files owned

Exact paths from §4. WS0 creates every project at M0 (§4, 03 §2); WS3 fills its own.

| Path | Contents |
|---|---|
| `src/OmniWin.SystemTools/` | `ISystemTools` implementation: audio, media, windows, apps, info, clipboard |
| `src/OmniWin.Ocr/` | capture, `Windows.Media.Ocr`, `IOcrFallback`, `IOcrRefResolver` |
| `src/OmniWin.Server/Tools/SystemTools.cs` | handlers for the eight system tools in section 4 |
| `src/OmniWin.Server/Tools/OcrTools.cs` | handler for `screen_ocr` |
| `tests/OmniWin.SystemTools.Tests/` | unit and real-desktop tests for system tools |
| `tests/OmniWin.Ocr.Tests/` | unit and real-desktop tests for OCR |
| `docs/status/WS3.md` | WS3 status (§16, 03 §5) |
| `docs/contract-proposals/WS3-*.md` | WS3 proposals (§17) |

**Do not touch.** Write a proposal instead (03 §6).
- `OmniWin.sln`, `Directory.Build.props`, `.editorconfig`, `global.json`: WS0.
- `src/OmniWin.Core/`, including the §6 types, the §7 signatures, `ResultFormatter`, `ToolDocs`, `ContractVersion`. WS3 implements the §7 interfaces. It does not change them.
- `src/OmniWin.Server/Program.cs`, `app.manifest`, `Hosting/` (WS0), and every other file in `Tools/` (WS0, WS1, WS2).
- `src/OmniWin.Safety/` and `src/OmniWin.Workflows/` (WS0), `src/OmniWin.Desktop/` including `IForegroundService` (WS1), and `src/OmniWin.Browser/` (WS2).
- Other streams' test projects, `tests/OmniWin.Integration/`, `testapps/`, `bench/`.
- Top-level `docs/`, other streams' status and proposal files, and the handoff files, including this brief.

---

## 3. Interfaces (§7)

### Provided by WS3

| Interface | Implemented in | Consumed by | Obligations |
|---|---|---|---|
| `ISystemTools` | `OmniWin.SystemTools` | `Tools/SystemTools.cs` | Every act returns an `ActResult` with accurate `InputSent`, `Channel = "api"`, and phase timings (§6, §10.2) |
| `IOcrFallback` | `OmniWin.Ocr` | `Tools/OcrTools.cs` | `OcrResult` with word boxes in physical pixels (§5) |
| `IOcrRefResolver` | `OmniWin.Ocr` | WS1, for `desktop_act` on `oN` refs | See below |

`IOcrRefResolver` obligations:
- `oN` ids are monotonic per session and never reused (§9.1).
- Each id stores the word, the hwnd of the captured window, and the capture time (§7).
- `TryResolve` is synchronous and is called from WS1 code, probably on the `uia-worker` thread (§5). It must not block, must not call into UIA, and must be safe while `screen_ocr` calls run at the same time.
- WS1 owns the 10 s staleness rule and the hit-test pre-checks (§7 note). Keep entries a bounded time past 10 s, so WS1 can return `stale` rather than `not_found`.

### Consumed by WS3

| Interface | Owner | Used for |
|---|---|---|
| `IForegroundService.BringToForegroundAsync` (§4, §7) | WS1 | `window_manage focus`. Returns `true` only if focus was verified |
| `IDesktopEngine.ListWindowsAsync` (§7) | WS1 | Mapping a `window` argument to a `WindowInfo` (hwnd, process, `Inaccessible`). Pending Q1 |
| `ISafetyGate` (§7, §13) | WS0 | Classifying and authorizing every WS3 act after precheck and before input |
| `IKillSwitch` (§5, §13.3) | WS0 | Linking its token into every handler |
| `IUserActivity` (§13.3) | WS0 | Scope pending Q10 |
| `IMetrics`, `IAuditLog` (§7) | WS0 | Phase timings and audit entries. Who writes the entry is pending Q10 |
| `ResultFormatter`, `ToolDocs` (§8, §9) | WS0 | All text output and tool descriptions |
| Config (§14): `app_launch_allow_paths`, `timeouts` | WS0 | The launch allow-list and engine timeouts |

WS3 merges before WS1 (03 §3). Until WS1 lands, code against fakes of `IForegroundService` and `IDesktopEngine`. Q9 covers where the interfaces are declared.

---

## 4. Tools registered (WS3 rows of §8)

| Tool | Calls (§7) | Default risk (§8, §13.1) | Notes |
|---|---|---|---|
| `apps_list` | `ListAppsAsync` | Read | Start-menu apps only |
| `app_launch` | `LaunchAppAsync` | Normal | A path or executable not in `app_launch_allow_paths` is Forbidden (`denied policy_forbidden`) |
| `window_manage` | `WindowManageAsync` | Normal; `close` = **Risky** | `focus` goes through `IForegroundService`. Windows of OmniWin, the secure desktop, and blocklisted processes are Forbidden (§13.1). An elevated window returns `blocked elevated_target` (§13.4) |
| `audio` | `GetAudioAsync` / `SetAudioAsync` | Normal | Called with no params, it reads and returns `AudioState` |
| `media` | `MediaAsync` | Normal | Uses WinRT media sessions (§3) |
| `system_info` | `SystemInfoAsync` | Read | `kind` values per §7 |
| `clipboard_get` | `ClipboardGetAsync` | **Risky** | Always confirmed. Deny or timeout gives `denied user_denied` / `confirm_timeout` (see Q5) |
| `clipboard_set` | `ClipboardSetAsync` | Normal | Text is a redacted value preview (§7, §13.4; see Q7) |
| `screen_ocr` | `IOcrFallback.RecognizeAsync` | Read | Text only, with the untrusted header (§13.4). `oN` ids are clickable via `desktop_act`, click actions only (§7 note) |

Rules for every row:
- Names and params are snake_case exactly as in §8.
- The gate may raise the class (§8).
- Descriptions carry the §10.3 retry rules via `ToolDocs` (§8).
- No exception escapes a handler (§16).
- Every handler is async, takes the kill-switch-linked token, and has a timeout. A hung call returns `error engine_timeout` with an accurate `input_sent` (§5).

---

## 5. Milestone deliverables (03 §2)

| Milestone | WS3 delivers |
|---|---|
| **M0** (WS0 skeleton) | Start before M0, inside the WS3 folders (03 §2). Check the MCP SDK samples for the tool-registration API (§3). Confirm the target framework exposes `Windows.Media.Ocr` and media sessions (§3). Write skeleton implementations of the three WS3 interfaces against fakes. Create `docs/status/WS3.md`. File the design-blocking proposals first: Q1, Q2, Q3, Q5, Q9. |
| **M1** Engines standalone | Every `ISystemTools` and `IOcrFallback` method works against real Windows and WS4 fixtures, with unit tests green. The gate is called in §7 order (a fake gate until Safety lands). `IOcrRefResolver` is ready for WS1. First p50/p95 numbers are in the status file. |
| **M2** Integrated through MCP | All nine WS3 tools are registered with `ToolDocs` wording and `ResultFormatter` output. The real gate, kill switch, audit log and metrics are wired. `window_manage focus` uses the real `IForegroundService`. An `oN` click works end to end with WS1. WS4's stdio suite covers the WS3 tools. First performance report. |
| **M3** Hardening | WS3 adversarial cases return the expected status and `input_sent`. Budgets are met, or WS0 has approved exceptions. Accepted proposals are implemented and their adapters removed. |
| **M4** Workflows (WS0) | System-tool acts give the run log what Q8 decides. No new tools, no regressions. |
| **M5** Release | Final p50/p95 in the status file. Tool descriptions final. No open WS3 proposals. The 03 §8 items hold for WS3 tools: no scripts, shells or arbitrary executables, and every call audited. |

---

## 6. Test plan

**A. Unit tests (no desktop changes; run anywhere).** xUnit plus FluentAssertions or Shouldly (§3).
- **Bad arguments.** An unknown `action` or `kind`, a volume outside 0–100, `move` without x/y, or `resize` without width/height returns a non-success status. It has `input_sent=false` and a §10.2 reason code, and no exception escapes.
- **Gate order.** A fake gate receives a correct `ActionDescriptor` after resolve and precheck, before the API call. When it denies, nothing changes and `input_sent=false`.
- **Kill switch and timeouts.** A tripped switch returns `denied kill_switch` with nothing sent. A cancel mid-call reports `input_sent` correctly. A hung fake returns `error engine_timeout` within the configured timeout.
- **`app_launch` input.** Start-menu names and AUMIDs are accepted. A raw path, a bare `.exe` name, a URL, or shell syntax returns `denied policy_forbidden`, unless the exact path is in `app_launch_allow_paths`.
- **OCR ref store.** Ids increase and are never reused across captures. `TryResolve` returns the stored box, hwnd and capture time. Unknown ids return false. Concurrent captures do not corrupt the store.
- **OCR recognition.** Run against checked-in test images: known words and boxes are found, the output starts with the untrusted header, and the output holds no image data.

**B. Real-desktop tests (hold `Local\OmniWin.UiInputTestLock`, 03 §4).** These change shared desktop state, so treat them like input tests: local only, interactive session, nobody using the PC. Save, then restore, volume, mute, clipboard contents and window positions.
- Run every `window_manage` action against a WS4 fixture window and read the result back. Test `focus` with the real WS1 service once WS1 has merged.
- `audio`: set, then read back. Test `media` with and without an available media session.
- `clipboard_set` then `clipboard_get` as a round trip, using a fake gate that allows.
- `app_launch` a Start-menu app, check its window appears, then close it.
- `screen_ocr` a fixture window with known text, at 100 % and at a scaled DPI where available. Boxes must land on the text in physical pixels. A joint test with WS1 clicks an `oN` ref.
- An elevated window (e.g. Task Manager, §9.4) returns `blocked elevated_target`. OmniWin's own windows are Forbidden.

**C. Adversarial cases handed to WS4.** WS4 owns the suite (§4). WS3 lists each case with its expected status and `input_sent` in its status file:
- path launch;
- blocklisted-process launch (Q3);
- `clipboard_get` denied or timed out;
- `window_manage close` denied;
- kill switch tripped mid-call;
- elevated target;
- an `oN` ref older than 10 s;
- an `oN` ref whose point is now covered (this result comes from WS1).

**D. Performance.** See section 7. WS4 owns `bench/`. WS3 measures through `IMetrics`, and asks WS4 for BenchmarkDotNet cases if micro-benchmarks are needed.

---

## 7. Performance budgets owned (§15)

| Operation | p95 budget | Note |
|---|---|---|
| `audio`, `media`, `window_manage` | ≤ 5 ms | Shared §15 row; `windows_list` in the same row is WS1's |
| `screen_ocr`, one window ≤ 1920×1080 | ≤ 300 ms | |
| Tool-call overhead (JSON in → engine → text out) | ≤ 1 ms | Server-wide row; WS3 handlers must not add to it |
| `apps_list`, `app_launch`, `system_info`, `clipboard_get`, `clipboard_set` | none | Report only |

Measurement rules:
- Server-side, using `Stopwatch` through `IMetrics` around each §6 phase (`resolve`, `precheck`, `gate`, `input`, `verify`, `read`, `format`), on the reference PC (§2, §15).
- Exclude gate and confirm time (§7) and app reaction time (§15), such as window animation or app start-up. Report them separately.
- SystemTools start lazily (§5). Report the first call after start separately from warm numbers.
- A phase key not listed in §6 needs a proposal.

**`docs/status/WS3.md` performance table** (kept alongside done / in progress / blocked and open proposals, 03 §5):

| Op | Budget p95 | p50 | p95 | Samples | Conditions |
|---|---|---|---|---|---|
| `audio` read / set | ≤ 5 ms | | | | device, warm/cold |
| `media` (per action) | ≤ 5 ms | | | | session present? |
| `window_manage` (per action) | ≤ 5 ms | | | | target app |
| `screen_ocr` | ≤ 300 ms | | | | window size, DPI, word count |
| report-only ops | — | | | | |

If a budget is missed, record the number and the cause, and ask WS0 for an exception before M3 (03 §2).

---

## 8. Definition of done (§16)

Shared definition, applied to WS3:
- [ ] Builds with zero warnings: `dotnet build -warnaserror` (§16, 03 §3), with `Nullable`, `TreatWarningsAsErrors` and analyzers on.
- [ ] Unit tests pass: `dotnet test` on both WS3 test projects.
- [ ] WS3's integration tests pass against the fixture app.
- [ ] p50/p95 measured and recorded in `docs/status/WS3.md`.
- [ ] No files touched outside section 2 of this brief.
- [ ] Every contract deviation is recorded as an approved proposal.

WS3 checks drawn from §16 and related sections:
- [ ] Async all the way, with a `CancellationToken` on every public async method. No `.Result`, `.Wait()` or `Thread.Sleep`. `Task.Delay` only in bounded polling, with a comment (§16, §11).
- [ ] No exception escapes a handler, and `input_sent` is accurate on every path (§6, §16).
- [ ] Win32 through CsWin32. COM and WinRT objects are used on the apartment that created them (§16).
- [ ] Nothing written to stdout; logs go to stderr and the file (§3).
- [ ] The gate is called after precheck and before input on every act; the Risky and Forbidden rows behave per §13.1.
- [ ] No WS3 tool accepts or runs a shell, script or arbitrary executable (§8, 03 §8).
- [ ] OCR output is text only and carries the untrusted-content header (§8, §13.4).

---

## 9. Open questions (file as `docs/contract-proposals/WS3-<topic>.md`, §17)

Until a proposal is accepted, code to the current contract, with an adapter in WS3 files where needed (§17).

- **Q1 `WS3-window-arg`.** §8 gives `window` for `window_manage` and `screen_ocr`, but not its accepted forms or who maps it to an hwnd. Proposed: accept the same forms as `DesktopObserveRequest.Window` (§7) and resolve them through `IDesktopEngine.ListWindowsAsync`, so `wN` ids stay WS1's (§9.1). §10.2 also has no reason code for "more than one window matches".
- **Q2 `WS3-foreground-result`.** `BringToForegroundAsync` returns only `bool` (§7). On `false`, WS3 cannot set an accurate `input_sent`, and cannot tell `foreground_failed` from `elevated_target`. Proposed: return status, reason and input-sent, or document that `false` means nothing was sent.
- **Q3 `WS3-launch-shells`.** §13.1 forbids acting on windows of `cmd.exe`, `powershell.exe`, `pwsh.exe`, `WindowsTerminal.exe` and the rest of the blocklist. It does not stop `app_launch` from starting those processes through their Start-menu entries, and `args` could carry a command line. 03 §8 says no tool runs shells. Proposed: launching a blocklisted process is Forbidden, and the proposal defines what `args` may contain. WS3 puts the resolved target process in `ActionDescriptor.Process` so the gate can decide.
- **Q4 `WS3-default-checks`.** §10.4 has no rows for system-tool acts. Proposed read-back checks, with §10.2 statuses:
  - volume and mute read back;
  - window rect and state read back;
  - `close`: the window is gone;
  - clipboard text reads back;
  - `app_launch`: `window_opened` for the launched app;
  - `media`: `unverified` unless the session state changes.
- **Q5 `WS3-non-act-status`.** `ClipboardGetAsync`, `SystemInfoAsync`, `GetAudioAsync`, `ListAppsAsync` and `RecognizeAsync` return plain values (§7). They have no way to report `denied` (for example, the user declines `clipboard_get`), `blocked` or `engine_timeout`. Proposed: a result wrapper with status and reason. Until then, an adapter in the WS3 tool files.
- **Q6 `WS3-ocr-scope`.** `screen_ocr` is Read (§13.1), yet OCR of a password manager, the OmniWin confirm dialog or the secure desktop would expose what §13.1 and §13.4 protect. With `region` only, words can span windows, but `TryResolve` returns one hwnd per word. The default when both `window` and `region` are omitted is also unstated. Proposed:
  - deny Forbidden windows (`denied policy_forbidden`) and mask them out of region captures;
  - set each word's hwnd to the top-level window under its box at capture time;
  - default to the foreground window.
- **Q7 `WS3-text-and-redaction`.** §9 has no format for `OcrResult`, `AudioState`, `apps_list`, `system_info` or `clipboard_get`. §13.4 puts the untrusted header only on observations, diffs and OCR. Proposed: one-line formats in `ResultFormatter`, and the header on clipboard text too. Also needs a decision: is `clipboard_set` text logged and previewed like typed text (`<N chars>` unless `log_typed_text`, §13.4)?
- **Q8 `WS3-runlog-system`.** §12 steps carry `ElementDescriptor.Path`, and system-tool acts have no element. Are they recorded as run-log steps and replayed by `workflow_run`? This must be decided before M4.
- **Q9 `WS3-interface-home`.** §4 places `IForegroundService` in `OmniWin.Desktop`. The home of `ISystemTools`, `IOcrFallback` and `IOcrRefResolver` (Core or the owning projects) is not stated. If they live in owner projects, SystemTools must reference Desktop and Desktop must reference Ocr. Because WS3 merges before WS1 (03 §3), `IForegroundService` must be on `main` before WS3's M1 merge. Proposed: declare all §7 interfaces in Core at M0.
- **Q10 `WS3-safety-scope`.** Open points:
  - Do the user-activity cooldown (§13.3) and the rate limit (§13.4) apply to system-tool acts?
  - Which `ActionDescriptor.Engine` value do system tools and OCR use? §6 names only `desktop` and `browser`.
  - Who writes the `AuditEntry`: the host or the tool file?

---

## 10. Kickoff prompt for the WS3 builder agent

> You are WS3 (System tools + OCR) for OmniWin. Read `README.md`, `02_Architecture_and_Contracts.md`, `03_Build_Orchestration.md` and `agents/WS3_System_Tools_OCR.md`, in that order. The contract wins on any conflict.
>
> Work on branch `ws3-system` in your own git worktree. Code only in the paths in section 2 of your brief. Never edit the `.sln`, Core, Safety, Desktop, Server hosting, or any other stream's files. If you need a change there, write `docs/contract-proposals/WS3-<topic>.md`, and code to the current contract until WS0 accepts it.
>
> You can start before M0 inside your own folders. First:
> - check the C# MCP SDK samples for the current tool-registration API (contract §3);
> - confirm `Windows.Media.Ocr` and media sessions are reachable from the target framework;
> - file the proposals in brief section 9. Do Q1, Q2, Q3, Q5 and Q9 first, because they shape your M1 design.
>
> Then deliver M1: implement `ISystemTools`, `IOcrFallback` and `IOcrRefResolver` (contract §7) against real Windows and WS4's fixtures.
> - Every act follows `resolve → precheck → gate → input → verify`, reports an accurate `input_sent`, and records phase timings.
> - Use fakes for WS0 and WS1 interfaces until those streams merge.
> - `app_launch` takes only Start-menu names or AUMIDs. Never add any path that reaches a shell, a script or an arbitrary executable.
>
> Tests that change desktop state must hold `Local\OmniWin.UiInputTestLock` and restore what they change. Meet the contract §15 budgets you own. Keep `docs/status/WS3.md` current: done / in progress / blocked, p50/p95 per op, and open proposals. Before every merge, `dotnet build -warnaserror` and `dotnet test` must pass.
