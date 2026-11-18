# WS0 status (lead)

Updated 2026-11-06. Contract version **2.1** (`OmniWin.Core.OmniWinContract.ContractVersion`).

## Done: M0

- Repo, `OmniWin.sln`, `Directory.Build.props`, `Directory.Packages.props` (central versions), `.editorconfig`, `global.json` (SDK 10.0.403, Microsoft.Testing.Platform).
- All 17 projects from contract §4 exist and build. Engine projects are empty for WS1–WS3. `testapps/OmniWin.FixtureApp` and `bench/OmniWin.Bench` have placeholder `Program.cs` files that WS4 owns and replaces.
- `OmniWin.Core` has every §6–§7 type and interface, plus:
  - `ReasonCodes` and `Channels` constants;
  - `CoreJson.Options` (snake_case; strict about missing required values);
  - `ResultFormatter` (§9.3 status lines, `Quote` for untrusted names/values);
  - `ToolDocs` (§10.3 wording);
  - `RollingMetrics` (`IMetrics`).
- `OmniWin.Safety` has `OmniWinPaths` (with the `--data-dir` override) and `ConfigStore`. The config loader is strict: unknown keys and bad values stop startup.
- `OmniWin.Server` has `Program.cs`, `app.manifest` (PerMonitorV2, asInvoker), the stdio MCP host (SDK `ModelContextProtocol` 2.3.1), and the `omniwin_status` tool. Startup:
  - refuses unknown arguments (exit 2);
  - refuses a bad config, or `test_auto_allow` without `--test-mode` (exit 3);
  - refuses to run elevated (exit 4).
- Contract gaps 1–4 from the handoff README are settled in contract 2.1.

## Verification (this PC, Debug build)

| Check | Result |
|---|---|
| `dotnet build OmniWin.sln -warnaserror` | 0 warnings, 0 errors |
| `dotnet test --solution OmniWin.sln` | 30/30 passed (Core 22, Safety 8; the other five projects have no tests yet) |
| MCP handshake over stdio | `initialize` ok (protocol 2025-06-18), `tools/list` = `omniwin_status` with read-only annotations |
| stdout purity | 0 non-JSON lines across 300 calls; 0 stderr lines after filtering SDK request logs |
| DPI awareness reported by the running server | `per_monitor_v2` (the manifest takes effect) |
| Clean shutdown when stdin closes | exit code 0 |
| Startup refusals | bad argument → 2; `test_auto_allow` without `--test-mode` → 3; misspelled config key → 3 |

Measured timings (3 runs × 300 calls):

| Operation | p50 | p95 | Notes |
|---|---|---|---|
| `omniwin_status` handler, server-side | 0.033–0.035 ms | 0.048–0.056 ms | p99 0.85–0.93 ms (first calls, JIT) |
| Full round trip over stdio, measured by the client | 0.42–0.44 ms | 0.55–0.69 ms | includes the Python client and pipe |
| Process start → `initialize` reply | ~305 ms warm | | 5.2 s on the very first run of a new exe (cold start, antivirus scan) |

Not measured yet: tool-call overhead as its own phase (needs the call filter, M1).

## Next: M1 (WS0)

- Safety: gate and risk classification, native confirm dialog, kill switch, user-activity hooks, audit log, rate limit.
- A call-tool filter around every tool, for audit entries and per-tool metrics. The SDK has `WithRequestFilters` / `AddCallToolFilter`.
- Rolling file log next to stderr.

## Notes for other streams

- Use `CoreJson.Options` for tool arguments. The model's JSON is snake_case, and `kind` may appear anywhere in an expectation.
- Use `ResultFormatter.Quote(text, ResultFormatter.MaxNameChars / MaxValueChars)` for every untrusted name and value. It escapes quotes, backslashes and control characters, so app text can't close a quote or start a new line.
- Register an `IComponentStatus` for your engine so `omniwin_status` shows it.
- Tests: always start the server with `--data-dir <temp>`. When you add your first test, remove the `--ignore-exit-code 8` line from your test project; it exists only so empty projects pass at M0.
- Packages: versions are central in `Directory.Packages.props`. FlaUI.UIA3 5.0.0, CsWin32 0.3.369, Shouldly 4.3.0 and BenchmarkDotNet 0.15.8 are already listed. Ask WS0 for anything else.

## Open proposals

None.

## 2026-11-08: Phase 3, run log and workflows (WS0)

Implemented by the WS0 workflows agent. Contract changes are listed for the lead under "Contract notes for 2.4"; the contract file itself was not edited here.

**What exists now**

