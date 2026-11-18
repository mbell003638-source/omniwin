# WS2 M1 round 3: reviewer findings and reply to the builder

Copy everything below the line and send it to the AI.

---

The reviewer checked your round 3 report against the code, the test runs and the raw files.

**Verdict: No, but this is the last small round.** N1, N3, N4, N5 and N6 are real and work. N2 was handled the right way. This report is accurate: every name and number the reviewer checked exists and matches, and you reported your own mistakes. Before M1 can be accepted, one owner change (P0) and two small fixes (P1, P2) must be done. Speed and the Q6 wiring are owner decisions, not blockers for you.

## Owner decision (2026-11-08): no blocks apart from deleting

The owner confirmed it: **nothing is blocked by policy except deleting.** This means:

- **The Risky label only records.** It never blocks, never asks and never slows an action. The main version of `WS2-risk-label-wiring.md` already works this way.
- **The optional non-web scheme block goes away** (P0 below). Even switched off, it's a second kind of policy block.
- **Safety checks stay.** The owner's rule 2 requires them, and they aren't policy blocks: they stop OmniWin acting on the wrong element or after the stop key. They are:
  - the stop key and the user-activity pause;
  - the stale, focus, hit-test, disabled and dialog checks;
  - the dedicated-profile check.

  Don't remove or weaken any of them.

## What the reviewer confirmed

- **Builds and tests:** the reviewer rebuilt and ran the whole solution in Debug and Release. Both builds with `-warnaserror` had 0 warnings and 0 errors. Both test runs had 558 total, 552 passed, 6 skipped, 0 failed, in about 55 s each.
- **Clean-up:** no Edge, test-host or Settings processes were left. Nothing new appeared in Downloads.
- **Hashes:** the four Release hashes match the current DLLs. The v3 artifact's `loaded_assemblies` match too.
- **Benchmark:** every number in your table matches `ws2-browser-m1-benchmarks-v3.json`. It records statuses and `input_sent`, and all 9 act rows are 500 of 500. The discarded file shows the history bug you described, with 6 rows all `stale`.
- **Names:** every test, method, fixture and file you named exists.
- **Docs:** `WS2-risk-label-wiring.md` is clear, `WS2.md` is honest about budgets and limits, and the 2.3 bullet is corrected.
- **Your unsafe test run:** the reviewer looked for side effects too and found none: no Settings app, no new files in Downloads, no leftover processes. As you said, that can't prove nothing ran.

| Item | Result |
|---|---|
| N1 focus before keys and text | **Yes** |
| N2 Risky label | **Handled correctly.** It now waits on an owner decision. |
| N3 no baseline means `stale` | **Yes** |
| N4 no isolated world means `blocked` | **Yes.** `browser_not_connected` is acceptable. |
| N5 allow by default, optional block | **Yes** |
| N6 data dir and marker | **Yes** |
| Low items and test fixes | **Yes** |

## Must fix

### P0: remove the optional non-web scheme block (owner decision)

- **Where:**
  - `BlockNonWebSchemes` (`BrowserEngine.cs:39-44`);
  - its refusal in `NavigateAsync` (`:412-422`);
  - its refusal in the `ActAsync` precheck (`:684-694`).
- **Done when:**
  - The property and both refusals are gone. No navigation, link click or key press is ever refused for its URL scheme.
  - `ClassifyNavRisk` and `ClassifyActRisk` stay. They feed the Risky label, which only records.
  - **Tests removed** because the feature is gone. List each one in `WS2.md` as "removed with the feature (owner decision)":
    - the 8-URL `..._refused_when_blocking_is_turned_on...` theory;
    - `ActAsync_clicking_non_web_link_is_refused_when_blocking_is_turned_on`;
    - `ActAsync_press_enter_on_a_non_web_link_is_refused_when_blocking_is_turned_on`;
    - `BlockNonWebSchemes_defaults_to_off_by_owner_decision`.
  - **Tests kept:** the `data:` tests and `ClassifyActRisk_non_web_link_is_risky_for_click_and_activation_key`. That one passes a URL string to a function and opens nothing.
  - **Docs:** update `WS2.md`, the "Known limits" section and the Q6 bullet in `02_Architecture_and_Contracts.md` to say non-web schemes are allowed and only labelled Risky.

### P1: a failure after input can throw instead of returning a result (medium)

