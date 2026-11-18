# OmniWin: mixed-engine plan (OmniWin + Windows-MCP + agent-desktop)

Status: **APPROVED 2026-11-13 and BUILT (P1 to P5, P7 stage A, the engine bridge and the Laya side that needs no PyTorch). The test round ran (Release and Debug 1,437 tests, 0 failed). Laya: PyTorch approved and installed 2026-11-14, offline gate run and FAILED (61% accuracy, wrong actions at every threshold, 827 ms): Laya stays off. `notify` dropped (see below). Live checks run 2026-11-14 with the owner connected: 11 of 11 new live tests pass (hover, drag, labelled view, `omniwin_do`, one acting call through each engine), nine tasks 20/20 on B01-B06, B09, B10 (B07 not applicable over Remote Desktop), clicks-v4, typing-v6/7/8 confirmed and read back exactly; details in `OmniWin_Handoff\25_Mixed_Engines_Builder_Report.md`.** Nothing was tested during the build, by the owner's instruction.

## 0. Goal and what the owner decided
- One OmniWin server that a model (Claude, Gemini, Antigravity) uses for everything: OmniWin's checked desktop control, plus the extra reach of Windows-MCP and the good ideas of agent-desktop.
- **Owner decisions (2026-11-13):** download and bridge **both** engines (Windows-MCP and agent-desktop); **power tools OFF**; **add Laya** to speed up simple commands.
- The owner accepts the risk of the third-party engines. Because the power tools stay OFF, OmniWin's delete refusal is **not changed**, and the shipped and personal configs both keep `power_tools.enabled = false` and `allow_delete = false`.
- Work order: plan, approval, then build everything without running tests, then one test round at the end, then fix what it finds.
- Constraints that stay (they cost nothing and are not safety limits): the stop key Ctrl+Alt+Shift+K, the activity log, no changes to Antigravity's files, no changes to system or power settings, nothing run as a scheduled task or startup entry, nothing installed system-wide.

## 1. What each project brings (from the READMEs and, for computah, its code; read, not run)
| | OmniWin (have) | agent-desktop (Rust, Vercel Labs) | Windows-MCP (Python) |
|---|---|---|---|
| Unique strengths | exact `input_sent`, checked results, delete refusal, tested live on this PC, OCR and look-only view, saved workflows, browser tools | version-matched "skills" guides for the model, a "retry is safe or not" flag on every error, drag, hover, batch, snapshot-scoped refs, headless-by-default actions | PowerShell, Registry, FileSystem, Process, Notification, Scrape, screenshot-with-element-ids, a very large user base |
| Cost | none | download a Rust binary | Python 3.14+, `uv`, many packages; telemetry on by default (turn off) |

## 2. Design: three layers, one front door

```
model (Claude / Gemini / Antigravity)
        │  MCP over stdio
        ▼
   OmniWin.Server  ── native tools (26 today)  ── checked results with `retry=`
        │         ├─ NEW native "power" tools (shell, registry, files, process) .... switch: power_tools
        │         ├─ NEW guide tool (version-matched skills) ........................ always on
        │         ├─ NEW drag, labelled screenshot, notify .......................... native
        │         └─ NEW engine bridge (engine_call, engines_list) .................. switch: engines
        ▼                                   │ stdio MCP client, one child per engine
  Windows, UIA, Win32                       ├─ windows-mcp   (pinned, telemetry off)
                                            └─ agent-desktop (pinned, `agent-desktop mcp`)
```

