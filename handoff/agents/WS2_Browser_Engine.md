# WS2 — Browser Engine: Workstream Brief

**Read with:** `02_Architecture_and_Contracts.md` (cited as §N) and `03_Build_Orchestration.md` (cited as 03 §N).
The contract wins on any conflict. This brief adds no types, tools, or reason codes. Anything it seems to need
beyond the contract is listed in section 9 as a proposal to file.

## 1. Mission and scope

**Mission:** give the AI client a compact, honest view of web pages and a safe way to act on them, inside a
dedicated browser profile that OmniWin launches and controls. Priorities are those of §1: never act on the wrong
target, never hide uncertainty, be fast.

**In scope**
- Own thin CDP client over `ClientWebSocket` (§3): one WebSocket to the browser, one session per target in
  flatten mode (§5). No Playwright or Puppeteer.
- Launcher driven by the `browser` block of config (§14), started lazily on first browser tool call (§5). Uses only
  the dedicated profile. Never opens, reads, or attaches to the user's personal profile or to a browser instance
  OmniWin did not launch.
- Injected index: the page-side element index per tab and frame, kept live from DOM changes, so `browser_observe`
  is served from it (§15) and each tab's generation advances per §10.1.
- The `IBrowserEngine` implementation (§7), with the act order `resolve → precheck → gate → input → verify`, the
  gate called by the engine, default expectations (§10.4), and an accurate `input_sent` on every result (§10.2).
- Structured data for `ResultFormatter` (§9.1–§9.3): browser header, `bN`/`tN`/`fN` refs, redaction, truncation.
- The four browser tools in `Tools/BrowserTools.cs` (§8), engine tests, and phase timings via `IMetrics` (§15).

**Out of scope (owned elsewhere)**
- Gate logic, risk lexicon, confirm dialog, kill switch, user-activity hooks, audit log, config, `ResultFormatter`,
  `ToolDocs`, Core types, server host and tool wiring, `wait_for`, `run_steps`, run log, `workflow_*` (WS0).
- Web fixture pages and their server, the end-to-end MCP suite, benchmarks and baselines (WS4). Desktop UIA (WS1).
- Everything §8 lists as deliberately absent. The injected index is internal; no tool lets the model run page
  script, and no tool returns screenshots, cookies, storage, or password values (§13.4).

## 2. Files owned and files not to touch

**Owned by WS2 (§4)**, paths relative to `OmniWin/`:

| Path | Contents |
|---|---|
| `src/OmniWin.Browser/` | CDP client, launcher, injected index, `IBrowserEngine` implementation |
| `src/OmniWin.Server/Tools/BrowserTools.cs` | The four browser tools |
| `tests/OmniWin.Browser.Tests/` | WS2 unit and engine tests |
| `docs/status/WS2.md` | WS2 status file (§16, 03 §5) |
| `docs/contract-proposals/WS2-*.md` | WS2 proposals (§17, 03 §6) |

WS0 creates the empty `OmniWin.Browser` and `OmniWin.Browser.Tests` projects at M0 (§4, 03 §2). WS2 works inside
them, never adds a project, and never edits the `.sln`. For a change anywhere else, write a proposal or ask the
owner; until it is accepted, code to the current contract with any adapter inside WS2's own folder (§17).

**Do not touch** (owner in brackets): `OmniWin.sln`, `Directory.Build.props`, `.editorconfig`, `global.json` [WS0];
`src/OmniWin.Core/`, including `ResultFormatter` and `ToolDocs` [WS0]; `src/OmniWin.Server/Program.cs`,
`app.manifest`, `Hosting/`, and every other file in `Tools/` [WS0, WS1, WS3]; `src/OmniWin.Safety/`,
`src/OmniWin.Workflows/` [WS0]; `src/OmniWin.Desktop/` [WS1]; `src/OmniWin.SystemTools/`, `src/OmniWin.Ocr/` [WS3];
every other project under `tests/`, all of `testapps/` (including `web-fixtures/`), all of `bench/` [WS0, WS1,
WS3, WS4]; top-level `docs/` and other streams' status and proposal files; the handoff files themselves.

