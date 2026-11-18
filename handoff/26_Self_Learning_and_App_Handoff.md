# OmniWin handoff 26: self-learning, decision-model slot, selection tools, and the OmniWin app

Date: 2026-11-15. Written by the builder of rounds 23 to 25 for a new builder AI. **The owner approved this scope on 2026-11-15** ("I approve"). Nothing in it is built yet.

Read first, in this order: `00_START_HERE_New_AI.md`, `02_Architecture_and_Contracts.md` (contract, now 2.5), `25_Mixed_Engines_Builder_Report.md`, then this file. Background and the owner's reasoning: `OmniWin\PLAN_self_learning.md` (it grew during a long conversation and has superseded paragraphs; **where it differs from this file, this file wins**) and `OmniWin\NOTES_openwhispr_handy_code_read.md`.

Words used here: **owner** = the person who owns the PC and the project (not a programmer; write to them in plain language). **Brain** = the AI client that calls OmniWin (Claude, Codex, Antigravity). **Recipe** = a saved workflow plus the request wording it belongs to. **Reviewer** = the reviewing AI session that owns `docs/*` and this folder.

---

## 1. The goal in plain words

1. **Learn once, replay fast.** The first time the brain does a task, OmniWin saves the steps that worked as a recipe. The next time the same task is asked for, OmniWin replays it itself, with every step still checked, and no brain thinking between steps. If something on screen differs, it stops at that step, the brain finishes the task, and the recipe is repaired.
2. **Optional decision model.** If, and only if, the owner wires one, a small "picker" model chooses among learned recipes and built-in quick commands for differently worded requests. Unsure or complicated requests go to the brain.
3. **Selected-text tools.** The brain can read and replace the text the owner has selected, so it can polish or rewrite it with no API key.
4. **One app (the UI).** A desktop app built from a copy of **Handy's** source: dictation with many local speech models, a command box, voice commands, polish-selection by hotkey, and a settings window for all of OmniWin. Every feature is optional and off until the owner turns it on.

Why: OmniWin's own work per step is already fast (click about 65 ms p95, typing 7.75 ms per character, measured in round 25). The waiting is the brain's thinking between steps. A recipe removes that. "About 10 times faster for a repeated 10-step task" is the previous builder's **estimate**; this work must measure it (section 9).

---

## 2. Where the project stands (facts)