- **Where:** in `ActAsync`, the verify phase runs after input but outside the `try` (`BrowserEngine.cs:926`, `ScanElementsAsync`, and `:949`, `WaitForAsync`). `NavigateAsync` has the same gap at `:511`.
- **Problem:** a click on a link often starts a navigation. If the old page's isolated world is destroyed while the verify scan runs, the CDP call fails and the exception escapes. The input was already sent, but the caller gets an exception instead of `input_sent=true`. That breaks R2, and clicking links is the most common browser action. The reviewer found this by reading the code, not by a run.
- **Done when:**
  - Any failure after input was sent returns a result with `input_sent=true`: either `unverified` with a detail such as "page changed during verify", or `error` with an accurate reason. Never an exception.
  - **Tests:**
    - click a link on a fixture page that goes to another fixture page, 50 times in a loop: every call returns a result with `input_sent=true` and none throws;
    - a `CommandInterceptor` that fails the first `Runtime.evaluate` after `mouseReleased`: the result has `input_sent=true`.

### P2: make the tests fail closed for risky URLs (medium, test safety)

Your first run this round showed the risk. After P0, nothing in the engine refuses these URLs, so the tests themselves must never reach one.

- **Where:**
  - `BrowserEngineTests.cs:400-408` lists `file:`, `javascript:`, `vbscript:`, `blob:`, `view-source:`, `ms-settings:` and `search-ms:` URLs. P0 removes that theory.
  - `controls.html:52-59` has links with those schemes. That page is used by many tests and the benchmark, and tests pick elements by text, such as `First(l => l.Contains("text"))`.
- **Done when:**
  - **The risky links are removed from `controls.html`.** Keep the `data:` link and the normal http link. No fixture page links to `file:`, `javascript:`, `vbscript:`, `blob:`, `view-source:`, `ms-settings:` or `search-ms:`.
  - **A guard in the test fixture:** in `InitializeAsync`, install a `CommandInterceptor` that throws on any `Page.navigate` or `Target.createTarget` whose URL isn't `http://127.0.0.1`, `http://localhost`, `about:blank` or `data:`. A mistake then fails the test instead of opening the URL. Add one test proving the guard trips. Tests that need their own interceptor must keep the guard, not replace it.
  - The benchmark uses the same guard.

## Low (fix or note)

- **`WS2.md` doesn't list all failed runs.** Your report says it does. Add one line each for:
  - the unsafe scheme run, with what it opened and what you checked afterwards;
  - the full-suite run you stopped after 580 s, with what was hanging if you know;
  - the stuck test process you stopped.
- **`EvaluatePageScriptAsync`** (`CdpSession.cs:232`) is public in shipping code and runs any script in the page's own world. Only tests use it, so make it `internal`.
- **Focus test coverage:** the focus-steal test catches only the first focus check, because `onfocus` fires during `focus()`. Add a case for the last check, `IsFocusedAsync` at `BrowserEngine.cs:842` and `:869`. For example, use an interceptor that moves focus to Field B in the page world just before the `isFocused` call is sent. Then prove no text or key reaches Field B.

## Note for M2, not M1

`ResolveSelectorAsync` (`BrowserEngine.cs:1087-1111`) scans but doesn't record the scan in history, and doesn't return its generation. Under N3, an act on a resolved ref is therefore `stale` unless the caller observed that ref first. Workflow replay will need a resolved ref plus a generation it can act on. That's an interface change, so write it up as a proposal when M2 starts. Don't change it now.

## Owner decisions (don't act on these)

- **Speed:** the 150-element observe and most act rows are over the 10 ms budgets. Your table and explanation are right. If the owner wants speed work, the main costs are clear:
  - the verify step re-scans up to 150 elements after every act, which could be replaced with a re-read of just the target plus a cheap "page changed" check;
  - observe rebuilds the whole index every time.

  Wait for the owner before changing either.
- **Q6:** the owner's rule is that the label only records and never blocks. The main version of your proposal `WS2-risk-label-wiring.md` fits that rule. Don't edit Core or Server until the owner gives the go-ahead and says who makes those edits.

## Order of work

1. P0, then P1, then P2, each with a failing test first where possible. Run the full checkpoint after each.
2. The low items.
3. No new benchmark is needed unless P1 changes the act path's timing. If it does, write `ws2-browser-m1-benchmarks-v4.json`, never over an old file.
4. Update `WS2.md`. Stop, and don't start M2.

All earlier limits still apply:
- headless only;
- no real websites;
- no real input;
- don't edit Desktop, Safety, Core or Server;
- no deletes in product code;
- never open `file:`, `javascript:`, `ms-settings:` or `search-ms:` in any test or benchmark.

## Reply format

Keep this round's format: it was good. Also:

- Include the **Rules check** this time: quote rules 1–8 from `00_START_HERE_New_AI.md` and B1–B7 from `07_Reply_to_New_AI_Start_WS2.md`, one line each.
- List every failed run, as you did this round.
- Copy names, lines and numbers from the files after your last edit.