## 3. Interfaces provided and consumed (§7)

**Provided: `IBrowserEngine`**

| Member | Called by | WS2 must guarantee |
|---|---|---|
| `ListTabsAsync` | `browser_tabs` | `tN` ids stable for a tab's life, never reused (§9.1); `TabInfo` per §6 |
| `NavigateAsync` | `browser_nav` | Every `BrowserNavRequest` action in §7; gate before input; `navigation_failed` on failure |
| `ObserveAsync` | `browser_observe`; act results (`After`) | Full and diff per §9.1–§9.2; evicted base → full observation, `IsDiff=false` |
| `ActAsync` | `browser_act`; WS0 `run_steps`, `workflow_run` | Every `BrowserActRequest` action in §7; §10.1 validation; §10.4 defaults |
| `WaitForAsync` | WS0 `wait_for`; WS2 verify step | Event-driven from CDP events (§11); any polling fallback bounded at 25 ms and commented |
| `DescribeAsync` | Safety, Workflows (§6) | `Engine="browser"`, `Url`, `IsPassword`, and `Path` from the document root |
| `ResolveSelectorAsync` | WS0 `workflow_run` (§12) | A fresh `bN` for a stored path on a page matching the URL pattern, or null |

Expectation kinds the browser engine evaluates (§6, §11): `url_contains`, `text_appears`, `element_appears`,
`element_gone`, `value_equals`, `focus_on`, `toggle_is`, `all_of`, `any_of`. The browser meaning of the desktop
kinds `window_opened` and `window_closed` is open question Q7.

**Consumed**

| Interface or type | Owner | How WS2 uses it |
|---|---|---|
| `ISafetyGate` | WS0 | Called after precheck, before input, with an `ActionDescriptor` (§7). Not allowed → `denied`, `input_sent=false` |
| `ActionDescriptor`, `ElementDescriptor`, `GateResult`, `RiskClass` | WS0 | WS2 fills the descriptor: the facts the §13.1 browser rules need, `Keys` for `press`, a pre-redacted `ValuePreview` |
| `IKillSwitch` | WS0 | Every CDP call's token is linked to it (§5); tripped → `denied kill_switch` (§13.3) |
| `IUserActivity` | WS0 | Checked before each input chunk; active → `blocked user_active` (§13.3) |
| `IMetrics` | WS0 | Each phase recorded under the §6 `Timing` phase keys (§15) |
| `IAuditLog` | WS0 | Every browser tool call is audited (§13.4); who writes the entry is Q3 |
| `ResultFormatter`, `ToolDocs` | WS0 | All model-facing text (§9) and tool description wording (§8, §10.3) |
| Core records and config | WS0 | Records from §6; config `browser` block, `timeouts.cdp_command_ms`, `observe` caps (§14) |
| `IDesktopEngine`, `IForegroundService` | WS1 | Not used: browser input goes over CDP, not through the real mouse or keyboard |

## 4. Tools registered (§8 rows owned by WS2)

| Tool | Engine call | Default risk | WS2 notes |
|---|---|---|---|
| `browser_tabs` | `ListTabsAsync` | Read | Output format and untrusted header for tab lists: Q11 |
| `browser_nav` | `NavigateAsync` | Normal | Navigation to non-http(s) schemes is Risky (§13.1) |
| `browser_observe` | `ObserveAsync` | Read | `since_gen` returns a diff; `viewport_only`, `include_text` per §7 |
| `browser_act` | `ActAsync` | Normal/Risky | Risky by lexicon and the browser bullets of §13.1 |

Rules for all four:
- Names and parameters exactly as in §8, snake_case, set explicitly in the SDK attributes. Check the current SDK
  API against its `samples/` folder before writing `BrowserTools.cs` (§3). Descriptions use `ToolDocs` constants.
