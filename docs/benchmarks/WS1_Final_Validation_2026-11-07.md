# WS1 final validation — 2026-11-07

Functional acceptance passes; strict physical-input latency acceptance does not. The final Release benchmark completed 200 measured cycles without failures, with exact click-counter and text verification. Debug and Release builds have zero warnings/errors, and both normal suites pass 406 tests with three opt-in scenarios skipped. The live fixture, repeated-input benchmark, and pointer diagnostics were run separately.

The [ten-workflow acceptance record](../status/WS1_Acceptance_2026-11-07.md) covers actual Notepad, Explorer, YouTube, and Maps. These are functional results; the native Edit fixture timings below do not establish Notepad or Chrome throughput.

## Final full-engine measurement

Artifact: `artifacts/benchmarks/ws1-input-final-apphost-release-100.json`. Release, .NET 10.0.14, Windows build 26300, active Admin RDP session 2, 10 warmup cycles followed by 200 measured cycles. The manifest-aware test EXE was used. Production stop/activity hooks and SafetyGate remained enabled. Diagnostics were enabled for this profile and are off in the normal server.

Each cycle performs a checked physical Apply click, provider focus on the editor, Ctrl+A, and 100 ASCII characters through seven focus-checked Unicode batches. There are exactly 200 measured samples for each of these four operations: click/focus/type confirmed; Ctrl+A unverified as expected because selection has no exposed tree change. Every click counter and typed value matched; Delete remained zero. Warmup samples are excluded. Percentiles use nearest rank.

| Measurement | p50 ms | p95 ms | Budget ms | Result |
|---|---:|---:|---:|---|
| Click checks: resolve + precheck | 9.899 | 11.902 | 10 | Over |
| Physical click SendInput | 19.224 | 31.457 | 1 | Over |
| Ctrl+A checks | 7.215 | 8.858 | 10 | Pass |
| Ctrl+A SendInput | 0.686 | 0.983 | — | Measured |
| 100-character typing checks | 101.040 | 120.307 | 10 | Over |
| 100-character SendInput, summed batches | 47.986 | 59.960 | 20 | Over |
| Ctrl+A verification wait | 300.311 | 311.221 | — | Measured |

Typing precheck time includes focus checks between batches, not only work before the first character. Seven `RequireKeyboardFocus` calls total 108.584 ms p95 per action; text-expectation capture is only 0.849 ms p95. Inclusive diagnostic scopes overlap their enclosing phases and must not be added to them. Percentiles from different phases cannot be added to reconstruct total-action percentiles.

Ctrl+A's default wait remains approximately 300 ms because the observed tree does not expose selection. Changing this default would change verification semantics. No such change was made.

## Retained optimization and correctness changes

Fresh `BuildUpdatedCache` now supplies the native HWND, avoiding a separate current-property read. A prepared physical e-ref point can be reused only inside fresh visible bounds with matching native ownership and an exact UIA hit. A failed candidate returns to normal provider point selection and the existing late Invoke/Toggle fallback. Final native/UIA hit checks, identity/policy comparison, and stop/activity boundaries remain.

The comparable preceding Release run measured click checks at 11.683/13.628 ms p50/p95. The optimized DLL-hosted run measured 9.873/12.274 ms; the final EXE run measured 9.899/11.902 ms. These sequential runs support a modest improvement, not compliance or a controlled claim about every application. No further speed gain was claimed from the frame-activation correction.

On this PC, the observed Notepad `RichEditD2DPT` burst corruption was avoided by one Unicode scalar per batch. Exact ASCII/accent/Greek/emoji readback and saved-file checks passed in the actual app. This correctness workaround retains the full per-batch guards; no 100-character Notepad speed claim is made.

## Pointer investigation

Earlier controlled runs isolated a reproducible focus-transition condition. With focus already on the button, repeated atomic clicks measured about 0.5 ms p95. Resetting focus to the editor before each click reproduced approximately 29–33 ms p95 even when OmniWin's activity hooks and UIA event subscriptions were absent. Move-only calls stayed below 0.2 ms p95. These experiments do not identify the precise Windows/provider mechanism or prove that other applications behave the same way.

