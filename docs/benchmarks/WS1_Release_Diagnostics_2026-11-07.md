# WS1 Release input diagnostics — 2026-11-07

Historical diagnostic stage. The subsequent [functional acceptance record](../status/WS1_Acceptance_2026-11-07.md) supersedes the everyday-task status below. These measurements used the DLL-hosted test runner; use the manifest-aware test EXE for subsequent physical-input runs.

Release measurements confirm the speed failures. The mouse slowdown can be reproduced by changing focus from the fixture editor to its button, even with OmniWin's activity hooks and UIA event subscriptions absent. The precise Windows/provider mechanism has not been traced. Removing those subscriptions is not a demonstrated fix.

The ten everyday acceptance tasks were not run. Performance budgets and production input checks remain unchanged.

Final validation: Debug and Release solution builds both passed with warnings as errors, zero warnings, and zero errors. Both normal automated runs reported 392 passed, zero failed, and three opt-in skips (functional fixture, repeated-input benchmark, pointer diagnostics). The Release repeated-input benchmark and accepted pointer diagnostic scenarios passed separately as described below.

## Full engine: 100-character typing

The complete run used Release, .NET 10.0.14, Windows build 26300, production stop/activity hooks, 10 warmup cycles, and 200 measured cycles. Each cycle verified a physical click, focus setup, Ctrl+A, and exactly 100 ASCII characters through seven Unicode batches. Every expected counter and text value matched. The report has no failure and 200 measured samples per operation.

| Measurement | p50 ms | p95 ms | p95 budget ms |
|---|---:|---:|---:|
| Click checks, resolve plus precheck | 11.597 | 12.802 | 10 |
| Click SendInput | 16.194 | 31.618 | 1 |
| Key checks | 7.406 | 8.878 | 10 |
| Ctrl+A SendInput | 0.678 | 0.987 | — |
| 100-character type checks | 94.081 | 115.565 | 10 |
| 100-character type SendInput, summed batches | 42.113 | 49.325 | 20 |
| Ctrl+A verification, including default wait | 303.393 | 313.459 | — |

Release does not solve the click or typing dispatch problem. The direct 100-character measurement replaces extrapolation from the earlier 23-character workload.

Full action p50/p95 were 133.542/154.160 ms for clicking, 311.598/322.545 ms for keys, and 239.923/262.249 ms for typing. These totals include verification and application/provider reaction. Their percentiles cannot be reconstructed by adding phase percentiles.

The first Release attempt stopped on real user activity with `blocked input_sent=false reason=user_active` after 55 successful measured typing actions. Its partial artifact is excluded from accepted benchmark figures. The owner then provided an idle desktop for the complete run.

Accepted artifact: `artifacts/benchmarks/ws1-input-release-profile-100-complete.json`.

## Where prechecks spend time

Optional diagnostics capture inclusive method durations and invocation counts per action, on the existing MTA worker. They are separate from `Timing.PhasesMs` and must not be added to their enclosing phase totals. The normal server has no diagnostic sink; disabled scopes do not allocate or read the clock.

| Diagnostic | Calls per action | p50 ms per action | p95 ms per action |
|---|---:|---:|---:|
| Typing RequireKeyboardFocus | 8 | 87.328 | 105.298 |
| Typing FocusForTyping | 1 | 1.540 | 2.000 |
| Typing CaptureTypingExpectation | 1 | 0.535 | 0.789 |
| Typing CheckInputBoundary | 9 | 1.059 | 1.596 |
| Click Prepare ReadLive | 1 | 1.546 | 1.859 |
| Click Dispatch ReadLive | 1 | 1.491 | 1.805 |
| Click Prepare HitTest | 1 | 1.586 | 1.897 |
| Click Dispatch HitTest | 2 | 3.057 | 3.588 |

Text capture is not the dominant typing precheck cost in this native Edit fixture. Fresh UIA/native focus verification between batches dominates. Focus queries can wait on the target/provider while prior input is processed; these timings do not separate provider execution from scheduling or message processing.

Removing the extra pre-capture focus check and the outer boundary immediately repeated by FocusForTyping is a potential small optimization. It would leave all per-batch checks in place and cannot remove the dominant seven-batch cost. This follow-up retains the original dispatch behavior. Deferring a middle click hit test also needs explicit tests for the existing late Invoke/Toggle fallback before changing it.

## Pointer controls

Each accepted control run alternates atomic and split clicks, with activity hooks on/off/on. The first and last blocks each have 100 measured pairs, and the middle block has 200, giving 200 measured samples per operation for each pooled hook condition. Ten warmup pairs per block are excluded. The real stop hotkey stays registered in every block.

