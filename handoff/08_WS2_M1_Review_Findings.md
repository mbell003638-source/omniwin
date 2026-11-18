# WS2 M1 review: reviewer findings and reply to the builder

Copy everything below the line and send it to the AI.

---

The reviewer checked your M1 work against the code and the raw files.

**Verdict: No, M1 isn't accepted yet.** The plumbing works:
- Debug and Release build cleanly.
- The browser tests pass against a headless dedicated profile, using the real `SafetyGate`.
- No Edge processes are left behind.
- The benchmark numbers match `artifacts/benchmarks/ws2-browser-m1-benchmarks.json`.

But four safety problems break the owner rules. Fix S1–S4 first, then R1–R4, then the low items. **Don't start M2.** The owner's answers to Q1–Q16 will come separately; keep building to the stricter choice until then.

## Safety problems (must fix)

### S1: the page can fake what OmniWin sees, so the no-delete rule can be bypassed (critical)

- **Where:**
  - `src/OmniWin.Browser/PageScript.cs:11` and `:120`: the indexer lives on `window.__omniwin` in the page's own JavaScript world.
  - `src/OmniWin.Browser/CdpSession.cs:63-69`, `:106-113` and `:133-141`: `addScriptToEvaluateOnNewDocument` and `Runtime.evaluate` run without an isolated world.
- **Problem:** a page can define `window.__omniwin` first. Line 11 then skips the injection, and the page's fake `scan()` decides every name, role, state, coordinate and occlusion result OmniWin sees. The page can also override DOM methods such as `getBoundingClientRect`, `elementFromPoint` and `getAttribute`, or change the `__omniwin_ref` expando.
  - **Example:** a "Delete account" button reported as "Save" passes the no-delete gate.