- Handlers are async, take the kill-switch-linked token, have a timeout, and never let an exception escape
  (§5, §16). A hung engine call returns `error engine_timeout` with an accurate `input_sent`.
- Browser not running or connection lost → `error browser_not_connected`. Nothing is written to stdout (§3).
- The engine also serves two WS0 tools: `wait_for` (browser conditions) and `omniwin_status` ("browser attached?", Q2).

## 5. Milestone deliverables (03 §2)

Branch `ws2-browser` in its own worktree (03 §3). WS2 merges last at each milestone (WS0 → WS4 → WS3 → WS1 → WS2).
Before every merge, `dotnet build -warnaserror` and `dotnet test` pass on the branch.

**M0 — Skeleton (WS0 builds it; WS2 starts in parallel)**
- Code in WS2's own folders against the contract right away; build once WS0's empty projects land.
- Create `docs/status/WS2.md` with the section 7 table; send WS4, through WS0, the page list in section 6; file
  the early proposals Q1, Q3, Q4, Q5, Q14, Q15.
- Spike: launch the dedicated profile, connect, list targets, attach one session per target.

**M1 — Engine works standalone against fixtures**
- All `IBrowserEngine` members working against fixture pages; `ResolveSelectorAsync` in a basic form.
- Injected index with per-tab generations and diffs; frames reported per §9.1.
- Full act pipeline with the gate call, default expectations, and event-driven waits. Test doubles for gate, kill
  switch, user activity, and metrics until WS0's Safety lands in this milestone.
- Engine tests green; first p50/p95 for both owned §15 rows in the status file.

**M2 — Integrated through MCP**
- `BrowserTools.cs` registers all four tools; they pass WS4's end-to-end suite over stdio.
- Real gate, kill switch, user activity in use; `wait_for` routes to `WaitForAsync`; WS2 numbers in the first report.

**M3 — Hardening**
- With WS0's real gate, every browser case in WS4's adversarial suite and every section 6 case returns the
  expected status and `input_sent` (§15 accuracy gates).
- Failure paths solid: browser killed, tab closed mid-act, navigation during act, CDP command timeout, cross-origin frames.
- Owned budgets met, or WS0-approved exceptions recorded in the status file.

**M4 — Workflows and `run_steps` (WS0 builds them)**
- `ElementDescriptor.Path` stable enough to replay; `ResolveSelectorAsync` resolves recorded steps fresh (§12).
- Replayed steps take the normal act path, gate included. Diffs accurate, since the run log uses them (§12).

**M5 — Release (WS0, WS4)**
- Packaged build creates and uses the dedicated profile on first run on a clean machine.
- Final p50/p95 recorded; no WS2 proposal open; no test skipped without a written reason; support 03 §8 checks.

## 6. Test plan against local web fixture pages

**Where:** engine tests in `tests/OmniWin.Browser.Tests/` (WS2); end-to-end tests in `tests/OmniWin.Integration/`
(WS4 writes, WS2 fixes failures). Pages: WS4's `testapps/web-fixtures/` on its local server. WS2 describes them; WS4 builds them.

**Ground rules**
- Tests point `profile_dir` at a throwaway folder per run. No test touches the user's own browser profile.
- Each fixture page shows a log of the input events it received. Tests read it back, so `input_sent=false` is
  proven by the page, not assumed. Every act test asserts status, `input_sent`, reason code, and the page log.
- Engine tests use test doubles for gate, kill switch, and user activity. Risk classification is checked by calling
  WS0's real `Classify` directly (no dialog appears); allow and deny paths use a scripted test gate.
- CDP input does not move the real mouse or keyboard, but a visible browser window can take foreground. Hold
  `Local\OmniWin.UiInputTestLock` (03 §4) for any WS2 run that opens a visible browser window until Q14 is decided.

**6.1 Core behavior**