Key decisions (my recommendations; the owner can override each):
1. **Rebuild the best parts natively in .NET; bridge the rest.** PowerShell, registry, files and process control are small in .NET and need no Python, no telemetry and no new download. Windows-MCP and agent-desktop are then reached through one generic bridge for everything else, and they serve as the speed and accuracy baselines.
2. **One generic bridge, not two special cases.** Both projects already speak stdio MCP, so OmniWin gets one MCP client with an `engines.json` registry (command, args, env, pinned version, enabled flag, timeout). Adding a third engine later is a config line.
3. **The brain chooses the engine explicitly.** No automatic fallback that could send the same click twice. The guide tells the model when to use which. A bridged call holds OmniWin's input lane for its duration, so OmniWin and an engine never drive the mouse at once, and the stop key kills the engine's process tree.
4. **Bridged results never claim `confirmed`.** OmniWin cannot check another engine's work, so they come back `unverified` with `engine=<name>` and `retry=observe_first`. Native tools keep their exact guarantees.
5. **Power tools OFF (owner decision).** `power_tools.enabled = false` everywhere. The switch exists so it can be turned on in one line later.
6. **OFF must really mean off, also through the bridge.** Windows-MCP ships its own `PowerShell`, `Registry`, `FileSystem` and `Process` tools, and bridging it unfiltered would hand them over. So the bridge has a per-engine tool filter (`exclude_tools`, or `allow_tools` as an alternative) applied twice: OmniWin passes Windows-MCP its own `--exclude-tools` option, and OmniWin refuses to forward any tool the filter does not allow. The default filter excludes `PowerShell`, `Registry`, `FileSystem`, `Process` for Windows-MCP, and the equivalent shell, file and registry commands for agent-desktop. Turning `power_tools` on removes those entries.

## 3. Work packages (in build order)

### P1. `retry=` flag on every result (contract 2.5)
- One central rule in `ResultFormatter`, derived from status, reason, `input_sent` and whether the action can be repeated safely. Values: `safe` (nothing was sent, or the action is repeatable), `observe_first` (stale, window changed: read again, then retry), `check_state` (input was sent but not confirmed and the action is a toggle or a click: check the screen before retrying), `wait` (blocked by user activity or a running type), `no` (denied, kill switch, policy).
- Appears on the first result line; the status documents list it.
- Files: `src/OmniWin.Core` (formatter), `docs` and the contract (version 2.5).

### P2. Version-matched skills guide
- A new always-on tool `omniwin_guide(topic)` plus the same text as MCP resources. Topics: `start` (the observe, act, verify loop), `desktop`, `browser`, `system`, `workflows`, `recovery` (every status with the right next step), `engines` (when to use Windows-MCP or agent-desktop), `power` (when the power tools exist).
- Text is generated from the real tool definitions and the contract version at build time, so it can never describe a different version than the server. Each topic starts with `omniwin <version> contract <version>`.

### P3. Native additions (no downloads)
- **drag:** `desktop_act action=drag` from one ref to another, as one atomic input batch (down, move, up), with the same hit tests as click on both ends. A stuck button is impossible by design, because agent-desktop itself refuses split mouse-down/up until it can release them.
- **hover** over a ref (pointer move only, same checks).
- **Labelled screenshot:** `screen_view labels=true` draws the current observe refs on the picture, so a vision model can pick by ref; password fields stay masked.
- **notify:** show a toast.
- **power tools** (built, but **off**; only when `power_tools.enabled`): `shell` (PowerShell with timeout and output cap), `registry` (read, write, list), `files` (list, read, write, move, copy, delete), `process` (list, stop). Each returns the same checked envelope, logs a summary, and honours the stop key. Delete is allowed only when `power_tools.allow_delete` is true.