- **Done when:**
  - The indexer and every in-page action run in an isolated world (`Page.createIsolatedWorld`, or `worldName` plus that context's id). Nothing OmniWin reads comes from page-defined globals or prototypes.
  - A fixture page tries to spoof (pre-defined `window.__omniwin`, overridden `getBoundingClientRect`, overridden `elementFromPoint`). A test proves OmniWin still sees the real name and the Delete control is still `denied` with `input_sent=false`.

### S2: clicks use stale cached data and coordinates (critical)

- **Where:** `src/OmniWin.Browser/BrowserEngine.cs`:
  - `:409`: the element comes from the last observation's cache;
  - `:438-454`: the prechecks read that cached copy;
  - `:459-483`: the gate sees the cached name;
  - `:515` and `:520`: the click goes to the cached `Center` point.
- **Problem:** if the page changed since the observation, the click lands on whatever is at that old point now, possibly a Delete control the gate never saw. Contract design priority 1 is never act on the wrong target.
- **Done when:**
  - Immediately before input, the target is re-read live: identity, name, role, enabled state, bounds and the occlusion hit test.
  - The gate is called with that fresh data.
  - A click goes only to a point whose hit test returns the exact element. A changed target returns `stale`, `input_sent=false`.
  - A test moves a Delete button under the old point after the observation; the click must not reach it.

### S3: the generation check never runs (high)

- **Where:** `src/OmniWin.Browser/BrowserEngine.cs:425-433`. `element.Role != element.Role` is always false.
- **Done when:** an act on a ref from an older generation compares it with the live element and returns `stale` if it changed. A test covers both outcomes: changed returns `stale`, unchanged proceeds.

### S4: navigation skips the stop key, the activity check and usually the gate (high)

- **Where:** `src/OmniWin.Browser/BrowserEngine.cs:213-320` (`NavigateAsync`). The gate runs only for "risky" URLs and `close_tab` (`:237`). There's no `IKillSwitch` or `IUserActivity` check at all.
- **Problem:** after the owner presses the stop key, navigate, reload, back, forward, new tab and switch tab still run. The contract and the brief say every act calls the gate before input.
- **Done when:**
  - Every `NavigateAsync` action checks the stop key, then calls the gate, before sending anything.
  - Tests show that each action returns `denied`/`kill_switch` with `input_sent=false` while the switch is tripped.

## Correctness problems (fix in this round)

- **R1: only http and https should be allowed.** `BrowserEngine.cs:228-233` blocks a short list of schemes. It misses `vbscript:`, `blob:`, `view-source:`, `devtools:`, and external protocol handlers such as `ms-settings:` or `search-ms:`, which can start other programs. Allow `http`, `https` and `about:blank` only. Treat everything else as Risky or refuse it, and add tests.
- **R2: results hide uncertainty.**
  - `BrowserEngine.cs:318`: a navigation with no expectation returns `confirmed`. It must be `unverified`.
  - `:304`: a failure after `Page.navigate` was sent reports `input_sent=false`.
  - `:587-591`: an exception partway through a click (mouse pressed, release failed) can report `input_sent=false`, and every exception is labelled `engine_timeout`.
  - `:530-537`: `type` focuses the element, then can return `denied` with `input_sent=false`.
  - Once any input command was sent, report `input_sent=true` (`error` plus `input_interrupted` if stopped partway). Use an accurate reason code.
- **R3: the process name is hard-coded as `msedge.exe`.** See `BrowserEngine.cs:240`, `:462` and `:477`, even when Chrome is launched. Use the launched browser's real process name, because the Safety policy and the audit log use it.
- **R4: the dedicated-profile check is a denylist.** `BrowserLauncher.cs:173-196` misses:
  - Chrome Beta, Dev and Canary (`Chrome SxS`);
  - Edge Beta, Dev and Canary;
  - Chromium, Vivaldi and Opera;
  - junctions, symlinks and 8.3 short names that resolve into a real profile.

  Use an allowlist instead: the profile must resolve, after following links, to a folder inside OmniWin's own data directory (`%LOCALAPPDATA%\OmniWin` or `--data-dir`). Refuse anything else, and test a junction into a fake "User Data" folder.

## Low (fix or note)

- **L1:** `BrowserLauncher.cs:65` deletes a stale `DevToolsActivePort` file. It's OmniWin's own file, but the owner's rule is "never delete". Prefer ignoring a port file older than the launched process instead of deleting it. Otherwise list this deletion in the status file so the owner can approve it.
- **L2:** `BrowserEngine.cs:635`: the `type` readback requires the whole value to equal the typed text. Typing into a field that already has text then falsely reports `contradicted`. Compare against the expected value after insertion, as WS1 does.
- **L3:** waits poll instead of listening for events. Navigation checks `document.readyState` every 25 ms (`CdpSession.cs:162-178`), and reload, back and forward sleep a fixed 100 ms. Either use page events (`Page.loadEventFired`, lifecycle events) or don't claim "event-driven waits" in the M1 checklist.

## Problems in your report (fix in the next one)

1. **The "Rules check" used a different rule list.** Rules 4, 5, 6 and 8 in your reply aren't the owner rules. The real ones are `00_START_HERE_New_AI.md` lines 97–104:
   - 4: no system or security setting changes;
   - 5: real input needs the owner's yes;
   - 6: leave the owner's files alone;
   - 8: no clipboard fallback, automatic replay or silent swap.

   Check each one by its real text.
2. **Test counts:** the run has 489 tests, 484 passed and 5 skipped, not 483. The benchmark test is a plain `[Fact]` that returns early without `OMNIWIN_BROWSER_BENCHMARK=1`, so it counts as a pass. Make it a skipped opt-in, the way WS1's benchmarks use `SkipUnless`, and report the count you actually see.
3. **Hashes:** "Core and Desktop match the historical WS1 acceptance hashes exactly" is wrong. The acceptance hash was Desktop `80C9E2C7…4D80`. `EB52842A…889A` and `65EE6BBC…C746` are the builds made after the T1 change.
4. **"Comfortably meet" the budgets:** `browser_act_live` measures `set_value` on a 14-element page, and that path has none of the live re-checks S2 adds. Re-measure after S1–S3, on `controls.html` and `large.html`, and report click as well as `set_value`.

## Reply format

Use the same headings as before: Result, Changes, Tests, Live runs, Measurements, Rules check, Open items and owner decisions, Hashes. Also:

- In **Result**, give Yes/No for each of S1–S4, R1–R4 and L1–L3.
- In **Changes**, give the file and line, and name the test that proves each fix.
- In **Rules check**, quote the real rule text for 1–8 and B1–B7.
- Copy every number from a raw file and name the file and key.