Production clicks retain one atomic native batch. Splitting mouse movement from the button pair was diagnostic only; it remained over budget in focus-changing cases. Windows documents serial insertion for one `SendInput` batch. [Microsoft SendInput documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

The final manifest-aware pointer run is `artifacts/benchmarks/ws1-pointer-final-apphost-trace.json`, with `ws1-pointer-final-apphost-trace.fixture-trace.json`. It completed with no failure, 860 verified normal/split samples including warmups, and 200 measured samples per operation in each pooled hooks-on/off condition. Focus was reset to the editor, UIA event subscriptions were absent, and the real stop key remained active. Atomic native clicks measured 32.293 ms p95 with activity hooks on and 31.460 ms with them off. Move-only p95 was 0.113/0.070 ms; button-pair p95 was 25.145/25.002 ms.

The opt-in fixture trace captured 7,748 records with zero drops. It includes double-click-down messages because Windows can translate the second left-button down into that message.

| Selected target WndProc handler | Samples | p95 ms | Max ms |
|---|---:|---:|---:|
| Apply mouse down | 431 | 2.884 | 3.748 |
| Apply double-click down | 430 | 2.926 | 3.817 |
| Apply mouse up | 861 | 1.312 | 3.135 |
| Editor focus gained | 861 | 0.996 | 1.482 |
| Editor focus lost | 861 | 0.570 | 1.000 |
| Apply focus gained | 861 | 0.152 | 0.361 |
| Apply focus lost | 860 | 0.317 | 0.625 |

No recorded handler approaches the complete native-call tail. These are inclusive, sometimes nested spans for selected editor/button messages, with instrumentation enabled. They cannot be summed indiscriminately or treated as a trace of all Windows/provider callbacks. The precise remaining mechanism is unresolved. All measurements were in the connected RDP session; no console-versus-RDP causal comparison was performed.

## Read-only check

The final server EXE reported desktop ready, PerMonitorV2, no-delete SafetyGate, armed kill switch, activity hooks on, and audit log on. A read-only stdio run recorded `windows_list` p50 0.226 ms/p95 86.2 ms over 31 calls, and `desktop_observe` p50 0.336 ms/p95 2.4 ms over 30 calls. These distributions include cold startup and the first snapshot. The first two window-list client calls were slow, followed by 29 approximately 1 ms client calls; startup/cache tails therefore remain part of performance follow-up. This is not a steady-state p95 certification. The server-side status is retained as `artifacts/acceptance/2026-11-07T13-10-57-984Z/final-apphost-readonly-status.txt`.

The final apphost functional fixture also passed, including deletion denial, physical input, insertion/readback, modality, occlusion, covered-window patterns, and stale references. Its console result is retained as `final-apphost-fixture.txt` in the same acceptance directory. All owned fixture and validation server processes exited after testing.

## Reproduction and evidence integrity

Use `OmniWin.Server.exe` for MCP deployment and `OmniWin.Desktop.Tests.exe` for opt-in input tests so their PerMonitorV2 manifests take effect. The earlier ad hoc DLL launch was DPI-unaware. Final server status, Notepad/Explorer switching, and physical Notepad File-menu expansion were rechecked through the server EXE. Earlier raw DLL-hosted measurements are retained as historical diagnostics.

Build Release with warnings as errors. On an active, unlocked, untouched desktop, set `OMNIWIN_UI_BENCHMARK=1`, `OMNIWIN_BENCHMARK_TEXT_LENGTH=100`, `OMNIWIN_BENCHMARK_PROFILE=1`, and `OMNIWIN_BENCHMARK_OUTPUT` to a new absolute JSON path, then run:

```powershell
& '.\tests\OmniWin.Desktop.Tests\bin\Release\net10.0-windows10.0.19041.0\OmniWin.Desktop.Tests.exe' -class OmniWin.Desktop.Tests.DesktopBenchmarkTests -noColor -noLogo -showLiveOutput
```

Reset those environment variables after the run. Run only one input scenario at a time. Validate the failure field, sample count, channels, statuses, and readback assertions before accepting a report. Partial runs stopped by user activity or a disconnected/locked desktop are excluded.

The final input report records these SHA-256 hashes; the measured desktop assembly matches the final Release build:

| Assembly | SHA-256 |
|---|---|
| Desktop | `9DBDB51573C911D9C3839239306F7954F2E112D93F1F0D31388645C0D3E8A957` |
| Core | `26CAC2F4C4BFC9C57E959D4D7566A84BA76195992F31AF99E9E951ADD09A74EA` |
| Safety | `B7B6708B029A1131B76AE5B96ED4ABFDECA930BACB8AB1F3E5FB2F7A54ACD662` |
| Desktop test harness | `23BEE6AA27C9A71BABD338EDF0FF72CE66BA841EA33C6B2EBBDBADE182F494EC` |
| Fixture | `140B5067754E9CDFD875B736CF19B6364DEBB14B11F029F5E8A6C81EE0CA5924` |

Budgets remain unchanged. The remaining gate needs a demonstrated safe improvement in physical delivery and repeated focus verification, or an explicit owner decision about contract budgets. Disabling guards, silently replacing physical input with UIA patterns, or claiming the exact OS cause from these samples is unsupported.