| Area | Page needed | Pass when |
|---|---|---|
| Launch, isolation | Any | First call starts the dedicated profile; later calls reuse it. With the user's own Edge open, `browser_tabs` lists only OmniWin's tabs |
| Browser lost | Any | Browser process killed → next call `error browser_not_connected`; nothing hangs past `cdp_command_ms` |
| Tabs, navigation | Two linked pages | Every nav action in §7 works; `url_contains` met; refs from a closed tab → `stale` |
| Bad navigation | Unreachable local URL | `navigation_failed` |
| Observe format | All §9.1 roles, long names and values, password field, disabled/required/invalid fields, offscreen items, over 150 elements | Roles, states, `<redacted>`, `…` with `(len=N)`, omitted and truncated footers per §9.1 |
| Ref rules | Same page, reloaded | New refs after reload; no `bN` or `tN` number reused (§9.1) |
| Frames | Same-origin iframe; iframe on a second local port | `frames=N` and `(fN)` prefixes; cross-origin behavior per Q9 |
| Diffs | Page that changes on a button press | `since_gen` returns a §9.2 diff; a very old base returns a full observation |
| Default checks | One control per §7 action | Each action gives its §10.4 default result |
| Read-back mismatch | Field that rewrites what is typed | `contradicted readback_mismatch`, `input_sent=true` |
| No change | Button that does nothing | `unverified`, "no visible change" |
| Stale target | Element removed, replaced, or renamed after observe | `stale`, `input_sent=false`, fresh diff. Element only scrolled → act proceeds (§10.1) |
| Pre-checks | Disabled button; element under an in-page overlay; offscreen element | `blocked` with the matching reason (overlay mapping per Q10); page log empty |
| Timeouts | Page that never reacts, explicit `expect` | `timeout`, `input_sent=true`; the 30000 ms `timeout_ms` limit enforced (§11) |
| Waits | Element that appears after a delay | Met from events; any use of the polling fallback is logged |
| Kill switch, user activity | Text field | Before input: tripped → `denied kill_switch`, user active → `blocked user_active`, both `input_sent=false`. Either one mid-way through a 100-character `type` → stops before the next chunk, `input_sent=true` (status per Q5) |
| Downloads | Link to a downloadable file | No file written (`downloads: deny`, §14) |

**6.2 Risky-action rules (§13.1, §13.2)**

| Page content | Action | Pass when |
|---|---|---|
| Button "Delete account" | click | Risky (lexicon on name). Deny → `denied user_denied`, `input_sent=false`, page log empty |
| Neutral button with id or `data-testid` "submit-order" | click | Risky (lexicon on `AutomationId`, §6, §13.1) |
| Text field focused | press `ctrl+enter`, `ctrl+w` | Risky (§13.1 key rules) |
| "Continue" in a form with a password field | click | Risky (submits a password form) |
| "Next" in a form with a `cc-*` autocomplete field | click | Risky (payment fields) |
| Password field | type | Risky; `ValuePreview` and audit entry hold no password text (§13.4) |
| File input | click | Risky |
| `file:`, `data:`, `javascript:`, or browser-internal URL | `browser_nav navigate` | Risky; on deny nothing loads |
| Enter pressed inside the password or payment form | press | Risky, pending Q6 |
| Lexicon words only inside longer words ("Postcode", "Reorder") | click | Normal (word-boundary match, §13.1) |
| Any Risky case, gate allows | as above | Act proceeds; status reflects the page, not the gate |
| Gate returns `confirm_timeout` | any Risky | `denied`, `input_sent=false`; gate time kept out of budgets (§15) |
| Target changes while the confirm dialog is open | click | `stale`, `input_sent=false`, pending Q4 |

**6.3 Untrusted-content rules (§13.4, §9.1)**
- Every observation and diff starts with the untrusted-content header, including diffs attached to act results.
- Injection page: names, aria-labels, values, title, and URL path hold instruction-like text, a fake status line,
  a fake header, and fake ref lines. Pass when that text stays inside quoted fields, no line break or quote in it
  starts a new line or ends a field, no extra refs appear, and behavior is unchanged. Formatter gaps go to WS0.
