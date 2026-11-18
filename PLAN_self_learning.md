# OmniWin: self-learning plan ("do it once, then replay it fast")

Status: **PROPOSED 2026-11-15, updated the same day to add the optional decision model (section 3.7) and the hotkey box, optional voice and settings window (section 3.8). Nothing built. Waiting for the owner's approval.** Contract would become 2.6 (additive: two new tools, two new config sections, nothing removed). Which decision model to wire is parked (section 9); the slot for it is in the plan.

## 0. In plain words

**What it does.** The first time the AI does a task on your PC, OmniWin watches the steps that worked and, when the AI says "done, it worked", saves them as a *recipe*. The next time you ask for the same thing, OmniWin replays the recipe itself: no thinking between steps, every step still checked as before. If anything looks different (a button moved, a window is missing), it stops at that step, nothing wrong gets clicked, and the AI takes over and *repairs* the recipe.

**Why it is faster.** OmniWin's own work per step is already tiny (about 0.1 to 0.3 s). The slow part is the AI thinking between steps (seconds each, my estimate). A recipe removes that thinking. Rough picture for a 10-step task: first time about 30 s, second time about 2 s. That is my estimate; the plan measures it for real from your own first run versus the replay (section 6).

**It is not model training.** Nothing is downloaded, no GPU, no weights. It is a notebook of steps that worked, kept as plain files you can read. (A decision model, if you ever wire one, is a separate optional piece: section 3.7.)

**Three speeds on the second time:**
| Speed | When | What happens |
|---|---|---|
| 1. Instant | You ask in words OmniWin has learned, with a trusted recipe ("open invoice 42 in word") | OmniWin runs it, no AI call at all |
| 1b. Instant, any wording | **Only if you wire a decision model** (section 3.7) and switch it to act | The model picks a learned recipe or quick command from the list OmniWin gives it (well under a second); OmniWin runs it. Anything unsure or complicated goes to the AI |
| 2. One AI step | The wording differs, or the recipe is not trusted yet | OmniWin shows the AI the best-matching recipes; the AI picks one with one call and runs it (one thinking step instead of ten) |
| 3. Normal | Nothing matches | The AI does it step by step as today, and it is learned for next time |

**What you must know up front.**
- OmniWin never sees your sentence or the final result on its own, because the AI sits between you and OmniWin. So the AI has to tell OmniWin the goal (it already calls `omniwin_do` first) and say `task_done` at the end. Two tiny calls, only on a first run. Until the hotkey box exists, learning depends on the AI client following that instruction.
- It only helps for tasks that repeat in the same way. A task that needs judgment each time ("pick the best invoice") still needs the AI for the judging step.
- The **decision model is optional and off**. Without one, everything above works with plain word matching and the AI. With one wired, it only widens speed 1 to differently worded requests, and the AI still handles anything the model is not sure about.
- The **hotkey box, voice and settings window** (section 3.8) are optional extras on top. The box lets you type (or, if you wire a speech model, speak) a request straight to OmniWin without opening an AI, runs what OmniWin already knows, and gives you a direct "that was right / wrong" button, which is the safest signal for trust.

## 1. What already exists (reused, not rebuilt)

| Piece | Where | What it gives us |
|---|---|---|
| Run log | `OmniWin.Workflows/RunLog.cs`, `StepRecorder.cs`, `Hosting/RunLogFilter.cs` | Every act that ended `confirmed` (or `unverified` with a visible change) is already recorded, with the element's selector, literal arguments, and a placeholder for typed text (typed text is never stored) |
| Save | `WorkflowService.Save` | Saves a range of steps as a JSON workflow after checking every step against the safety gate; never replaces a file |
| Replay | `WorkflowService.RunWorkflowAsync` | Finds each element again from its saved selector, re-checks the gate, runs through the normal act path, stops at the first step that fails and returns a current observation. Per-step overhead budget 20 ms |
| Phrase router | `Server/Router/PhraseRouter.cs`, `RouterService.cs` | Strict, safe matching of short requests, a risky-word list, a `needs_brain` reply, and a "run workflow X" rule |
| Safety | `ISafetyGate`, `DeletePolicy` | Deleting stays refused everywhere, including on replay |
| Audit | `Hosting/ToolCallAudit.cs` | Logs only safe metadata (never typed text) |

## 2. What is missing

1. Saving is manual: the AI must remember to call `workflow_save` with step ids. Nobody does that reliably.
2. Nothing remembers *what request* a workflow was for, so a later request cannot find it.
3. No way to fill the typed-text placeholders from the new request.
4. No memory of how well a workflow worked (runs, failures, time saved), so nothing can be trusted automatically.
5. No repair: a failed replay is just a failure.
6. The run log is in memory only (2,000 steps per session): fine for the first run, but the learning has to be stored.

## 3. Design

```
you ask ──► AI calls omniwin_do("<your words>")
              │
              ├─ trusted recipe matches the words exactly ─► OmniWin replays it (speed 1) ─► done
              │
              ├─ (only if you wired a decision model and set it to act) it picks a recipe or
              │   quick command and is sure enough ─► OmniWin replays it (speed 1b) ─► done
              │
              └─ needs_brain  (+ "recipes: …" shortlist, speed 2)
                    │  AI works (or runs a shortlisted recipe with workflow_run)
                    ▼
              AI calls task_done(success=true, template?, params?)
                    │
                    ▼
              OmniWin builds a candidate recipe from the steps that ran in this task
                    │
                    ▼   replays that finish clean 3 times in a row
              candidate ─────────────────────────────────────────► trusted (speed 1 allowed)
                    ▲  a replay stops at step k: the AI finishes the task, OmniWin keeps steps 1..k-1,
                    └─ swaps in the AI's new steps, saves a new version (candidate again)
```