The dedicated MTA probe validates the fixture PID, HWND, UIA runtime identity, enabled state, input desktop, foreground/focus ownership, native/UIA hit, and gate authorization before sending. Split button input additionally verifies the actual cursor after the move. Every click increments Apply exactly once; Delete remains zero. Readback and all checks are outside the native call timings.

All accepted control reports have `Completed=true`, no failure, and complete verified samples.

| UIA event subscriptions | Focus before each click | Activity hooks | Atomic click p50/p95 ms | Move-only p95 ms | Button-only p95 ms |
|---|---|---|---:|---:|---:|
| On | Button remains focused | On | 0.379 / 0.500 | 0.086 | 0.945 |
| On | Button remains focused | Off | 0.266 / 0.361 | 0.059 | 0.462 |
| On | Reset to editor | On | 24.824 / 29.100 | 0.090 | 21.417 |
| On | Reset to editor | Off | 24.950 / 28.762 | 0.058 | 21.287 |
| Off | Reset to editor | On | 25.158 / 29.364 | 0.091 | 22.216 |
| Off | Reset to editor | Off | 23.896 / 28.844 | 0.066 | 21.793 |

The controlled focus reset reproduces the large delay; omitting OmniWin's activity hooks and event subscriptions does not remove it. This rules out either subscription alone as a sufficient explanation for the delay in this fixture. It does not identify the exact Win32, accessibility-provider, or application focus-processing mechanism, nor establish results for other applications.

The summed split calls exclude checks between the move and buttons. For editor-focus cases their p95 was 21–22 ms, still above budget. Splitting is a diagnostic only. Production clicks retain one batch: Windows guarantees that a SendInput batch's events are inserted serially without other injected/user input interspersed. [Microsoft SendInput documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)

Low-level hook callbacks run on the installing thread, which must pump messages. That context-switch behavior is documented, but the hook on/off comparison here is more specific evidence than assuming hooks cause the entire 30 ms delay. [Microsoft LowLevelMouseProc documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)

Accepted control artifacts:

- `artifacts/benchmarks/ws1-pointer-release-with-uia-events.json`
- `artifacts/benchmarks/ws1-pointer-release-focus-transitions.json`
- `artifacts/benchmarks/ws1-pointer-release-focus-no-uia-events.json`

The earlier `ws1-pointer-release-on-off-on.json` is an exploratory harness revision with 200 pairs in every block. It also passed, but the pooled comparisons above use the later balanced on/off/on runs.

## What changes next

The retained changes add optional per-action diagnostics and fixture-only experiments. The public KillSwitch constructor continues to install both activity hooks. Only the internal constructor exposed to tests can omit them; it still registers the stop hotkey. No application security settings were changed during these experiments.

Further speed work should trace the target/provider and Windows focus path, and distinguish focus-already-on-target from focus-changing clicks in acceptance workloads. Simply disabling OmniWin's hooks, dropping focus checks, or splitting production input batches is not supported by these measurements. Explicit existing `invoke` and `set_value` actions remain options when their logical UIA behavior matches the requested operation; they do not establish physical-input budget compliance.

Ctrl+A's default verification wait remains approximately 300 ms because selection is not represented as an observable tree change. Meaningful explicit expectations can avoid waiting for unrelated changes when an application exposes a suitable condition. Shortening that default would change the current verification contract and has not been done.

## Reproduce

Build Release first. Run only on an unlocked idle desktop. Each command must finish and its report must be validated before starting another input test.

For the full-engine profile, set `OMNIWIN_UI_BENCHMARK=1`, `OMNIWIN_BENCHMARK_TEXT_LENGTH=100`, `OMNIWIN_BENCHMARK_PROFILE=1`, and a unique `OMNIWIN_BENCHMARK_OUTPUT` path. Run:

```powershell
& '.\tests\OmniWin.Desktop.Tests\bin\Release\net10.0-windows10.0.19041.0\OmniWin.Desktop.Tests.exe' -class OmniWin.Desktop.Tests.DesktopBenchmarkTests -noColor -noLogo -showLiveOutput
```

For pointer controls, set `OMNIWIN_POINTER_DIAGNOSTICS=1` and a unique `OMNIWIN_POINTER_DIAGNOSTICS_OUTPUT`. Set or unset `OMNIWIN_POINTER_UIA_EVENTS=1` and `OMNIWIN_POINTER_RESET_FOCUS=1` for the intended case. Run:

```powershell
& '.\tests\OmniWin.Desktop.Tests\bin\Release\net10.0-windows10.0.19041.0\OmniWin.Desktop.Tests.exe' -class OmniWin.Desktop.Tests.DesktopPointerDiagnosticTests -noColor -noLogo -showLiveOutput
```

Restore the environment variables after each run. Inspect the test summary and raw completion/failure/sample-count fields; do not rely on the direct runner's exit code alone. Generated JSON artifacts are intentionally ignored by Git.