- Hidden text (`display:none`, `aria-hidden`, zero size, positioned offscreen) is not listed by default (§9.1).
- Secrets: a page pre-fills a password field and sets cookie and storage values. Search every tool output, log
  file, and audit file for those values; none may appear. Tab titles and URLs are page-controlled too (Q11).

## 7. Performance budgets owned (§15) and reporting

| §15 row | p95 budget | What WS2 times |
|---|---|---|
| `browser_observe` from live index (≤150 elements) | ≤ 10 ms | `read` + `format` phases with the index already built, on a page of 150 interactive elements |
| `browser_act` excluding page reaction | ≤ 10 ms | `resolve` + `precheck` + `input` + `verify` processing; gate time and waiting for the page excluded |

Also reported, no WS2 budget (see Q16): cold `browser_observe` (first read after navigation), browser launch to
first observation, verify time after the awaited CDP event (against the generic §15 verify row), and handler
overhead in `BrowserTools.cs` (part of the tool-call overhead row WS0 owns).

**Method:** `Stopwatch` via `IMetrics` around each §6 phase key (§15); Release build; reference PC (§2) with the
browser version noted; warm-up runs dropped; at least 500 samples per operation. Gate and confirm time are
reported separately, never inside a budget.

**In `docs/status/WS2.md`**, next to done / in progress / blocked and open proposals (03 §5), updated every
milestone; any row over budget at M3 needs a WS0-approved exception listed there:

```
## Performance (ms)
| Operation                         | p50 | p95 | Budget | Samples | Conditions                     | Date / commit |
| browser_observe, live index, ≤150 |     |     | ≤ 10   |         | page, element count, Edge ver. |               |
| browser_act, excl. page reaction  |     |     | ≤ 10   |         | action mix                     |               |
| browser_observe, cold (report)    |     |     | —      |         |                                |               |
| launch to first observe (report)  |     |     | —      |         |                                |               |
## Budget exceptions
- <row>: measured p95, cause, WS0 decision and date
```

## 8. Definition of done (§16)

Shared, from §16: zero-warning build; unit tests pass; WS2's integration tests pass against the fixture pages;
p50/p95 recorded in `docs/status/WS2.md`; no file touched outside WS2's ownership; every contract deviation
recorded as an approved proposal.

WS2 checks on top, all from the contract:
- §16 coding standards met, including no exception escaping a tool handler (errors become `ActResult(Error, …)`
  with an accurate `InputSent`) and no allocation-heavy LINQ in the observe and format path.
- Every act follows `resolve → precheck → gate → input → verify` (§7). Logs go to stderr and the rolling file only (§3).
- All section 6 cases pass; WS4's suite shows zero wrong-target actions and zero false `confirmed` (§15).
- `dotnet build -warnaserror` and `dotnet test` pass on `ws2-browser` before each merge (03 §3).

## 9. Open questions to raise as contract proposals (§17)

File each as `docs/contract-proposals/WS2-<topic>.md` with problem, proposed change, and effect on other streams.

- **Q1 `WS2-browser-mode`:** §14 names only `mode: dedicated`. Propose the launcher refuse any other value, any
  `profile_dir` that is a browser's default user-data folder, and any attach to a browser it did not launch.
- **Q2 `WS2-engine-status`:** `omniwin_status` reports "browser attached?" (§8), but `IBrowserEngine` (§7) has no
  member for it. Propose a read-only connection-state member. Affects WS0.
- **Q3 `WS2-wiring-and-audit`:** who constructs the engine lazily (§5) and hands it gate, kill switch, and metrics;
  and who writes the `IAuditLog` entry for browser tools, the handler or WS0's host.
- **Q4 `WS2-recheck-after-gate`:** a confirm can take up to 60 s (§13.2) and the page can change meanwhile. Propose
  re-running the §10.1 target check after the gate and before input; changed → `stale`. Same issue for WS1.