### P4. Engine bridge (needs downloads, see section 4)
- `engines.json`, an MCP client over stdio, the `engine_call(engine, tool, args)` and `engines_list` tools, lane locking, stop-key kill of the child tree, result wrapping, timeouts, audit log entries.
- First run of each engine happens inside its own folder `OmniWin\tools\<engine>\`; the bridge uses absolute paths, no PATH or registry change.

### P7. Fast router: `omniwin_do(text)`, with Laya as an optional second stage
Why: for a simple spoken or typed command the time goes to the brain model (seconds), not to OmniWin (about 50 ms). A local router answers the simple ones itself and hands everything else to the brain.
Honest scope (from the Laya sources, not yet verified on this PC): Laya is a 421M classifier that picks among typed choices and gives a confidence; it writes no text, does not extract numbers, needs fine-tuning (zero-shot it scores near chance), and its 30 ms figure is the vendor's. So it is built in two stages and **Laya must earn its place by a measured gate**.
- **Stage A (no download): phrase router inside `OmniWin.Server`.** A table of command patterns with numeric and name slots (pause, play, next, volume up/down/to N, mute, unmute, switch to <app>, open <app>, minimize, maximize, close window, click <named control>, type <text>, run saved workflow <name>) mapped to existing native tools. `omniwin_do(text)` runs a match **only** if exactly one pattern matches, and reports `confirmed` only if the tool did; otherwise it returns `needs_brain` with the transcript, what was tried and every result including `retry=`. A toggle is never retried automatically.
- **Stage B (downloads, section 4): Laya as the matcher for phrasing the table misses.**
  - Runs as a **separate child process** (`tools\laya`, Python 3.14 from `uv`, ONNX Runtime) that OmniWin starts on demand and talks to over stdio JSON lines. A model crash or hang cannot touch input handling; the stop key kills it.
  - Laya only chooses the **intent** from the fixed list above, with a confidence. Numbers and names come from rules; names are matched against `windows_list`, `apps_list` and the saved workflow list, never invented.
  - **Training data:** the owner's own activity log (arguments are already redacted), plus paraphrases I write for each intent, plus "none of these" examples (open-ended requests, questions, risky wording) so it learns to refuse. Fine-tuning is first tried head-only and on CPU; if that is too slow I stop and report what it needs (for example a GPU) rather than guessing.
  - **Offline gate before any integration** (no input is sent during it): on a held-out list of at least 300 commands, **wrong-action rate at the chosen confidence threshold <= 0.5%**, intent accuracy at least 95% on the commands it accepts, and **median time <= 50 ms on this PC**. The threshold is calibrated on the held-out list, not fixed at 0.80. If Laya fails the gate it is **not integrated**, Stage A stays, and the result is reported with the numbers.
  - Only if the gate passes is Laya wired into `omniwin_do`, behind `router.laya.enabled`.
- **Brain hand-off without API keys:** `omniwin_do` is an ordinary MCP tool, so the owner's own agent (Antigravity, Claude, Gemini CLI) is the brain. It calls `omniwin_do` first and continues itself on `needs_brain`. Calling a cloud model from inside OmniWin would need keys and is out of scope.
- **Front end: DEFERRED (owner decision 2026-11-13), kept in the plan, activated only if needed later.** `omniwin_do` is an ordinary tool, so for now the owner's own AI app calls it and no new window or listener is built. When wanted, the options are: (a) a text hotkey box (a global shortcut opens a one-line box; Enter sends the text to `omniwin_do`; needs a small background program, a configurable shortcut, and shows the result or hands the text to the AI app), or (b) voice (wake word, transcription, spoken reply; its own downloads, section 6). Neither adds work to P1 to P5 or P7 now.

### P5. Settings, docs, guide text
- New config sections (`power_tools`, `engines` with per-engine tool filters, `router`), README and contract update, guide topics filled in (including a `router` topic: when `omniwin_do` helps and what `needs_brain` means), the plan file marked done.

### P6. End-of-project test round (after the owner says "build is done")
1. Build Debug and Release with warnings as errors.
2. Full suite, Release then Debug, one at a time, Edge counted before and after.
3. Live (session Active, hands off): the nine tasks, clicks, typing, plus new live checks for drag, labelled screenshot, power tools, both bridges.
4. **Router evaluation** (P7): accuracy and speed of the phrase router and, if it passed the offline gate, of Laya on a held-out list of spoken commands.
5. **Three-way comparison**: the same tasks on OmniWin native, Windows-MCP and agent-desktop, at least 3 runs each, measured by the same independent checks; written to `artifacts/benchmarks/` and a report. Only then do we say who is faster or more accurate.
6. Fix what it finds, rerun that part.

## 4. Downloads (named, nothing else; all into `OmniWin\tools\`, no system-wide install, no PATH change)
| What | From | Why | Size (to confirm on the page before each download) |
|---|---|---|---|
| `uv` (single executable) | github.com/astral-sh/uv releases | runs Windows-MCP in an isolated environment | about 15 to 25 MB |
| Python 3.14 | fetched by `uv python install` from astral-sh/python-build-standalone | Windows-MCP requires 3.14+; the PC has 3.12 | about 30 to 50 MB |
| Windows-MCP, pinned version | PyPI (`windows-mcp`) with its dependencies | the bridge and the baseline | tens of MB plus dependencies (the old estimate for everything Python is 100 to 150 MB) |
| agent-desktop, pinned release | GitHub Releases (Windows x64 archive; it needs its helper kept beside the executable) | the bridge and the baseline | to confirm; Rust binary, likely small |
| **Laya** weights (`convaiinnovations/laya`, Apache-2.0; the typed-decisions variant if the model card says it is the better base) | Hugging Face | Stage B router | about 1 GB (sources); to confirm on the model card |
| ONNX Runtime, `tokenizers`, `numpy` (and `onnx` only if the export needs it), into the same isolated Python | PyPI | run Laya without PyTorch | tens of MB |
| **Only if the ONNX export fails or is too slow:** PyTorch and `transformers` for the export or the fine-tune | PyPI | export and fine-tune | large (hundreds of MB to a few GB); **I will stop and ask before this one** |
- Before each download I will state the exact file, size and version in the log, and record the SHA-256 that I computed (and the published digest where the page gives one).
- Telemetry: `ANONYMIZED_TELEMETRY=false` is set in the engine's environment from the first start.
- Risk I cannot remove: the packages come from third parties and I will not audit all of their code. The owner accepted this.

## 5. Unknowns and risks (so they are not a surprise)
- **Windows-MCP's consent countdown.** Its default "relaxed" policy asks the user to consent in a 10-second countdown before the AI takes control. If this cannot be configured off, the bridge will wait on it. I will read the downloaded project's own documentation for the setting, and report if there is none.
- **Two engines on one desktop.** The lane lock prevents overlap inside OmniWin, but if a model calls an engine outside OmniWin (a separately configured client), nothing can stop clashes. Use the bridge.
- **Contract change.** The `retry=` field and the new tools are contract 2.5, which the reviewer session's docs must follow. The delete refusal changes to a switch; this is the owner's decision, and the audit log still records every call.
- **Tool count.** Defaults add 2 tools (`omniwin_guide`, `engines_list`). The 5 more (`shell`, `registry`, `files`, `process`, `engine_call`) appear only when enabled, so the model is not buried in tool definitions.
- **Laya may not pass the gate.** Zero-shot it is near chance, and exporting a model with a custom decision head to ONNX is not guaranteed. The plan then ends at Stage A, which still gives instant answers for the common commands. The cost of finding out is the Laya download and some CPU time.
- **Fine-tuning compute is unknown.** If fine-tuning a 421M model on CPU is impractical I stop and ask.
- **Cannot verify Windows-MCP's or agent-desktop's claims** about speed or safety until the final comparison.
- This environment's own permission rules may refuse some commands during the build (for example anything that looks like persistence). I will not work around such a refusal; I will stop and say so.

## 6. Out of scope for now
- **Voice** (computah-style wake word, speech-to-text, text-to-speech): a separate decision, later. Its parts would be openWakeWord, faster-whisper and Piper (more downloads) and a Windows voice loop written fresh; computah's own bridge is not reusable.
- Calling a cloud model from inside OmniWin (needs API keys).
- The Windows-MCP-style `Scrape` tool: OmniWin's browser tools already cover pages.
- Mac and Linux.

## 7. Decisions (owner, 2026-11-13) and what is still open
Settled:
1. **Downloads:** Windows-MCP and agent-desktop (with `uv` and Python 3.14 for the first) **approved**.
2. **Engines to bridge:** **both**.
3. **Power tools:** **OFF** (built, switchable). Delete refusal unchanged.
4. **Laya:** **added** as P7, behind the offline gate.
5. Stop key and activity log stay.

Still needed with the final approval:
- **Laya downloads (section 4, the Laya rows):** the weights (about 1 GB) and the small Python packages. The PyTorch row is asked about separately only if it turns out to be needed.
- **Front end for P7:** deferred, kept in the plan; nothing is built or downloaded for it now. Activate later by telling me which option (text hotkey box or voice).
- **Reviewer:** after the build, the reviewer session is told about contract 2.5 and the new tools so it updates `docs/*`.

Nothing starts until the owner replies **"approve"**.
