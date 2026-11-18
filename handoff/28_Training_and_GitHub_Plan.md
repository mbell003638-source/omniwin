# 28. Plan: teach OmniWin everyday tasks, then publish it on GitHub

Date: 2026-11-17. Written by the reviewer at the owner's request. **Nothing in this plan has started.** Rewritten the same day with the owner's answers: GitHub account confirmed, the owner runs the console command, and the owner changes and reverts the power settings with the commands in section 3. Each phase still waits for the owner's yes on section 7.

## 1. What the owner asked for

Teach OmniWin about 500 everyday computer tasks once, so the next time each one is asked for it runs fast. The examples: Chrome, Calculator, typing, opening apps, a terminal, flights and hotels searches, Google Maps, Bluetooth, notifications, volume, camera, brightness, windows, Word, Excel, online forms and cmd commands. Then create a **public** GitHub repository with the whole project, the learned data and a handoff, so another AI can import and configure it. **The training must not need Remote Desktop to stay connected.**

## 2. Facts that shape the plan (checked 2026-11-17)

- **OmniWin cannot see or click anything while the Remote Desktop session is disconnected.** A disconnected session has no live screen, only a 640x480 placeholder, so every click, keystroke and screen read is refused (the live tests skip for this reason). This applies to training, and to using OmniWin at any time without a live desktop.
  - **The way round it, without Remote Desktop:** move the Windows session to the PC's own screen (the "console") instead of disconnecting. The owner runs one command as administrator, from inside the Remote Desktop window, instead of closing it: `tscon <session id> /dest:console`. The Remote Desktop window closes and the desktop keeps running on the laptop's own display.
  - **Two conditions:**
    - The PC must not lock, sleep or turn off its display while training runs. The lock, sleep and lid settings are the owner's to set; the AI will not change them.
    - Anyone near the laptop can see, and interfere with, what OmniWin is doing.
  - The Claude sessions themselves run on the PC whether or not Remote Desktop is connected, so the owner can follow and stop from the phone over Remote Control.
  - **Not yet tried on this PC:** run one short live test after `tscon` before the long run.
- **Already instant, so not learnable:** the quick commands volume up, down and set, mute, media play, pause, next and previous, open or switch to an app, minimise, maximise, restore and close a window, time, battery. `task_done` refuses them (`template_conflict`). They are already about 20 to 70 ms.
- **"500 tasks" is really about 60 to 100 different tasks**, each with many wordings and values ("open {file} in notepad", "search flights from {from} to {to}"). One learned task covers all of its values. Teaching 500 different wordings is easy once those tasks exist (each new wording is learned when the AI uses the task for it).
- **Installed:** Chrome (`C:\Program Files\Google\Chrome`). **Not installed:** Word and Excel (no Microsoft Office found). So "make a Word document" and "create an Excel sheet" cannot be taught unless the owner installs Office, or uses WordPad and Notepad, or Excel on the web.
- **What a learned task can do by itself** (owner decisions in handoff 26):
  - **Runs by itself after 3 clean replays:** desktop apps such as Calculator, Notepad, Explorer, Settings pages, the Terminal window and Paint.
  - **Never runs by itself, only by the AI:** browser steps (flights, hotels, Maps, online forms) and steps labelled Risky. The AI still runs them in one call (`workflow_run`), which is much faster than step by step.
  - **Not learnable:** steps through the bridged engines.
- **The learned data only works on a PC that looks the same.** A task remembers controls by their names and positions in the app's layout. On another PC with a different Windows language, app version, or Chrome instead of Edge, it stops safely at the first step that differs (nothing is clicked) and the AI does it there instead. So the published learned data is "starter recipes": they work out of the box only on a matching setup.
- **GitHub:** the `gh` command is signed in as **`mbell003638-source`** (owner switched it; checked by the reviewer with `gh auth status` on 2026-11-17).
- **Licences:**
  - The app is a copy of Handy's MIT code: allowed with its licence kept.
  - Windows-MCP and agent-desktop are other people's programs, under `tools\`. Their licences must be checked before copying them into a public repository; the safer route is a setup script that downloads them, pinned and checksum-checked.
  - **Laya (1.5 GB) must not be uploaded.** Laya *was* built: it is stage B of `omniwin_do` (contract 2.5). On 2026-11-14 PyTorch and the model were installed for an offline test. They were recorded then as owner-approved, but **the owner said on 2026-11-17 that they never approved it**: they wanted only an optional slot for a future third-party model such as Jev. That slot already exists: the decision-model slot of contract 2.6, which is off and has no model. The test failed: 61% correct, 827 ms on this PC's processor. So it was switched off and never used. What is left on disk is that test setup: `tools\laya\.venv` (PyTorch, 682 MB) and `tools\laya	yped-decisions` (the model, 808 MB). OmniWin does not need either. The scripts and results (a few KB) are worth keeping.
  - OmniWin itself needs a licence chosen by the owner (no licence means nobody may legally reuse it).