- **Q5 `WS2-interrupted-input`:** `denied` and `blocked` require `input_sent=false` (§10.2), but a kill switch trip
  or user activity during partial typing leaves `input_sent=true`. Propose `error` with reason `kill_switch` or
  `user_active`. Affects WS1 and WS4's expected results.
- **Q6 `WS2-browser-risk-triggers`:** §13.1 names clicks that submit a password or payment form, and navigation to
  non-http(s) schemes. Propose Risky also for `press enter` in such a form, clicks on links to such schemes, and
  `browser_nav close_tab` (Normal in §8, while `ctrl+w` is Risky).
- **Q7 `WS2-tab-opened`:** a click that opens a new tab or popup has no expectation kind; `window_opened` is a
  desktop kind (§6, §11). Propose a browser kind, or define `window_opened` / `window_closed` for tabs.
- **Q8 `WS2-js-dialogs`:** page alert, confirm, prompt, and leave-page dialogs are not covered by §9.1 or §13.1.
  Need how they appear, their risk class, and the reason code when they block the page.
- **Q9 `WS2-frames`:** does `cross_origin_frame` mean such frames are listed but not actionable, or not indexed?
- **Q10 `WS2-in-page-occlusion`:** propose `occluded_same_window` for in-page overlays, `modal_blocking` for in-page modals.
- **Q11 `WS2-tabs-format`:** §9 has no format for `browser_tabs`, and the §13.4 header covers only observations,
  diffs, and OCR, yet titles and URLs come from pages. Propose a format line plus the header. Affects WS0.
- **Q12 `WS2-payment-redaction`:** §9.1 redacts password values only. Propose redacting `cc-*` field values too.
- **Q13 `WS2-channels`:** which §6 `Channel` value each browser action reports (`press`, `select`, `check`, `hover`).
- **Q14 `WS2-test-browser-visibility`:** may WS2 tests run the browser without a visible window, so it never takes
  foreground during other streams' input tests (03 §4)? Until decided, WS2 holds the input lock.
- **Q15 `WS2-fixture-pages`:** confirm WS4 builds the section 6 pages with the on-page input log, and whether WS2
  may keep throwaway pages in its own test project until then.
- **Q16 `WS2-report-rows`:** propose report-only §15 rows for cold `browser_observe` and browser launch.

## 10. Kickoff prompt for the builder agent

> You are WS2, the browser engine builder for OmniWin. Read `README.md`, `02_Architecture_and_Contracts.md`,
> `03_Build_Orchestration.md`, and `agents/WS2_Browser_Engine.md` in full before coding. The contract wins on conflicts.
>
> Work on branch `ws2-browser` in your own git worktree. Edit only the WS2 paths in section 2 of your brief.
> Never edit the `.sln` or another stream's files; if you need a change there, write a proposal and code to the
> current contract meanwhile.
>
> Build `IBrowserEngine` (contract §7) on your own CDP client over `ClientWebSocket` (§3, §5), a launcher that only
> ever uses the dedicated profile from config (§14), and the injected index that serves `browser_observe`. Every act
> runs resolve → precheck → gate → input → verify, calls `ISafetyGate` itself before any input, and returns an
> accurate `input_sent`. Model-facing text goes through `ResultFormatter`; tool descriptions use `ToolDocs`. Check the
> MCP SDK API against its `samples/` folder before writing `BrowserTools.cs`. Logs go to stderr, never stdout.
>
> First: create `docs/status/WS2.md` from section 7; file proposals Q1, Q3, Q4, Q5, Q14, Q15; send WS4 (through
> WS0) the page list from section 6; spike launch, connect, and tab listing. Build once WS0's M0 projects land.
> Then deliver M1 per section 5, with test doubles for gate, kill switch, and user activity until WS0's are ready.
> Hold `Local\OmniWin.UiInputTestLock` for any test that opens a visible browser window. Record p50/p95 per §15 in
> your status file. Before every merge, `dotnet build -warnaserror` and `dotnet test` must pass. Stop and report
> when M1 is done, or when a blocker cannot be solved inside your files.