### 3.1 Learning (episodes)
- An **episode** opens when `omniwin_do` answers `needs_brain` (it stores the goal text in memory, the run-log position, and the start time). If the AI never called `omniwin_do`, `task_done(goal=…)` can still learn from the steps since the last episode ended.
- The episode counts every act it sees. `RunLogFilter` already wraps every acting tool, so failed, blocked and unrecorded acts are counted in the same place (small addition there, no change to the tools).
- `task_done(success, goal?, template?, params?)` closes it. It learns **only** when ALL of these hold: `success` is true; at least 2 recorded steps (one act is no saving); at most 60; no failed or blocked act inside the episode (a "noisy" run can still be saved as tier-2-only, see below); every step passes the safety gate; the goal has no risky word (delete, install, send, pay, submit, and so on, the router's list). A refusal comes back with the reason, so the AI can fix it and call again.
- **Noisy runs.** If the AI wandered (a failed click then a retry, a menu opened and closed), the recipe is saved as *tier 2 only*: it can be suggested but never auto-run, and it stays that way until a replay finishes clean 3 times.
- **Template and parameters.** The AI passes `template` such as `open {file} in word`, and `params` mapping the recipe's text placeholders to slot names (`{"p1":"file"}`). OmniWin checks that the template matches the goal text, that slot names and placeholders line up one to one, and that the template has at least 2 plain words (so it can never match "anything"). In the answer it shows each slot with the control it types into (`file -> typed into "File name"`), so a swapped mapping is visible at once and the AI can correct it. No template means no slots, so only a recipe without text parameters can ever auto-run.
- Constant typed text is never stored (existing privacy rule), so every typed text is a slot filled from the request or from the AI.

### 3.2 Storage (append-only, nothing replaced or deleted)
- The steps are a normal workflow file in `workflows\` (so `workflow_run`, `workflow_list` and the gate checks keep working unchanged). New versions get new names: `open-in-word`, `open-in-word-v2`, and so on (names already allow letters, digits, `-` and `_`, up to 64).
- A **recipe card** beside it, `recipes\<name>.recipe.json`, written once and never changed: goal text, template, up to 5 aliases, workflow name and version, number of steps, `learned_wall_ms` (the real time the first run took, thinking included), which tools/apps it touches, and "tier 2 only" if noisy.
- `recipes\events.jsonl`: one appended line per event (`learned`, `replay_ok`, `replay_stopped step=k`, `repaired`, `promoted`, `disabled`, `enabled`, `superseded`) with time and `replay_wall_ms`. A recipe's trust level, counts and time saved are always computed from this file, so no file is ever rewritten. OmniWin still never deletes; you can delete files yourself, or disable a recipe from the AI.

### 3.3 Finding a recipe (three speeds)
- **Speed 1, instant.** Inside `omniwin_do`, after the existing phrase table: the request is normalized the same way (lower case, fillers removed), must be 12 words or fewer, have no risky word, and match **exactly one** *trusted* recipe's template or alias as a whole sentence. Slot values must be 1 to 60 safe characters. Then the recipe runs through the normal replay. Not allowed at speed 1: a recipe with a Risky step, a browser step (pages change; tier 2 only in v1), or a recipe that is not trusted. If step 1's element is not on screen, nothing is sent and the answer is `needs_brain` (the AI then handles it).
- **Speed 2, one AI step.** When nothing matches exactly, the `needs_brain` answer adds a line `recipes: name(params) steps=N, "<goal>" [candidate|trusted]` for the best 3 recipes (plain word overlap with the goal, aliases and description; no model). The AI runs one with `workflow_run(name, params)`. Candidates are runnable here; this is how they earn trust under supervision.
- **Speed 3.** The AI works as today.
- **Wording learned for free (no model needed).** When the AI runs a recipe for a request that matched nothing exactly, and the whole run is confirmed, OmniWin can add that exact wording as an alias: each parameter value the AI passed that appears once, word for word, in the request becomes a `{slot}`. The same sentence is then instant next time. At most 10 aliases per recipe, kept as appended events (the recipe card never changes), only for recipes that qualify for speed 1, never for a request with a risky word. (Added while writing section 3.7; tell me if you do not want it.)
- Matching sits behind small interfaces, so the optional decision model of section 3.7 plugs in without touching anything else.

### 3.4 Replay, stats and repair
- Replay is the existing `RunWorkflowAsync`, wrapped so each run appends its event with the real wall time. Per-step overhead stays under the existing 20 ms budget.
- **Trust.** A recipe becomes *trusted* after 3 replays in a row where the whole run was `confirmed` (or every step passed), with no stop. One stopped replay drops it back to candidate. (`promote_after` is configurable.)
- **Repair.** When a replay stops at step k, the answer says so and starts a repair episode linked to that recipe. When the AI finishes and calls `task_done(success=true)`, OmniWin builds the new version from steps 1 to k-1 of the old recipe plus the steps the AI ran after the stop, saves it as `-v2` (candidate), and marks the old one `superseded`. The old version stays on disk.
- The result of every recipe run is **never better than what OmniWin verified**: a stopped step is a stopped run, `unverified` stays `unverified`, and `retry=` advice stays as in contract 2.5.

### 3.5 Safety and privacy rules (all of them, so you can check me)
1. Deleting stays refused. Replay re-checks the gate for every step, exactly like `workflow_run` does today.
2. The stop key (Ctrl+Alt+Shift+K) stops a replay like any run.
3. Nothing is learned from a run that was not declared successful, that had risky words, or that contains a step the gate would deny.
4. Nothing auto-runs (speed 1) unless it is trusted, low-risk (no Risky step), without browser steps, and matched exactly once.
5. `learning.auto_run=false` in `config.json` turns speed 1 off entirely; `learning.enabled=false` turns learning off. Both are the owner's switches.
6. **Poisoned learning.** If a web page or document tricked the AI into doing something, learning could save it. The defenses: the same gate on every step; 3 clean replays before anything auto-runs; speed 1 excludes browser steps and Risky steps; everything is plain, readable files and an event log; the template must match the goal text. A residual risk remains until the hotkey box exists, because "the user said it worked" currently reaches OmniWin through the AI. Said plainly so you can decide.
7. Privacy: typed text is never stored (existing rule); the goal text and template are stored locally in `recipes\` only, never sent anywhere and never written to the activity log (the audit keeps only recipe name, ids and counts). The template's plain words come from your request, so avoid private words that are not slots.
8. `engine_call` (Windows-MCP, agent-desktop) is **not** learnable in v1: its results are never `confirmed`, so a step cannot be verified. Power tools stay off.
9. The optional decision model (3.7) only chooses. It cannot skip any rule above, never invents a value, never sees the screen, and is off by default.

### 3.6 What changes in the product
- **New tools (2): `task_done`, `workflow_manage`** (disable or enable a recipe, or report a decision-model `wrong_pick`). Tool count by default 30 to 32. Descriptions kept short on purpose: every tool adds words to every AI request, which itself costs time.
- **Changed:** `omniwin_do` (opens an episode, runs speed 1, asks the decision model only if one is wired, adds the shortlist); `workflow_list` (shows learned or manual, trust level, runs, last result, real time saved); `workflow_run` (records events); `omniwin_status` (counts: recipes, trusted, speed-1 hits, falls back; the decision-model state and its shadow numbers); `omniwin_guide` (new topic `recipes`).
- **Config:** new `learning` section: `enabled` true, `auto_run` true, `promote_after` 3, `max_recipes` 200, `episode_max_steps` 60, `episode_idle_minutes` 10. New `decision` section, all off by default: `provider` none, `mode` off, `threshold` 0.95, `margin` 0.30, `max_latency_ms` 400, `timeout_ms` 1500, `max_words` 20, `endpoint` empty, `api_key_env` empty, `allow_remote` false.
- **Contract 2.6** appended to `02_Architecture_and_Contracts.md`; `docs/*` stay reviewer-owned, so I only append.

### 3.7 Optional decision model (off unless you wire one)

**What it is.** A small, fast "picker": you give it the request and a list of options, and it gives back a confidence for each option. It does not write text and does not plan. OmniWin offers it only options OmniWin itself lists: the **trusted recipes** (goal, template, aliases) and the **built-in quick commands** (volume, mute, media, windows, apps), plus "none". Every task OmniWin learns becomes an option for it automatically from then on: nothing to train and nothing to set up per task.

**Where it sits.** After the two exact checks and before the AI:
1. phrase table, 2. learned template or alias, 3. **decision model (only if wired and set to act)**, 4. shortlist and one AI step, 5. the AI as today.
It widens speed 1 to differently worded requests ("get me invoice 57", "pull up last month's report"). If you always word a task the same way, the exact match already makes it instant and the model adds nothing.

**It acts only when ALL of these hold.** Anything else goes to the AI, which also gets the model's top 3 as its shortlist when the model produced them.
- The model is set to `act` (see shadow mode below).
- Its top pick is a trusted recipe or a built-in command, with confidence at least `threshold` (default 0.95), a lead of at least `margin` (default 0.30) over the second pick, and an answer within `max_latency_ms` (default 400).
- The request has no risky word (same list as the router) and at most `max_words` (default 20) words.
- The recipe passes the same rules as speed 1: trusted, no Risky step, no browser step, its first element on screen.
- Every slot value is found **word for word** in the request. The model never invents a value; a value it cannot find means the AI is asked.
- Replay then works exactly as in 3.4: every step checked, the gate on every step, a stop at the first step that fails.

**The AI gets involved whenever:** no model is wired; the model is off or still in shadow; confidence or lead is too low; it says "none"; it is slow, down, unreachable or out of quota (the request simply continues to the AI and costs at most `timeout_ms`, default 1500); the pick is untrusted, risky or browser-based; a value is not in the request; step 1 is not on screen; or a replay stops part-way (repair). A broken or hung model can never block or damage a request.

**Optional means optional.** `decision.provider` is `none` and `mode` is `off` by default. With no model wired nothing is called, no process is started and no connection is opened (a test checks exactly that). Wiring is yours alone: you set the provider in `config.json`. OmniWin never picks, downloads or signs up for one.

**Shadow mode first.** When you wire a model it starts in `shadow`: it decides silently on requests that go to the AI, OmniWin compares its pick with what really happened (which recipe or command ran), and keeps only counts (picked id, confidence, time, right or wrong; no request wording). `omniwin_status` shows for example "decision model: shadow, 52 confident picks, 0 wrong, median 90 ms". **You** switch it to `act` when you are happy (one word in config); OmniWin never switches itself. To be honest about the maths: 50 clean picks only shows it is wrong less than about 6 percent of the time; the evidence keeps growing as you use it, and a wrong pick reported with `workflow_manage(name, wrong_pick)` drops it straight back to shadow. For the built-in quick commands the existing offline test (the 793 labelled commands Laya failed) applies as well, through a new `decision-eval` bench command, before you ever switch to `act`.

**What is sent to a model.** Only the normalised request wording and the option labels (recipe goals, templates, aliases, built-in command names). Never screen contents, window titles, file paths, typed text, clipboard or earlier values. A model running on this PC needs nothing else. A hosted model also receives those words over the internet, so it is blocked unless you set `allow_remote: true` and name its address; its key is read from an environment variable you name (`api_key_env`) and is never written to a file or log; `omniwin_status` says "remote" when one is in use. The audit log keeps only provider, outcome and time, never the wording.

**How a model is wired (adapters).** One small interface: request and options in, ranked options with confidence out. Adapters: `laya` (the existing offline helper; already written, failed its gate, stays off; it would need a dynamic option list first) and `http` (any service speaking the same decision-API style: the DataCamp guide says Jev's `/v1/systemone` contract is also implemented by Kev and Rizzo Flow, and NanoJev has its own `/api/evaluate`). I have **not** read those APIs' exact formats, so the `http` adapter is written **only after you choose a model and I have read its docs** (price, privacy terms, request format). I will not create accounts or keys; that is yours to do.

**What the benchmark says (their numbers, not tested here).** Artificial Analysis lists decision models answering in roughly 60 to 300 ms per decision (Celeris 61, Elephant 79, Jev 92, Mammoth-1 106, Luna Decisions 206) plus network time, against seconds for an AI step. Their scores come from document and tool-calling tests, not from your requests, so shadow mode on your own tasks is the real test.

**Boundary.** The model only chooses among learned recipes and built-in quick commands. It does not pick arbitrary tools or click targets for a brand-new task; that needs the screen and judgment and stays with the AI.

### 3.8 Hotkey box, optional voice, and a settings window (all optional)

**The hotkey box.** A small always-available box: press a hotkey (default `Ctrl+Alt+Space`, changeable, never the stop key `Ctrl+Alt+Shift+K`), type a request, press Enter. It runs through the same path as `omniwin_do`: phrase table, learned recipes (speed 1), and the decision model if you wired one. It shows what ran and the result in OmniWin's own words (`confirmed`, `unverified`, `needs the AI`). After a recipe or model pick it shows **Right / Wrong** buttons: Right counts toward trust; Wrong flags a wrong pick, drops the model to shadow and demotes the recipe. This is the direct "did it work?" from you, without an AI in between.
- **The box has no brain.** When a request is not something OmniWin can do itself, it says "needs the AI", keeps the request in its history, and offers **Copy** so you can paste it into your AI. It does not hand requests to an AI by itself in v1.
- It learns nothing from your typing by itself; learning still comes from tasks the AI performs (3.1).

**How it is built (small, no duplicated logic).** A separate small Windows program, `OmniWin.Companion` (tray icon, box, settings). It starts its own copy of `OmniWin.Server.exe` as a child and talks to it like any AI client does, over MCP. So the router, recipes, safety gate and checks are exactly the same code, nothing is re-implemented. Two copies of the server (yours for the AI client, and the box's) share the config and recipe files, so they also need one new machine-wide input lock so they never type at the same moment (the current lock is inside one process). The stop key works for both.
- **Protected from the AI.** The companion's process joins the protected-process list, so OmniWin's own tools refuse to click or type in the box or the settings window. An AI client cannot use OmniWin to change your settings or inject requests through them.
- **Not started for you.** It runs when you start it. No startup entry and no scheduled task (that stays your choice, and I will not create one).

**Optional voice.** Off by default (`speech.provider` none). Push-to-talk only: hold a key (default `Ctrl+Alt+V`), speak, release. No always-listening and no wake word in v1.
- The speech engine is a swappable piece, like the decision model: `windows` (Windows' own speech recognition, if it works offline here; I will check before choosing), `whisper` (a local Whisper model through `whisper.cpp`, offline; the smallest models are roughly 75 to 150 MB, bigger ones are more accurate but slower on this CPU; sizes and speed to be confirmed), or `http` (a hosted speech service). Hosted is blocked unless `allow_remote: true`, the key comes from an environment variable you name, and the audio is sent out only then.
- The spoken words become text, shown in the box, then follow the exact same path as typed text, with the same safety rules (a risky word still never auto-runs). Because speech recognition makes mistakes, voice requests show the text for `confirm_delay_ms` (default 1500) with a **Cancel** button before running; `confirm_before_run` can be set to wait for Enter instead.
- Audio is never saved to disk, never logged and never sent anywhere in local modes. The microphone switch in Windows privacy settings is yours: OmniWin never changes it. I download no model without your yes.

**One app with four voice and text features (owner's decision, 2026-11-15: OpenWhispr is out; OmniWin builds these itself).** The companion app is the single UI. Each feature has its own hotkey, and each can be switched on or off and re-mapped in the settings window; all are off until you turn them on.
| Feature | Default key | What it does | Needs |
|---|---|---|---|
| Command box | `Ctrl+Alt+Space` | type a request, OmniWin runs it | nothing extra |
| Voice command | hold `Ctrl+Alt+V` | speak a request, OmniWin runs it (text shown first, Cancel possible) | a speech model |
| Dictation | hold `Ctrl+Alt+D` | speak, and your words are typed at the cursor in any app | a speech model |
| Polish selection | `Ctrl+Alt+P` | rewrites the selected text (a prompt, pasted text, an email) in place; modes: fix grammar, make clearer, make formal, shorten, turn into a good AI prompt | a language model |
- **Speech models: several local choices, picked in the UI.** One engine (`whisper.cpp`, offline) with a model list: Whisper tiny (about 75 MB), base (about 142 MB), small (about 466 MB), medium, large, turbo; a second engine for Parakeet (about 680 MB, made for CPU) if it proves worth it. The settings window shows each model's size, lets you download it (only when you press the button), and has a Test button that shows speed and the recognised text on this PC. A hosted speech service stays possible behind `allow_remote`.
- **Dictation** types through OmniWin's own checked typing (about 8 ms per character, measured) or pastes, your choice; a light cleanup (fillers, punctuation) is optional and needs the language model. It refuses to type into password fields and secure windows, like every OmniWin act.
- **Polish needs a different kind of model than the decision model.** A decision model (Jev, Elephant and the like) only *picks* from options: it decides which recipe or command to run and cannot rewrite text. Rewriting needs a text-writing language model, wired the same optional way (`polish.provider`): a local one through `llama.cpp` or Ollama (a 1 to 5 GB download, slow on this CPU: seconds per paragraph, to be measured), or a hosted one with your own API key behind `allow_remote` (fast, but the selected text leaves the PC). Polish reads the selection through the clipboard and puts your clipboard back afterwards; the original text is kept so one key press undoes it.
- **How the pieces fit:** speech model = ears, decision model = fast picker, your AI = brain for new or hard tasks, language model = editor for text. All four are optional and independent; OmniWin's core and the typed box work with none of them.
- Speech and language models are separate helper processes (like the engines today), ended by the stop key and by timeouts; audio and text are never saved or logged.

**FINAL CHOICE OF BASE (2026-11-15, after reading both code bases; owner: "whichever is better"): the app starts from a copy of HANDY's code (MIT, Tauri/Rust, about 63,000 lines), not OpenWhispr's.** Reasons and evidence are in `NOTES_openwhispr_handy_code_read.md`: Handy is dictation only, has no accounts, cloud or telemetry, and its 76-model catalog (pinned versions, hashes, licences) already contains every OpenWhispr speech model. OpenWhispr (about 180,000 lines, mostly meetings, notes, cloud and connectors) stays the *reference* for the selected-text rewrite and for the look. Handy's name and logo are not open, so the app is renamed. The paragraph below about copying OpenWhispr is superseded and kept only for the record.
- **Build needs (asked one by one):** the Rust toolchain and Tauri's build tools, Node or Bun packages for the UI, the speech engine library, and later each model.
- **Kept from Handy:** dictation hotkeys (hold or toggle), the model catalog and downloader, paste methods, history, "transcribe with post-processing" and its **prompt list** (named instructions such as "improve transcription"; you can add your own), its provider settings (OpenAI, Anthropic, OpenRouter, Groq, Cerebras, Z.ai, Bedrock, or a custom address such as a local model server), its command line.
- **Added:** the OmniWin section, command box, Action hotkey, Right / Wrong buttons (as above), and **Polish selection**.

**Polish selection and prompt processing: two routes, both using the same named instructions.**
| Route | How you start it | Who rewrites | Needs |
|---|---|---|---|
| A. Hotkey | select text, press the Polish key (or hold it and speak an instruction: "make this formal") | the provider you set in the app | an API key, or a local model server |
| B. Your AI | tell Claude, Codex or Antigravity "polish what I selected" or "rewrite it as a bug report" | that AI itself, through OmniWin | nothing extra: no key |
- **Route A (new in the app):** read the selection, send it with the chosen instruction to the provider, replace the selection, keep the original for one-key undo. Dictation clean-up ("prompt processing") already works this way in Handy and is kept; Polish reuses the same instructions and provider.
- **Route B (new OmniWin tools, contract 2.6):** `selection_get` reads the selected text of the window in front (through the accessibility text interface; fallback copy with the clipboard saved and put back); `selection_replace` puts new text in its place and reads it back, with the usual result line (`confirmed` only if the read-back matches). `polish_prompts` lists your named instructions so the AI can apply "my formal style" by name. Password fields and secure windows are refused; the selected text is marked as untrusted content, so text on screen cannot give the AI orders; nothing is logged. Tool count becomes 35 by default.
- **Limit, said plainly:** a hotkey cannot borrow the AI you chat with. The protocol has a feature for a tool to ask the client's own model ("sampling"), but AI clients support it unevenly and I have not checked Claude, Codex or Antigravity; I will check, and wire it only where it really works. So: at the keyboard without a chat, polishing and dictation clean-up need route A (a key or a local model); inside a chat, route B needs nothing.
- **Privacy:** route A sends the selected text to the provider you chose (a local server sends nothing out); route B sends it to the AI you are already talking to.

**(Superseded) Earlier idea: start from a copy of OpenWhispr's code.** This replaces "a small .NET companion" above and the "OpenWhispr is out" wording: the OpenWhispr *app* is not installed beside OmniWin; its *source code* becomes the OmniWin app.
- **Why this is allowed and sensible.** OpenWhispr's LICENSE is MIT (copyright "OpenWhispr Team"): copying, changing and redistributing is permitted if that notice stays in the copy. Its dictation, hotkeys, cleanup, "rewrite the selected text by voice", local language model (llama.cpp), model downloader and settings UI already exist and are what the owner likes. Rebuilding them in .NET would take far longer and come out worse.
- **What we keep:** dictation at the cursor, hold or tap hotkeys, cleanup, the Voice Assistant that rewrites selected text (this is "Polish selection"), translation, custom dictionary and snippets, history, the model manager, the two windows (small overlay and control panel).
- **What we add:** an **OmniWin** section in the control panel (learning and recipes, decision model, router, engines, safety with the locked switches); a **Command box** and an **Action hotkey** (speak or type a request, OmniWin performs it); Right / Wrong buttons; the app starts `OmniWin.Server.exe` as a child and talks MCP to it, so all checks stay in OmniWin's own code.
- **What we switch off or remove (to be confirmed after reading the code):** sign-in, cloud sync, the paid cloud models as defaults, team and enterprise parts, meeting recording, auto-update from their servers, any telemetry. Default is local-only. Their name and logo are replaced (a licence covers code, not a brand; Handy says so explicitly for its own name and logo).
- **Model list = OpenWhispr's list plus Handy's list** (github.com/cjpais/Handy, MIT, Tauri/Rust; only its model list and download sources are borrowed, not its code, unless a model needs it). From their pages, to be re-checked at build time: Whisper tiny 75 MB, base 142 MB, small 466 MB, medium 1.5 GB, large 3 GB, turbo 1.6 GB; Parakeet TDT 0.6B v3 (25 languages, CPU-friendly, about 680 MB); Parakeet Unified EN 0.6B (Handy's recommended one, 631 to 731 MB depending on the build); Nemotron streaming EN and 15-language (about 630 to 650 MB); Orukeet (about 672 MB); Cohere Transcribe (about 1.7 GB download); plus Handy's own quantised Whisper builds (small 487 MB, medium 492 MB, turbo 1.6 GB, large 1.1 GB as its page lists them). Each shows size, languages and a Test button; nothing downloads until its button is pressed.
- **Costs, said plainly.** (1) A second technology in the project: Electron, React and TypeScript on Node.js 24, next to .NET. (2) A large code base (about 2,850 commits) that I have **not read yet**; what can be cleanly removed is unknown until I do. (3) Their future updates have to be merged by hand, or we stop following them. (4) Building it needs downloads I will ask for one by one: the source, a portable Node.js 24 under `tools\`, the npm packages (hundreds of third-party packages), the whisper.cpp binaries, and later each model. (5) Our build is unsigned, so Windows may warn when it starts. (6) The app is bigger (hundreds of MB) than a small .NET tool.
- **First step after approval, before any change:** read the source and write `PLAN_app_from_openwhispr.md`: keep / remove / add file by file, where the hotkeys, paste, model registry and settings live, what talks to the network, and how the app is tested. The owner approves that before the fork is touched.
- The four-feature table above stays as the target; "Polish selection" is OpenWhispr's Voice Assistant on a selection, and its language-model choices (local, own key, self-hosted) replace the `polish.provider` design.

**(Superseded, kept for the record) Voice through OpenWhispr.** OpenWhispr (github.com/OpenWhispr/openwhispr, MIT, Windows installer) is a full dictation app. From its docs (read only; not installed, code not read yet):
- It already gives what was asked for: several local speech models to choose from (Whisper tiny 75 MB to large 3 GB, Parakeet about 680 MB and CPU-friendly, Nemotron streaming, Orukeet, Cohere Transcribe), dictation into any app at the cursor (default hotkey `Ctrl+Win`), a cleanup pass (fillers, grammar, punctuation; can run on a local model), and a Voice Assistant hotkey that rewrites highlighted text in place ("make this more formal"), which covers prompt polishing and pasted-text polishing. OmniWin builds none of that.
- **Wiring step 1 (no integration code): dictate into the OmniWin box.** Open the box, press OpenWhispr's dictation hotkey, speak; the text lands in the box like any text field, you see it, press Enter. Same safety rules as typed text. The box gets a "pause before run" option for dictated text. This works with any dictation app, so nothing ties OmniWin to one product.
- **Wiring step 2 (only after I read its code): one-key voice command.** The docs mention a local "loopback bridge" with "broadcast events" and a CLI that can transcribe on-device (`openwhispr --local transcribe`), but give no port, format or event details, and no documented hook that sends dictated text to another program. If the code shows a stable way, OmniWin's speech slot gets an `openwhispr` adapter (OmniWin records push-to-talk audio, OpenWhispr's installed model turns it into text). If not, step 1 stays the way.
- **Two hotkeys, two jobs (owner's idea, 2026-11-15): one key dictates, another key performs an action.** Three ways, tried in this order after I read the code:
  (a) *No fork:* OpenWhispr's Voice Assistant hotkey can use a "self-hosted OpenAI-compatible endpoint". The OmniWin companion would offer a tiny local endpoint of that shape (this PC only, with a secret key): OpenWhispr sends the spoken request there, OmniWin runs it through the normal router and safety rules, and answers with the one-line result. Unverified: exactly what OpenWhispr sends (its own instructions, any highlighted text) and that it types the answer at the cursor or shows it in its panel, which may be unwanted.
  (b) *Small fork:* OpenWhispr is MIT-licensed, so its code may be copied and changed if the licence notice is kept. Add one "Action hotkey" that sends the transcript to OmniWin instead of pasting it. Cost: an Electron/Node build to maintain, re-merging their frequent updates, and an unsigned installer we build ourselves. Kept as small as possible; offering the change back to their project would remove the upkeep.
  (c) *Fallback:* step 1 (dictate into the OmniWin box).
- **Not used:** OpenWhispr's cloud API and hosted MCP server (they manage notes and transcripts in its cloud and need an account and key; nothing OmniWin needs).
- **Yours to do and to know:** you install it yourself (I do not download or run installers; whether the Windows build is signed is not stated); choose "local" for transcription and cleanup so nothing leaves the PC; whether local-only use needs an account or has free-plan limits is not stated in the pages I read; polishing with a local language model is an extra multi-GB download and may be slow on this CPU; its meeting, notes and cloud-sync features are extras you can leave off.
- The `windows` / `whisper` / `http` engines above stay in the plan as later alternatives, not built first.

**The settings window.** One window that edits `config.json` so you never touch the file: sections for Learning, Decision model, Voice, Hotkeys, Router, Engines and Safety. Each setting has a plain-language line, its default, and a **Reset** button. It validates with the same rules the server uses, shows errors in words, and writes the file atomically.
- Safety settings that protect you are shown but **locked read-only by default** (the delete refusal, power tools off); changing those stays a deliberate hand edit of the file.
- Hosted-service switches (`allow_remote`) show a one-line warning about what leaves the PC.
- A running AI-client copy of the server picks up changes the next time it starts; the box's own copy restarts right after you press Save. The window says which.
- The decision-model and voice sections have a **Test** button: it sends a harmless sample and shows the answer and speed, and shows the shadow numbers.

**Order of work.** The learning core first (P1 to P5). The box next (P6), the settings window after it (P7), voice last (P8). Each can be stopped at without losing the others.

## 4. Honest limits
- Needs the AI to call `omniwin_do` first and `task_done` last. If a client ignores that, nothing is learned (nothing is learned silently either).
- Same task, different values: yes (speed 1). Same task, different words: speed 2 (one AI step). A similar but different task (another app, another screen): no reuse; it is learned separately.
- Apps change. A moved or renamed control stops the replay at that step and triggers repair; it never guesses.
- A recipe replays what happened, including pointless detours that happened to succeed. The clean-run rule and trust rule limit that; pruning detours is parked.
- It cannot replace judgment inside a task.
- A decision model only helps for *differently worded* requests, and only for tasks OmniWin has already learned. How accurate it is on your tasks is unknown until shadow mode has collected data.
- The box has no AI of its own: anything OmniWin cannot do itself is shown as "needs the AI" and you take it to your AI.
- Speech recognition makes mistakes, especially with names and numbers; that is why voice shows the text and waits before running, and why a risky word never auto-runs. How good each speech model is on your voice and this PC is unknown until you try it.
- Over Remote Desktop the microphone has to be redirected for live voice, so the test round uses recorded or synthesised audio files instead of a real microphone; real-voice quality is something you judge yourself.
- A very polished look is not the goal of v1: the box and settings window are clean and simple; styling can be improved afterwards.
- "10 times faster" is my estimate for step-by-step tasks. The real number is measured per recipe from your own data (first-run wall time against replay wall time).

## 5. Build order (compile-only while building, as in the last plan)
1. **P1 Data**: `Learning/` in `OmniWin.Workflows`: recipe card, events log (append-only), trust computation, slug/version naming, `learning` config and validation.
2. **P2 Episodes + `task_done`**: episode tracker (hooked into `omniwin_do` and `RunLogFilter`), the "clean run" checks, template and parameter validation, risky-word and gate checks, recipe builder (reuses `WorkflowService.Save`).
3. **P3 Matching**: template and alias matcher (strict, speed 1) in the router; word-overlap shortlist (speed 2); wording learned from speed-2 runs; the decision-model seam with its off, shadow and act logic, thresholds, fall-backs to the AI, shadow counts, the `decision` config and the status line. No real model is needed for this: it is built and tested with a stand-in that returns scripted answers.
3b. **P3b Decision-model adapter (only after you choose a model and I have read its docs)**: the `http` adapter (or a dynamic-option `laya`) and the `decision-eval` bench command. Nothing is downloaded or signed up for before that.
4. **P4 Replay + repair**: events around `RunWorkflowAsync`, trust promotion and demotion, repair episodes (old prefix + new suffix, new version).
5. **P5 Surface**: `workflow_manage`, `workflow_list` and `omniwin_status` additions, guide topic, tool descriptions, audit metadata, docs and contract 2.6 section.
6. **P6 Hotkey box** (3.8): `OmniWin.Companion` with the tray icon, the box, history, Right / Wrong buttons, the child server over MCP, the machine-wide input lock, the protected-process entry.
7. **P7 Settings window** (3.8): the config editor with validation, defaults, reset, locked safety settings, and the Test buttons.
8. **P8 Voice command and dictation** (3.8): push-to-talk recording, the speech-engine slot with a stand-in, the `whisper.cpp` adapter and model list, typing at the cursor, confirm-before-run. Model files are downloaded only on your yes.
8b. **P8b Polish selection** (3.8): the language-model slot (local and hosted adapters), the polish modes, clipboard save and restore, undo.
9. **P9 One test round at the end** (section 6), fix what it finds, then report to the reviewer.

## 6. The end test round (what must pass, no hoping)
- **Unit tests**, next to the existing workflow and router tests: template matcher (matches and near-misses; a labelled set of at least 200 phrases; **0 wrong-recipe auto-runs** is the gate), slot extraction and limits, templates that would match anything are refused, risky words never reach speed 1, episode rules (clean, noisy, too long, failed act inside), trust promotion and demotion, repair (prefix kept, suffix replaced, old version untouched), the event log is append-only (no file rewritten or deleted), privacy (no typed text and no goal text in the activity log or events), config switches.
- **Decision-model slot (stand-in model, scripted answers; no real model needed):** off by default means zero calls, no process and no connection; it acts only when confident, with a clear lead, in time, below the word limit, with no risky word, for a trusted low-risk recipe, with every value found word for word; every other case (low confidence, close second, "none", timeout, error, unreachable, untrusted or risky pick, missing value, step 1 not on screen) ends at the AI with the shortlist; shadow mode never acts and counts right and wrong correctly; `wrong_pick` drops it back to shadow; a hosted address is refused unless `allow_remote` is set; what is sent contains only request wording and option labels (the test inspects the captured payload); the audit log holds no wording.
- **Box, settings and voice (stand-ins, no real model or microphone needed):** the hotkey opens the box and a different key does not (the live check sends the key through a repo test); a typed quick command runs and shows its result; "needs the AI" shows with Copy; Right and Wrong change trust and the model state; two server copies never type at once; OmniWin's own tools refuse to act on the companion's windows; the settings window rejects invalid values with a readable message, writes atomically, keeps locked settings locked, and Reset restores defaults; config round-trips with no field lost; voice: a synthesised audio file goes through the speech stand-in, the text is shown and can be cancelled, a risky word never auto-runs, audio is never written to disk (test inspects the folders), local mode opens no connection, and a hosted address is refused without `allow_remote`.
- **Live on the F01 fixture** (needs the Remote Desktop session `Active`, hands off, Edge counted before and after): learn "type a name, tick Agree, press Apply" by doing it once; check replay confirms, the fixture's own log shows exactly one Apply click per run, and recipe trust rises after 3 clean replays; speed 1 runs it from the phrase with no AI call; **drift test** with the fixture's "Rename target" button (Apply becomes "Renamed apply"): the replay must stop at that step with `wrong_target` 0 and nothing else clicked, then a repair produces `-v2` that replays clean.
- **Numbers reported:** first-run wall time (including a stand-in for the AI's thinking, clearly labelled as a stand-in) against replay wall time; OmniWin per-step overhead against the 20 ms budget; counts of AI round trips avoided. For your real feeling of speed: time one real task twice with a real AI and tell me the two numbers.
- Gates: speed 1 wrong-recipe 0; replays of a trusted fixture recipe 20 of 20 confirmed; drift test as above; no leftover windows or processes; Edge unchanged; all old suites still green (Release and Debug).

## 7. Decisions I need from you (my recommended answer first)
1. **Speed 1 (run with no AI at all) on by default** after 3 clean replays? Recommended **yes**; `auto_run=false` turns it off.
2. **Learning needs the AI to say `task_done`** (nothing learned silently). Recommended **yes**; a "guess success" mode is not planned.
3. **Where recipes live:** `%LOCALAPPDATA%\OmniWin\recipes\` and `workflows\`. Recommended **yes**.
4. **Browser steps** only at speed 2 in v1. Recommended **yes** (pages change and carry untrusted content).
5. **Test approach:** build first, one test round at the end, like the last plan. Recommended **yes**.
6. **Decision-model slot:** build the slot, shadow mode and safety rules now with **no model chosen or wired** (off by default). You choose a model later, once you have recipes and want differently worded requests to be instant. Recommended **yes**.
7. **Hosted models** (Jev, Celeris, Elephant, Mammoth-1 and Luna Decisions are hosted services) need `allow_remote: true`, which sends only your request wording and the option names out. Recommended **blocked by default**; a model that runs on this PC needs nothing.
8. **Hotkey box** with the default `Ctrl+Alt+Space`, run only when you start it (no startup entry), protected from the AI's own tools. Recommended **yes**.
9. **Settings window** with the safety protections locked read-only by default. Recommended **yes**.
10. **Voice and text features built into OmniWin's own app** (voice command, dictation, polish selection; OpenWhispr not used), each with its own re-mappable hotkey, all off until you enable them. I build the features and the model slots; each model download (speech, language) happens only when you press its button or say yes. Recommended **yes**.
14. **Base = Handy's code** (MIT, renamed), with selected-text polish by two routes (hotkey with a key or local model; or your AI through new `selection_get` / `selection_replace` tools). This replaces decision 12 below. Recommended **yes**.
12. (Superseded by 14.) The app starts from a copy of OpenWhispr's code (MIT), renamed, local-only by default, with the model list of OpenWhispr plus Handy. This replaces the small .NET companion in P6 to P8b: those phases become "read the code and write the keep / remove / add plan", "strip and rename", "add the OmniWin section, command box and action hotkey", "model list". Recommended **yes**, with the read-first step.
13. **Downloads for that** (source copy, portable Node.js 24 in `tools\`, npm packages, whisper.cpp binaries): asked one by one when needed. Recommended **yes**.
11. **Polish model:** local (private, slow on this CPU) or hosted with your key (fast, text leaves the PC)? Recommended: build both options, you choose in the UI after trying the Test button.

## 8. What I will not do
No commits, no deleting or replacing your files, no downloads, no system or power setting changes (including the Windows microphone and speech privacy switches), no startup entry or scheduled task for the box, power tools stay off, Laya stays off, no edits to `docs/*` beyond appended sections, no scratch scripts for real input (the live checks go through repo tests).

## 9. Parked (not in this plan)
- **Choosing and wiring one particular decision model** (Laya, Jev, Celeris, Elephant, Mammoth-1, Luna Decisions, Von, Phi and others). The slot, shadow mode and safety rules are in this plan (3.7). The choice, its adapter and any download or account are yours to decide later, and it still has to pass shadow mode (and the offline test for quick commands) before it is allowed to act.
- A wake word or always-listening voice, starting the box with Windows automatically, and a connector so the box can hand hard requests to an AI by itself.
- Composing recipes from recipes, pruning detours automatically, learning `engine_call` steps.