**Sizes and personal data (measured 2026-11-17):**
- **Sizes:** source and tests about 4 MB; `app` 68 MB; `publish` 198 MB; `artifacts` 49 MB; `tools` 1.7 GB, of which Laya is 1.5 GB, uv and its cache 158 MB, and Python 66 MB.
- **Personal data:** the owner's email appears in no project or handoff file. 160 files contain the path `C:\Users\<you>`; the scrub replaces it.
- **The everyday data folder** (`%LOCALAPPDATA%\OmniWin`) holds only `config.json` and `logs`: nothing has been learned yet.

## 3. Phase 0: make training possible without Remote Desktop (the owner runs these; about 5 minutes)

The AI does not change Windows settings: the owner's safety rules keep power, lock and system settings for the owner. The owner runs the commands below and reverts them when training is done.

**Current settings (read, not changed, by the reviewer, 2026-11-17; Balanced plan):**
- Plugged in: sleep **never**, screen off after **15 minutes**, hibernate never.
- On battery: sleep after **3 minutes**, screen off after 3 minutes.
- Screensaver: no lock set.

So, plugged in, the laptop already never sleeps. The only change is the screen turning off after 15 minutes. That probably does not stop OmniWin, but turning it off removes the doubt. **Keep it plugged in and the lid open** throughout: on battery it sleeps after 3 minutes, and closing the lid may sleep it.