- `C:\Users\<you>\Desktop\OmniWin`, .NET 10, **no git**. Contract 2.5. 30 tools by default. Published build in `publish\`; hashes in report 25.
- Last full test round (round 25): Release and Debug 1,437 tests, 0 failed. Live (session Active): 11 of 11 `Contract25LiveTests`, nine tasks 20/20 on B01-B06, B09, B10, clicks-v4, typing-v6/7/8.
- Laya (an offline decision model) was tried and **failed its gate**; it stays off (`router.laya.enabled=false`). Power tools are off. Two engines are bridged (`engine_call`).

Code you will build on (all read by the previous builder on 2026-11-15):

| Piece | File | What it already does |
|---|---|---|
| Run log | `src/OmniWin.Workflows/RunLog.cs`, `src/OmniWin.Core/RunLogContracts.cs` | In memory, 2,000 steps. A step = tool, engine, action, `ElementDescriptor` target (selector path), literal args, a placeholder name (`p1`, `p2`) instead of typed text, the expectation met |
| Recording | `src/OmniWin.Workflows/StepRecorder.cs`, `src/OmniWin.Server/Hosting/RunLogFilter.cs` | Every acting tool call is wrapped; an act is recorded when `confirmed`, or `unverified` with a diff. The result line gets `step=sN` |
| Save | `WorkflowService.Save` in `src/OmniWin.Workflows/WorkflowService.cs` | Saves a step range as JSON under `workflows\`; every step checked against the gate; never replaces a file (`FileMode.CreateNew`) |
| Replay | `WorkflowService.RunWorkflowAsync` | Checks params, re-checks the gate for all steps, resolves each selector fresh, runs through the normal act path, stops at the first step that does not pass, returns a current observation. If step 1's selector matches nothing the result is `stale not_found` with `input_sent=false` |
| Store | `src/OmniWin.Workflows/WorkflowStore.cs` | Names `[A-Za-z0-9][A-Za-z0-9_-]{0,63}`, params `[a-z][a-z0-9_]{0,31}`, max 200 steps, version 1 |
| Router | `src/OmniWin.Server/Router/PhraseRouter.cs`, `RouterService.cs`, `RouterTools.cs` | `omniwin_do(text)`: strict phrase table (12 words max, risky-word list, exactly one rule), else `needs_brain reason=<code> input_sent=false retry=safe`. Has an optional classifier slot `IIntentClassifier` (Laya), registered in `Program.cs` only when enabled and present |
| Tools | `src/OmniWin.Server/Tools/WorkflowTools.cs`, `GuideTopics.cs`, `StatusTools.cs` | `run_steps`, `workflow_save/list/run`, `wait_for`; guide topics; status components (`IComponentStatus`) |
| Config | `src/OmniWin.Safety/OmniWinConfig.cs`, `OmniWinPaths.cs` | Sections incl. `router`, `engines`, `power_tools`; data dir `%LOCALAPPDATA%\OmniWin` or `--data-dir` |
| Audit | `src/OmniWin.Server/Hosting/ToolCallAudit.cs` | Logs safe metadata only; never typed text |
| Input lane | `IInputLane` in `src/OmniWin.Core/DesktopContracts.cs`, `src/OmniWin.Desktop/ActLanes.cs` | One input-sending action at a time **inside one process**; busy gives `blocked rate_limit` |

---

## 3. Standing rules (the owner's; they are not negotiable)

1. **Never delete or replace the owner's files.** Only temp folders a test created itself may be removed. OmniWin's own delete refusal is not weakened.
2. **No commits, no git advice.** The project is local-only.
3. **Ask before every download or install** (state name, source, size). Nothing system-wide, no startup entry, no scheduled task, no system, power, microphone or privacy setting changes, never touch `ForegroundLockTimeout`, never edit any Antigravity settings file, never run `windows-mcp install`.
4. **Never create accounts or API keys, never type secrets.** Those are the owner's to do. Keys are read from an environment variable the owner names.
5. **Real input only through tests or bench commands in the repo.** No scratch scripts that send input (read-only peeks are fine).
6. **Live runs:** run `query user` first; only when the session is `Active` and the owner has said hands-off. One test process at a time. Count `msedge.exe` before and after each run. Stop key is Ctrl+Alt+Shift+K. Never close or change anything of the owner's. Tell the owner when the window is theirs again.
7. **Power tools stay off. Laya stays off.**
8. **Reviewer-owned:** `docs/*`, `bench/*`, `tests/Shared/*`, existing files in `tests/OmniWin.Integration/*`, and this folder except your own report. Add new test files freely; if a format or tool-list change forces an edit to a reviewer file (for example `Harness/Contract.cs`, `SurfaceTests.cs`), make the smallest edit and list it in your report. Append to docs, never rewrite them.
9. **Report honestly:** failures with output, skipped steps as skipped, estimates labelled as estimates.
10. **Work order approved by the owner:** build first with compile-only checks (`dotnet build OmniWin.sln -c Release --nologo -warnaserror`), then **one test round at the end** per part, then fix what it finds.

---

## 4. Decisions the owner has made (final)

| # | Decision |
|---|---|
| 1 | Instant replay with no brain call is allowed for a recipe after 3 clean replays in a row (`learning.auto_run`, default true) |
| 2 | Nothing is learned silently: learning needs the brain to call `task_done(success=true)` |
| 3 | Recipes are plain files under the data dir: `workflows\` and `recipes\` |
| 4 | Browser steps never replay instantly in v1 (the brain may still run such a recipe with `workflow_run`) |
| 5 | Decision-model slot is built now with **no model chosen**; off by default; shadow mode before acting; the owner flips it to act |
| 6 | Hosted services are blocked unless `allow_remote` is set by the owner |
| 7 | The app is built from a copy of **Handy's** source (MIT), renamed; OpenWhispr is only a reference for the selected-text rewrite and the look |
| 8 | Speech model list = Handy's catalog (it already contains every OpenWhispr speech model) |
| 9 | Polish-selection by two routes: hotkey (API key or local model) and brain (new selection tools) |
| 10 | Safety protections (delete refusal, power tools off) are shown locked in the settings UI |
| 11 | Each model download happens only when the owner presses its button or says yes |

Parked (do not build): choosing a particular decision model, a wake word or always-listening voice, starting the app with Windows, learning `engine_call` steps, composing recipes, pruning detours.

---

## 5. Shape of the whole thing

```
owner ── types / speaks / presses a hotkey ──► OmniWin app (fork of Handy, Part D)
                                                  │ starts its own OmniWin.Server.exe and talks MCP over stdio
brain (Claude / Codex / Antigravity) ── MCP ──► OmniWin.Server.exe (its own copy)
                                                  │
        omniwin_do(text):  1 phrase table → 2 learned template or alias → 3 decision model (only if wired, mode=act)
                           → 4 needs_brain + recipe shortlist → brain works → task_done → recipe saved / repaired
        both copies share: config.json, workflows\, recipes\, and ONE machine-wide input lock
```

Four independent parts. **A** is the core and has no dependency on the others. **B** and **C** depend only on the server. **D** depends on A to C being in the published server. Each part ends with its own test round and report, so the owner can stop after any of them.

---

## 6. Part A: the learning core (contract 2.6, in the .NET server)

### A1. Data (new folder `src/OmniWin.Workflows/Learning/`)
- **Workflow file:** unchanged format, in `workflows\`. Versions get new names: `<slug>`, `<slug>-v2`, … Nothing is ever replaced.
- **Recipe card:** `recipes\<workflow name>.recipe.json`, written once with `CreateNew`: `version`, `workflow`, `goal` (the request wording), `template` (may be null), `slots` (slot name to placeholder), `steps`, `learned_wall_ms` (first act to `task_done`, thinking included), `tools` and `processes` touched, `auto_run_eligible` (false when the run was noisy, has a Risky step, a browser step, or a text parameter with no slot), `supersedes` (previous version or null), `learned_at`.
- **Events:** `recipes\events.jsonl`, append-only, one JSON object per line: `learned`, `replay_ok`, `replay_stopped` (with `step`), `repaired`, `promoted`, `demoted`, `disabled`, `enabled`, `superseded`, `alias_added` (with the alias template), `right`, `wrong_pick`. Each has `at`, `recipe`, and where relevant `wall_ms`. **Two server processes may append at the same time:** open with append and write each line in one call, guarded by the machine-wide lock of A6 or a retry loop.
- **Trust is computed, never stored:** `trusted` = the last `promote_after` (default 3) replays were all `replay_ok` with no `replay_stopped`, `wrong_pick` or `disabled` since; otherwise `candidate`. `superseded` and `disabled` recipes are never matched.
- No goal text, template or typed text in the activity log or in `events.jsonl` except the alias template itself in `alias_added` (it lives only in `recipes\`). The audit keeps recipe name and counts.

### A2. Episodes
- One episode at a time per server process, in memory. It **opens** when `omniwin_do` answers `needs_brain` (stores the goal text, the run-log position, a start time). A new `needs_brain` replaces an open episode. It expires after `episode_idle_minutes` (default 10) without an act.
- It counts every acting tool call it sees: recorded steps (hook `StepRecorder.Record`) and acts that ended failed, blocked, denied or unrecorded (hook `RunLogFilter`, where the status line is already parsed; `run_steps` and `workflow_run` report their steps themselves).
- A **repair episode** opens when a recipe replay stops at step k (from `omniwin_do` speed 1 or from `workflow_run`): it remembers the recipe and k.

### A3. New tool `task_done`
`task_done(success, goal?, template?, params?)`. Read-only as far as input goes (`input_sent=false` always).
- `success=false`: closes the episode, learns nothing.
- `goal` is needed only when no episode is open (the brain never called `omniwin_do`); then the steps since the last closed episode (or session start) are used.
- **It learns only when all hold**, else it answers with the reason and the episode stays open so the brain can correct and call again:
  1. `learning.enabled`; 2. at least 2 and at most `episode_max_steps` (60) recorded steps; 3. no risky word in the goal (reuse `PhraseRouter`'s risky-word test); 4. every step passes `WorkflowService.WhyForbidden`; 5. if a `template` is given it must match the normalised goal, contain at least 2 literal words, have no two slots side by side, and `params` must map each used placeholder to exactly one slot and back; 6. the normalised goal must **not** parse as a phrase-table command (those are already instant).
- **Noisy run** (any failed, blocked or unrecorded act inside the episode): still saved, with `auto_run_eligible=false`.
- **An episode that only ran an existing recipe** (`workflow_run`) learns no new recipe; see A5 (alias) instead.
- **Repair episode:** new workflow = steps 1..k-1 of the old recipe + the steps recorded after the stop; saved as the next version, old one gets `superseded`; template and slots carry over.
- **Answer** (proposal; the reviewer fixes the final wording in the contract): first line `task_done status=confirmed input_sent=false recipe=<name> steps=N trust=candidate auto_run=<true|false>`, then one line per slot `slot <name> -> step <n> <tool> on "<control name>"` so a swapped mapping is visible. Refusal: `task_done status=blocked reason=<code> input_sent=false` + `detail:`. Proposed reason words: `learning_off`, `no_episode`, `too_few_steps`, `too_many_steps`, `risky_words`, `policy_forbidden`, `template_mismatch`, `template_conflict`. New reason words are a contract change: list them for the reviewer.
- Build the workflow by reusing `WorkflowService.Save` (it already does the gate check and placeholder renames).

### A4. Matching inside `omniwin_do` (order matters)
1. Phrase table (unchanged).
2. **Learned template or alias (speed 1).** Normalise exactly like the phrase router. Requirements: `learning.auto_run`; at most 12 words; no risky word; **exactly one** trusted, `auto_run_eligible` recipe matches the whole sentence; each slot value is 1 to 60 characters of `[a-z0-9 ._+-]` (note: the value keeps the casing and characters of the **original** request text at the same position where possible; decide and test this, because file names are case- and punctuation-sensitive). Run `RunWorkflowAsync`; append the event with wall time. Result: the normal `workflow_run` text plus `routed rule=recipe.<name>`. If the run stops at step 1 with `input_sent=false`, answer `needs_brain reason=recipe_not_ready` instead. If it stops later, return the stop as it is (`retry=check_state` rules apply) and open a repair episode.
3. Decision model (Part B), only when wired and `mode=act`.
4. `needs_brain`, now with up to 3 extra lines `recipe: <name>(<params>) steps=N trust=<…> goal="<goal>"`, chosen by plain word overlap between the request and each recipe's goal, template and aliases (no model). The existing hint line stays.

### A5. Replay bookkeeping, trust, aliases
- Wrap `workflow_run` and speed-1 runs: `replay_ok` when the run outcome is `confirmed` or every step passed; `replay_stopped step=k` otherwise. Promotion and demotion follow from A1.
- **Alias learned for free:** when the brain runs a recipe with `workflow_run` inside an episode, the run is `replay_ok`, the recipe is `auto_run_eligible`, the goal has no risky word, and every parameter value appears exactly once, word for word, in the goal: store `alias_added` with those values turned into `{slot}`. Max 10 aliases per recipe.

### A6. Machine-wide input lock (needed before Part D, build it here)
Two server processes (the brain's and the app's) must never send input at the same moment. Add a session-wide named lock taken wherever the in-process lane is taken (`IInputLane` users: acts, typing, engine calls). Use a named **semaphore** (`Local\OmniWin.InputLane`, count 1), not a mutex, because holders are async and change threads. Busy from another process gives the same `blocked rate_limit input_sent=false retry=wait` as today, with a detail naming "another OmniWin process". It must be released on every path, including the stop key and a crash (a crashed holder releases a semaphore only if the handle closes: verify, and add a stale-holder timeout if needed).

### A7. Second new tool `workflow_manage`
`workflow_manage(name, action)` with `disable`, `enable`, `right`, `wrong_pick`. Appends an event; never edits or deletes a file. `right` counts like a clean replay confirmation from the owner; `wrong_pick` demotes the recipe and, if a decision model made the pick, drops the model to shadow (Part B).

### A8. Surface changes
- `workflow_list`: add `kind=learned|manual`, `trust`, `runs`, `last`, `saved_ms` (learned wall time minus median replay wall time; say "estimate" in the guide).
- `omniwin_status`: recipes total, trusted, speed-1 hits, stops; decision-model state.
- `omniwin_guide`: new topic `recipes` that tells the brain: call `omniwin_do` first with the owner's words; when it answers `needs_brain`, do the task; if a `recipe:` line fits, run it with `workflow_run`; at the end call `task_done`. Keep every description short: each tool adds words to every brain request.
- Config section `learning`: `enabled` true, `auto_run` true, `promote_after` 3, `max_recipes` 200, `episode_max_steps` 60, `episode_idle_minutes` 10, with validation.
- Contract number 2.6 in `OmniWinContract.cs`; append a "Contract 2.6" section to `02_Architecture_and_Contracts.md`.

### A9. Not learnable in v1
`engine_call` steps (never `confirmed`), power tools, anything the gate denies, browser steps at speed 1.

---

## 7. Part B: the optional decision-model slot (server)

- **Interface** (new, beside `IIntentClassifier`): request text + a list of options `{id, label, examples[]}` in, ranked `{id, confidence}` + latency out. Options are built by OmniWin: every trusted, eligible recipe (goal, template, aliases), the built-in intents (`PhraseRouter.Intents`), and `none`.
- **Config `decision`:** `provider` `none`, `mode` `off|shadow|act` (default off), `threshold` 0.95, `margin` 0.30, `max_latency_ms` 400, `timeout_ms` 1500, `max_words` 20, `endpoint`, `api_key_env`, `allow_remote` false. With `provider=none` nothing is constructed, started or connected (test this).
- **Acts only when all hold:** `mode=act`; top pick is a trusted eligible recipe or a built-in intent; confidence ≥ threshold; lead over second ≥ margin; latency ≤ max; no risky word; ≤ `max_words`; every slot value found **word for word** in the request (the model never supplies a value; for built-ins reuse `PhraseRouter.FromIntent`). Then the same run path as A4 step 2.
- **Everything else goes to the brain** with `needs_brain` (reasons such as `low_confidence`, `no_slots`, `model_unavailable`), and the model's top 3 become the `recipe:` shortlist. A slow, failed or unreachable model costs at most `timeout_ms` and never fails the request.
- **Shadow mode:** the model is asked on requests that reach step 4; nothing is run from its answer. When the episode closes, compare its confident pick with what happened: the recipe the brain ran with `workflow_run`, or "none" otherwise. Keep counts only (pick id, confidence, latency, right/wrong) in `recipes\decision-events.jsonl`; **no request wording**. `omniwin_status` shows them. OmniWin never switches the mode itself; `wrong_pick` sets an override back to shadow that only the owner's config edit clears. State in the guide that N clean picks only bound the error rate loosely (50 clean picks ≈ "below about 6%").
- **What may be sent to a model:** the normalised request and the option labels. Never screen content, window titles, paths, typed text or clipboard. A non-loopback `endpoint` is refused unless `allow_remote`; the key comes from the env var named in `api_key_env`; the audit logs provider, outcome and latency only.
- **Adapters:** build the slot and all logic against a **scripted stand-in** (tests). Do **not** write a real adapter until the owner names a model and you have read its docs (price, privacy, request format). Candidates discussed, all hosted and all unverified by us: Celeris-1.1 Decision, Elephant, Jev 1.14, Mammoth-1, GPT-6 Luna Decisions (figures from a third-party copy of the Artificial Analysis Decision Index, 2026-11-12: about 60 to 280 ms and $14 to $340 per million decisions). An offline `bench decision-eval` command over the existing 793 labelled commands (`tools\laya\eval_set.jsonl`, `eval_hard.jsonl`) belongs with the first real adapter.

---

## 8. Part C: selection tools (server)

- `selection_get(window?)`: the selected text of the focused control in the foreground (or named) window. Read through UI Automation's text pattern; fallback copy with the clipboard saved and restored (the Win32 save/restore from round 23 exists). Returns the untrusted-content header, the text (cap it; say when cut), and a token `sel=<id>` that identifies this selection (window, element, text hash). Password fields, secure desktop and protected processes are refused. Read-only: `input_sent=false`, except the clipboard fallback, which sends Ctrl+C and must say `input_sent=true`.
- `selection_replace(sel, text)`: only if the same selection is still there (same idea as ref + generation; otherwise `stale`). Goes through the **same safety gate as typing** (so a typed delete command into a terminal is still refused), uses the checked typing path or paste, then reads back: `confirmed` only when the new text is where the selection was, else `unverified` with `retry=check_state`. Never logs the text.
- `polish_prompts()`: lists the owner's named instructions from `prompts.json` in the data dir (id, name, text), so the brain can apply one by name. The app (Part D) edits the same file.
- Guide topic `selection`. Tool count becomes **35** by default (30 + `task_done`, `workflow_manage`, `selection_get`, `selection_replace`, `polish_prompts`). Update `tests/OmniWin.Integration/Harness/Contract.cs` and the surface tests accordingly.
- Open point to check, not to assume: MCP "sampling" (a tool asking the client's own model) would let a hotkey borrow the brain. Client support is uneven and unverified for Claude, Codex and Antigravity. Check, report, and wire it only where it really works.

---

## 9. Part D: the OmniWin app (from Handy's source)

**Source:** `https://github.com/cjpais/Handy`, MIT, copyright "CJ Pais"; commit read: `aca8985` (2026-11-14), version 0.10.1, Tauri 2 (Rust) + React/TypeScript, about 63,000 lines. Keep its `LICENSE` text in the copy and add a `NOTICE` saying what was changed. **Its name, logo and brand assets are not open: rename and replace them** (`productName`, `identifier` `com.pais.handy`, icons, sponsor images). Remove its updater endpoint and public key (`src-tauri/tauri.conf.json` points at Handy's GitHub releases).

**Before anything else (ask the owner, one item at a time):** the source copy into `OmniWin\app\`; and the build tools Handy's `BUILD.md` requires on Windows: Microsoft C++ Build Tools, CMake on `PATH`, the Vulkan SDK, Rust, Bun (or Node). **Most of these are system-wide installs, which standing rule 3 forbids without the owner's explicit yes.** Check what is already installed first (read-only). If the owner declines, Part D stops here and Parts A to C still stand. Then write a short keep / change / add list per folder and get the owner's OK before editing.

**Keep (already in Handy):** dictation with hold or toggle shortcuts (`src-tauri/src/shortcut/`, `actions.rs`); the 76-entry model catalog with pinned revisions, SHA-256 and licences (`src-tauri/src/catalog/catalog.json`; some entries are non-commercial, keep that label visible); model download on demand; paste methods (`clipboard.rs`, `paste_tx/windows.rs`); history; "transcribe with post-processing" with named prompts (`settings.rs`, `default_post_process_prompts`) and providers OpenAI, Z.ai, OpenRouter, Anthropic, Groq, Cerebras, Bedrock, custom endpoint (`llm_client.rs`); the command line (`cli.rs`); the secure-input check (`secure_input.rs`).

**Add:**
1. **Child server.** The app starts `OmniWin.Server.exe` (path from settings, default the sibling `publish\` folder) with the normal data dir and talks MCP over stdio (JSON-RPC lines: `initialize`, `notifications/initialized`, `tools/call`). It restarts it after a settings save and ends it on exit. All OmniWin logic stays in the server; the app re-implements none of it.
2. **Command box** (new shortcut binding): a small window with a text field. Enter sends the text to `omniwin_do`. Shows the status line in plain words; for `needs_brain` shows "needs your AI" with a **Copy** button (the app has no brain of its own). After a recipe or model pick shows **Right / Wrong**, which call `workflow_manage`.
3. **Voice command** (new binding): Handy's recording and transcription, but the text goes to the command box, is shown for `confirm_delay_ms` (default 1500) with Cancel, then runs. A risky word never auto-runs: it waits for Enter.
4. **Polish selection** (new binding): `selection_get` through the child server, send text + chosen prompt to the configured provider with Handy's `llm_client`, `selection_replace` with the answer, keep the original for one-key undo. Hold-and-speak variant: the spoken sentence is the instruction. Prompts live in the data dir's `prompts.json` (shared with `polish_prompts`); move Handy's prompt editor onto that file.
5. **OmniWin settings section:** edits `config.json` for Learning, Decision model, Router, Engines, Hotkeys. Do not duplicate validation: add a `OmniWin.Server.exe --validate-config <file>` mode that prints errors as JSON and use it; write atomically (temp file + rename in the same folder is a *new-file then replace* of OmniWin's own config, which is allowed; never touch other files). Safety settings (delete refusal, `power_tools`) are displayed read-only. `allow_remote` switches carry a one-line warning. "Test" buttons for the decision model and the polish provider.
6. **Protection:** add the app's process name to the server's default protected-process list (see `process_blocklist_extra` in contract §13.1), so the brain cannot use OmniWin to click or type in the app's windows.

**Hotkeys:** proposals only: command box, voice command, polish selection each get their own binding, all re-mappable and all **unbound or off until the owner enables them**. Read Handy's Windows defaults in `settings.rs` first and avoid collisions; never allow Ctrl+Alt+Shift+K.

**Not in the app:** accounts, cloud, telemetry (Handy has none; keep it that way), auto-start with Windows, auto-update.

**Verify, do not assume:** whether Handy's transcription hook (`transcription_hook.rs`, a program at `<app data>/hooks/transcription`) runs on Windows; how dictation behaves over Remote Desktop without a redirected microphone; CPU speed of each model on this PC (Intel Core Ultra 5 125H, no discrete GPU).

---

## 10. Build order and "done" for each part

| Part | Phases | Done when |
|---|---|---|
| A | data and trust → episodes and `task_done` → matching in `omniwin_do` → replay events, repair, aliases → input lock → `workflow_manage`, list/status/guide, config, contract 2.6 | section 11 A passes; published; report written |
| B | interface, config, act/shadow logic, counts, status, with the stand-in | section 11 B passes |
| C | three selection tools, guide, contract entries | section 11 C passes |
| D | owner approvals → keep/change/add list → rename and strip → child server → command box → voice command → polish → settings → protection | section 11 D passes; the owner tries it |

Publish only after a part's test round is green: `dotnet publish` as in `docs/INSTALL.md`, then record SHA-256 of the published files in the report (each DLL must equal its Release build output).

---

## 11. The test round (gates, no hoping)

Commands and conditions: `docs/RUNNING_TESTS.md`. Results with `--results-directory artifacts/test-results/<date>-<name> --report-xunit-trx`; never `--no-build` for a quoted run.

**A (unit, with the existing fakes in `tests/OmniWin.Core.Tests/WorkflowFakes.cs` and `tests/OmniWin.Router.Tests`):** template matching on a labelled set of at least 200 phrases with **0 wrong-recipe runs**; near-misses, two matching recipes (`ambiguous`), risky words, over-long requests, slot limits; templates that would match anything are refused; a goal that is a phrase-table command is refused; episode rules (clean, noisy, too few, too many, failed act inside, expiry); trust promotion and demotion from events; repair keeps the prefix, replaces the suffix, leaves the old files byte-identical; alias learning and its limits; nothing rewritten or deleted (hash the folder before and after); no goal or typed text in the audit log; config switches; the input lock across **two real processes**.
**A (live, F01 fixture, session Active):** learn "type a name, tick Agree, press Apply" by doing it once through the tools, then `task_done`; replay confirms and the fixture's own log shows exactly one Apply click per run; trusted after 3; speed 1 from the phrase with a different name; **drift:** press the fixture's "Rename target" button (Apply becomes "Renamed apply"), replay must stop at that step with nothing else clicked, then repair gives `-v2` that replays clean. Gates: 20 of 20 trusted replays confirmed; wrong target 0; no leftover windows or processes; Edge count unchanged.
**A (numbers to report):** first-run wall time against replay wall time (label any stand-in for brain thinking as a stand-in); per-step overhead against the existing 20 ms budget; all old suites still green in Release and Debug.
**B:** off = zero calls, no process, no socket; each "goes to the brain" case; acts only inside the rules; shadow never runs anything and counts correctly; `wrong_pick` forces shadow; non-loopback endpoint refused without `allow_remote`; the captured payload contains only request wording and option labels.
**C:** read and replace on the F01 editor and one real app the tests open themselves (Notepad, as the typing benchmark does); stale selection refused; password field refused; a delete command into a terminal still denied; clipboard restored after the fallback; text never logged.
**D:** the app's own tests still pass (`bun`/`cargo` as in Handy's docs); command box round trip; Right / Wrong reach the events file; two servers never type at once; the brain's tools refuse the app's windows; settings reject invalid values with a readable message and round-trip without losing fields; voice command with a recorded WAV through Handy's `--transcribe-file` path; polish with a stand-in provider; no network connection in local mode (capture and check).

---

## 12. Known risks and open points (say them to the owner as they come up)

- Learning depends on the brain calling `omniwin_do` first and `task_done` last. A client that ignores the guide learns nothing. The command box does not fix this by itself, because it has no brain.
- **Poisoned learning:** a web page could trick the brain into steps that then get saved. Defences in this design: the gate on every step, 3 clean replays before instant runs, no browser or Risky steps at speed 1, plain readable files. A residual risk remains; do not describe it as solved.
- Slot values and casing (A4) need a careful decision and tests; file names are the common case.
- Replay does not re-apply the original `expect` conditions (`WorkflowStep` has none); a step passes on `confirmed` or `unverified` with a diff. Consider storing the met expectation and waiting on it; if you change the workflow file format, bump its version and keep version 1 readable.
- The app adds a second toolchain (Rust/Tauri) and its build tools are system-wide installs.
- Decision-model and speech-model quality on the owner's own requests and voice are unknown until measured.
- The read-only clones used for the code read were in a temp folder and may be gone; the commits are named above.

---

## 13. Your report

Save `26_Builder_Report.md` here (one section per part), then message the reviewer session and tell the owner in plain words. Include: what was built, every file of the reviewer's you had to touch, new reason words and tool descriptions for the contract, test counts with `.trx` paths, the measured speed-up, hashes of `publish\`, everything not run or not verified, and every download or install you asked for and the owner's answer.