- **Run log** (`OmniWin.Workflows/RunLog.cs`, contract in `OmniWin.Core/RunLogContracts.cs`): in memory, newest 2000 steps, ids `s1`, `s2` … never reused. A step holds the tool, the target element's descriptor (its `Path` is the selector), literal non-text arguments, and a placeholder name (`p1`, `p2` …) for any text. **Typed, set and clipboard text is never stored.**
- **What is recorded** (`StepRecorder`): an act that ended `confirmed`, or `unverified` with at least one diff line (`+ `, `- `, `~ `). Window ids (`w3`) are stored as the window's process name because ids last one session. An element act whose element cannot be described is not recorded (it could not be found again).
- **Where recording happens:** single acts through `Hosting/RunLogFilter.cs`, a call-tool filter that wraps `desktop_act`, `desktop_keys`, `desktop_scroll`, `browser_act`, `browser_nav`, `app_launch`, `window_manage`, `audio`, `media` and `clipboard_set` (so the tool files are unchanged), looks the element up before the act (time-boxed to 750 ms, metric `run_log_prepare`), and adds `step=sN` to the result's status line. Steps run inside `run_steps` / `workflow_run` are recorded by the runner.
- **`run_steps`** (`WorkflowTools.cs`): a list of `{tool, <that tool's parameters>}`; `stop_on_failure` defaults to true. Tools allowed: the ten above. Anything else is refused as `blocked unsupported_action` with no input. Missing parameters are refused the same way. A tripped stop key denies the step (`kill_switch`) before any engine call. A stop mid-run returns `error input_interrupted` with `input_sent=true`.
- **A step passes** when it is `confirmed`, or `unverified` with a non-empty diff (the same rule that decides what the log keeps). The run stops at the first step that did not pass and returns it with a current observation.
- **`workflow_save(name, description, from_step, to_step, params?)`:** copies run-log steps `from_step`..`to_step` into `<data dir>\workflows\<name>.json`. `params` renames placeholders (`{"p1":"search_text"}`). Every step is first checked with `ISafetyGate.Classify` using what is stored (target name and id, keys, process, address); a step the gate would deny means nothing is saved (`denied policy_forbidden`, naming the step). A name that exists is refused and the file is left as it is: the store creates files with `FileMode.CreateNew` and never replaces or deletes one.
- **`workflow_list`:** name, description, step count, parameter names. Files that do not load are skipped.
- **`workflow_run(name, params?)`:** loads and validates the file (known tools only, declared parameters only), refuses a missing or unknown parameter before any step, re-checks every step with the gate (a hand-edited file with a delete step is refused before step 1), then for each step resolves the selector fresh (`ResolveSelectorAsync`, which returns `ResolvedRef(Ref, Generation)` and records the scan) and acts through the normal engine act path, so the engine's own gate, stop key, user-activity pause and prechecks run for every step. A selector that matches nothing is `stale not_found` with `input_sent=false`.
- **`wait_for(condition, timeout_ms?)`:** `ConditionRouter` sends `url_contains` and refs/scopes starting with `b` or `t` to the browser, refs/scopes starting with `e`, `o` or `w` and anything that names nothing specific to the desktop; a condition mixing both is refused (`unsupported_action`). Timeout default is `config.Timeouts.DefaultExpectMs`, clamped to 1..30000.
- **`omniwin_status`:** `Hosting/ServerState.KnownComponents` already listed every engine; this adds one detail line for `browser`, `system_tools`, `ocr` and `workflows` when they register one (the workflows line: `N saved, M steps in the run log`) and registers the `workflows` component.
- **Every handler** links the call's token with the kill switch's token and a time limit (10 s + 40 s per step, at most 300 s), never throws, and uses `ResultFormatter`. Model-supplied names are made single-line tokens before they are echoed.
- **Benchmark hook:** each workflow/run_steps step records metric `workflow_run_step` (`WorkflowService.StepMetric`) = step time minus the engine's `verify` phase (the app's reaction), and `StepRun` carries `ResolveMs`, `TotalMs` and `OverheadMs` for the Phase 2/3 benchmark (budget 20 ms per step).

**Tests** (`tests/OmniWin.Core.Tests`, which now also references `OmniWin.Safety` and `OmniWin.Workflows`; no new test project, `OmniWin.sln` untouched): `RunLogTests` (4), `StepRecorderTests`, `WorkflowStoreTests`, `WorkflowServiceTests`, `ConditionRouterTests`, `WorkflowFormatterTests` — 53 new test methods, 127 tests in the project, all passing in Debug and Release. They use a fake desktop and system engine, the real `SafetyGate`, and temp folders only; they send no input and open nothing.

**Also checked by hand:** the Release server started over stdio with `--data-dir` in a temp folder, listed 24 tools, generated schemas for `run_steps`, `wait_for` and `workflow_save`, and answered `omniwin_status`, `workflow_list`, a refused `run_steps`, refused `workflow_save` / `workflow_run`, and a mixed and a browser `wait_for`, with no input sent.

**Not verified here:** the order of call-tool filters. `RunLogFilter` is registered after `ToolCallAudit.Filter` on the assumption that the first registered filter is the outermost, so the audit line sees the `step=` token. The Phase 2 stdio harness should assert that an audit line for a recordable act contains the step id.