**Before training**, in an **administrator Command Prompt**, while connected over Remote Desktop:
1. `powercfg /change monitor-timeout-ac 0` (screen never turns off while plugged in).
2. `query user` (note the number under ID for your session).
3. `tscon <ID> /dest:console` (Remote Desktop closes; the desktop keeps running on the laptop's own screen).

**The AI checks** that `query user` shows the session `Active` on `console`, and runs one short live test (`LiveSessionTests`) before any training. If it fails, the fallback is short training blocks while the owner is connected.

**After training** (connect over Remote Desktop again as usual, then in an administrator Command Prompt):
- `powercfg /change monitor-timeout-ac 15` puts the screen setting back to 15 minutes, exactly as it was. Nothing else was changed.

While the desktop is on the laptop's screen, anyone near it can see and interfere with what OmniWin does. The stop key (Ctrl+Alt+Shift+K) works on the laptop's keyboard, and you can stop the Claude session from your phone.

## 4. Phase 1: the task list (the AI, no input sent)

Write `bench/curriculum/tasks.json`: each task with its wordings (5 to 10 per task, so the total reaches about 500), sample values, the app it needs, its category and its expected end result (what to check). Proposed categories:

| Group | Examples | Can run by itself? |
|---|---|---|
| Calculator | "calculate {expr}", "convert {n} inches to cm" | yes |
| Notepad and WordPad | "write a note saying {text}", "new document titled {name}" | yes (typing into an empty box only) |
| Explorer | "open my {folder} folder", "make a folder called {name} on the desktop" | yes |
| Settings pages | "open sound settings", "open display settings", "turn night light on" | yes, read and navigate; see the safety list |
| Terminal and cmd | "open terminal", "run {command} in cmd" | AI only (Risky), and every command is still checked for deleting |
| Snipping, Paint, Clock and timers | "set a timer for {n} minutes", "take a screenshot" | yes |
| Chrome or Edge (OmniWin's own browser) | "search google for {query}", "open {site}", "directions from {a} to {b} on google maps", "search flights from {a} to {b} on {date}", "hotels in {city}" | AI only (browser) |
| Online forms | fill a test form (the project's own W01 page, never a real form with real data) | AI only |

**Left out, unless the owner says otherwise:**
- **Bluetooth on/off, brightness, camera pictures and notifications.**
  - Bluetooth and brightness are system settings, which the owner's rules keep for the owner.
  - Brightness cannot be changed at all over Remote Desktop.
  - The camera photographs the room.
  - A notification is different every time, so there is nothing to replay; the AI handles it live.
- **Anything that buys, books, sends, signs in, or enters real personal data.** Searches only: no booking, no sign-in, no real names or card numbers.

## 5. Phase 2: teach (an AI client connected to OmniWin, live desktop on the console)

- **Who:** a Claude session with OmniWin added as an MCP server (the published `publish\OmniWin.Server.exe`), working through `tasks.json`. For each task:
  1. Call `omniwin_do` with the first wording; it answers `needs_brain`.
  2. Do the task with OmniWin's tools.
  3. Check the end result.
  4. Call `task_done(success=true)` with the template.
  5. Replay it 3 times with other values (it becomes trusted).
  6. Run the other wordings once each (they are learned as aliases).
  7. Record pass or fail in `artifacts/benchmarks/curriculum-v1.json`.
- **Pilot first:** the first 50 tasks, about 2 hours. Report what worked, what failed and how long a task takes, then the rest.
- **Time:** about 2 to 4 minutes per task with an AI thinking (first run, 3 replays, wordings), so about 3 to 6 hours for 80 tasks with 500 wordings. Done in blocks of about 1 hour, stopped at once by Ctrl+Alt+Shift+K or from the phone.
- **Rules during teaching:**
  - OmniWin's own browser profile only.
  - No downloads or installs.
  - Nothing deleted outside the tests' own temp folders.
  - Every file the training makes goes into one folder, `Desktop\OmniWin-training`.
  - Edge and window counts are checked before and after each block.
  - Learned data goes into a separate data folder, `OmniWin-training-data`, not the owner's everyday one, so it can be reviewed before it is published or used.
- **Measured at the end:** first run against replay time for every task (the real speed-up with a real AI thinking, which was not measured before), and how many tasks run by themselves.

## 6. Phase 3: publish on GitHub (only after the owner's yes on section 7)

1. **Account:** the owner runs `gh auth login` as `mbell003638-source`, or says `<another-github-account>` is right. The AI checks `gh auth status` before anything else.
2. **A clean copy, not the working folder:** export to `Desktop\OmniWin-github`, so the working project stays untouched. Include:
   - the source (`src`, `tests`, `bench`, `testapps`, `app` without `node_modules` and `target`);
   - the docs, and the handoff folder as `handoff/`;
   - the scrubbed learned data (`workflows/`, `recipes/`);
   - `.gitignore`, `LICENSE`, and a `README` with "import and configure" steps.
   - **Excluded:**
     - `bin`, `obj`, `node_modules`, `target`, Laya's Python folder, `artifacts/test-results`;
     - the data folder's logs, `config.json` and the browser profile (it holds cookies);
     - speech models;
     - `tools\` engine files, replaced by a setup script.
3. **Binaries as a Release, not in git:** zip `publish\` (the self-contained server, about 200 MB, and `publish\app`) and attach it to a GitHub Release, with SHA-256 values in the release notes.
4. **Privacy scrub, then the owner looks:**
   - Scan for the owner's email, account names, `C:\Users\<you>`, the machine name, IP addresses, Tailscale or home-lab details and tokens. Replace them with placeholders.
   - In the learned data, remove the owner's own wording if they wish, and any literal web addresses with search details.
   - Show the owner the list of files and every replacement **before** the repository is made public.
5. **Create the repository:**
   - Make it **private first**, push, then let the owner look at it on GitHub.
   - Switch it to public only on their word.
   - The project has no git history today: this starts a new history in the export folder only.
6. **Handoff for another AI:** `handoff/START_HERE.md` covers:
   - what OmniWin is;
   - how to install: download the Release zip, or build with .NET 10 and Rust;
   - how to add it as an MCP server;
   - how to import the learned data (copy `workflows/` and `recipes/` into `%LOCALAPPDATA%\OmniWin`);
   - what to expect: starter recipes stop safely on a different setup, and the AI repairs them;
   - the safety rules.

## 7. Decisions for the owner

Settled 2026-11-17:
- the session was moved to the console: `query user` shows session 2 `console` `Active` (checked by the reviewer); sleep and screen-off are both never while plugged in;
- the GitHub account is `mbell003638-source`;
- the owner runs `tscon` and the power commands in section 3, and reverts them afterwards.

Still open:
1. **Scope:** pilot of 50 tasks first (recommended), or all of about 80 tasks and 500 wordings in one go.
2. **Left-out groups:** keep Bluetooth, brightness, camera and notifications out (recommended), or include some while you are at the laptop.
3. **Office:** skip Word and Excel (use WordPad and Notepad; recommended), install Office yourself, or use the web versions.
4. **GitHub:** repository name (for example `omniwin`); licence (MIT recommended); private first, then public after you look (recommended).
5. **Laya (settled 2026-11-17):** kept on this PC and **not deleted**; **never uploaded** to GitHub (`tools\laya\.venv` and `tools\laya	yped-decisions` are excluded). The small scripts, test commands and `tools\laya\README.md` (with where the model came from) go into the repository, so it can be downloaded again later only if the owner says yes.

When the plan is approved, the memory note "OmniWin: no commits, local only" is replaced by the owner's new decision to publish.

## Results, 2026-11-17/18 (done by the reviewer under the owner's goal "proceed with the plan, choose the recommendation")

- **Console check:** the session was moved to the console by the owner. `LiveSessionTests` passed the window test (`2026-11-17-console-live2`); the audio test failed because no speaker device is active on the console (not a desktop problem).
- **Teaching tool:** `bench/teach/` (`omni.py` MCP client, `teach.py` runner, `make_tasks.py` task list, `export_github.py` scrubbed export, `learned_readme.py`). It is a **scripted teacher** driving OmniWin's own tools (one server process per run, `omniwin_do` → steps → `task_done` → 3 replays → an instant run from words → every other wording). It cleans up only windows that appeared during a task (by window id), so the owner's windows were never touched.
- **Three OmniWin replay bugs found and fixed by the reviewer** (`WorkflowService.cs`, `DesktopVerification.cs`; contract §19 note, `docs/TOOLS.md`): a replayed step (1) now looks for its control for up to 3 s after a window opens, (2) finds modern apps hosted by `ApplicationFrameHost.exe`, (3) accepts the saved control by a unique role+name+AutomationId+class match when the saved path no longer lines up, and (4) retries a step that went stale with nothing sent, up to 3 times. Before these, most learned tasks in Settings, Calculator and Clock stopped at step 2 on every replay. Builds clean; Core.Tests 157/157, Router.Tests 171/171, Desktop.Tests 400 (0 failed); republished self-contained.
- **Final teaching run (`artifacts/benchmarks/curriculum-v3.json`, data `Desktop\OmniWin-training-data-v3`):** 44 tasks, 572 wordings tried. **38 tasks learned and verified** (3 clean replays each with the end result checked), **260 wordings learned**. **23 tasks run by themselves from the user's words** (median 1.46 s including the app opening); 15 Calculator modes replay reliably but are AI-run only (their first run had a retried step, so OmniWin marks them noisy). Not learned: Notepad note, Windows Update page and YouTube (replays failed), Google Maps and Wikipedia (end-result check timing), Downloads folder (refused: "downloads" is a risky word).
- **Not taught, by decision:** general Google web search (Google showed its "unusual traffic" bot check; never worked around), Google Flights and Hotels (results need picking a changing suggestion), plus the groups left out in section 4.
- **Honest scale:** 260 learned wordings over 38 tasks, not 500; the remaining requests of the original list either are already instant quick commands, are left out for safety, or could not be made reliable here.
- **Published 2026-11-18:** public repository **https://github.com/mbell003638-source/omniwin** (account `mbell003638-source`, MIT): 1,188 files in five commits (a fresh history made from the scrubbed copy `Desktop\OmniWin-github`; the working project's own old local git history was not published). Release **v2.6.0** with `OmniWin-2.6-win-x64.zip` (79.4 MB, SHA-256 `2B95012D…0F0F`, matched by GitHub's own digest after upload). The repository was created private, checked (file count and asset digest equal the local copy; no personal details found by the scrub scan), then made public as the owner asked.
- **Left for the owner:** reconnect over Remote Desktop and run `powercfg /change monitor-timeout-ac 15` to put the screen setting back. During the Calculator tasks the clipboard was used, so its earlier content is gone.
- **Second teaching pass, 2026-11-18 (to reach the plan's ~500 wordings):** OmniWin keeps at most 10 extra wordings per recipe (11 in all), so more tasks were added: Media Player pages, Weather views, File Explorer places and tabs, and Clock, Media Player and Settings pages. Notepad and Windows Update, which had failed their replays, now run instantly from their words. **Final: 63 of 67 tasks learned and verified, 515 wordings learned, 48 tasks run by themselves from the user's words** (`artifacts/benchmarks/curriculum-v3.json`). Still not learned: Google Maps and Wikipedia (end-result check timing), YouTube (replays failed), Downloads (risky word, refused by design). Browser tasks never gain extra wordings, because OmniWin never runs a browser recipe by itself. The repository was updated with these results.
- **Third pass, 2026-11-18 (to reach the plan's ~80 tasks):** added Microsoft Store pages (view only, never installs; its Downloads page skipped), Photos views and Paint colours. **Final: 76 of 80 tasks learned and verified, 618 wordings learned, 57 tasks run by themselves from the user's words.** Not learned: Google Maps, Wikipedia and YouTube (their replays fail), and Downloads (risky word, refused by design).
- **Open finding for OmniWin's browser engine:** after a search on a very large page (a Wikipedia article), `browser_observe` answered `error engine_timeout` ("The browser did not answer in time") repeatedly, although `browser_tabs` showed the page loaded. That is why the web replays fail. Next step for a builder: make a large page's scan stop at its element limit, or within its time limit, and answer with what it read instead of timing out.
- **Fourth pass, 2026-11-18 (web tasks):** the web replays stopped at their first step because a replayed `browser_nav` carries no expectation and a navigation shows no visible change, so it could never pass. **Fixed by the reviewer** (`WorkflowService.cs`): a replayed `navigate` must now arrive on the saved address's host (`url_contains`, 10 s). The teacher also checks a web result by the tab's title and address, because reading a whole very large page can time out (the browser-engine finding above stays open), and Google Maps now submits with its Search button. YouTube, Wikipedia and Google Maps are learned with 3 of 3 clean replays. No learned file contains map coordinates or the PC's location.

## Fifth pass, 2026-11-18 (the owner's left-out items, as far as they can be done safely)

- **Display (brightness), Sound, Notifications, Power and Storage pages of Settings:** learned (view only). OmniWin opens the page; changing brightness or Bluetooth is a system setting, which the AI does not change for the owner.
- **Camera:** this PC has no camera (the Camera app says "We can't find your camera"), so taking a picture cannot be taught here.
- **Notification center:** a single shortcut (Win+N); a recipe needs at least 2 steps and the AI opens it in one call anyway.
- **Web search:** done through DuckDuckGo, because Google showed its bot check. Saved, but the replays type the words without submitting: OmniWin does not record an Enter press that shows no visible change, so the submit step is missing. **Open item for a builder:** store each step's met expectation in the workflow file and re-apply it on replay (handoff 26 §12 already names this risk). The same gap stops Google Flights and Hotels.

## Sixth pass, 2026-11-18 (web search)

- **Fixed by the reviewer, so a web search can be learned:** (a) a saved step keeps a met `url_contains` or `window_opened` expectation in its arguments and a replay checks it again (`StepCall.cs`; no file-format change; text-bearing expectations and session window ids are never saved); (b) a pressed key name is saved as a literal, not as a parameter; (c) the browser checks a `url_contains` expectation on the new page when an act navigated away mid-check (`BrowserEngine.cs`). Tests: `SavedExpectationTests` 5 of 5, Core.Tests 162/162, Router.Tests 171/171, Browser.Tests 150 (0 failed). Republished.
- **Web search (DuckDuckGo) is learned** with 3 of 3 clean replays.
- **Google Flights and Hotels stay open:** their boxes do not submit on Enter, and their suggestions are list items with no name or ID. OmniWin will not save a click on something it cannot find again (that is the safe behaviour), so these cannot be checked recipes. The AI can still do them live.

## Plan completion checklist (2026-11-18)

| Plan item | State |
|---|---|
| Phase 0: training without Remote Desktop (console, screen never off) | done by the owner; checked (session 2 `console` `Active`) |
| Phase 1: task list (about 80 tasks, about 500 wordings) | done: 80 tasks, 1,532 wordings offered (`bench/teach/make_tasks.py`) |
| Phase 2: pilot, then the rest | done: **85 of 89 tasks learned and verified, 667 wordings learned, 61 tasks run by themselves** (`artifacts/benchmarks/curriculum-v3.json`). Not learned: Downloads (refused by OmniWin's own rule; its wording contains "download"), notification center (one shortcut, nothing to learn), Flights and Hotels (suggestions have no name to find again; see the sixth pass) |
| Bluetooth, brightness, notifications | their Settings pages are learned (view only); changing those settings stays with the owner |
| Camera | no camera on this PC |
| Buying, booking, signing in, personal data | left out as the approved plan says |
| Terminal commands | left out: a learned paste into a terminal would skip OmniWin's typed-delete check |
| Word and Excel (decision 3) | not installed; WordPad is not on this PC either, so Notepad covers note writing |
| Web searches | YouTube, Wikipedia and Google Maps learned. General Google search was stopped by Google's bot check, and Flights and Hotels need a changing suggestion picked, so neither can be a checked recipe. The AI can still do all three live |
| Phase 3: scrub, licence, handoff, private repository, release, public | done: https://github.com/mbell003638-source/omniwin, release v2.6.0 (zip rebuilt with every fix) |
| Revert the screen setting | for the owner: `powercfg /change monitor-timeout-ac 15` |

